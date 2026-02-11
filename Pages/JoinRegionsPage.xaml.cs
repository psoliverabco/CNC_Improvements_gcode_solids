using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CNC_Improvements_gcode_solids.Utilities;


namespace CNC_Improvements_gcode_solids.Pages
{
    public partial class JoinRegionsPage : Page
    {
        private enum ActiveKind { Turn, Mill }
        private enum EndSel { Start, End }
        private enum MotionMode { None, G1, G2, G3 }
        private enum SegType { Line, Arc }

        private struct Pt2
        {
            public double X;
            public double A2; // Z (turn) or Y (mill)
            public Pt2(double x, double a2) { X = x; A2 = a2; }
        }

        private sealed class SegInfo
        {
            public SegType Type;
            public int MotionLineIndex;
            public Pt2 Start;
            public Pt2 End;
        }

        private sealed class RegionInfo
        {
            public RegionSet Set = null!;
            public int ResStart;
            public int ResEnd;

            public int StartXIdx;
            public int StartA2Idx;
            public int EndXIdx;
            public int EndA2Idx;

            public int TrueStartIdx; // max(startXIdx,startA2Idx)
            public Pt2 StartPoint;

            public SegInfo? FirstSeg;
            public SegInfo? LastSeg;

            public Pt2 EndPoint
            {
                get
                {
                    if (LastSeg != null) return LastSeg.End;
                    return StartPoint;
                }
            }
        }

        private ActiveKind _kind;
        private RegionSet? _setA; // Fixed
        private RegionSet? _setB; // Edit

        // TurningPage snapshot keys (explicit)
        private const string TURN_STARTX = "__StartXLine";
        private const string TURN_STARTZ = "__StartZLine";
        private const string TURN_ENDX = "__EndXLine";
        private const string TURN_ENDZ = "__EndZLine";

        // MillPage snapshot keys (match the same pattern)
        //  private const string MILL_STARTX = "__StartXLine";
        //  private const string MILL_STARTY = "__StartYLine";
        //  private const string MILL_ENDX = "__EndXLine";
        //  private const string MILL_ENDY = "__EndYLine";

        private const string MILL_STARTX = "StartXLineText";
        private const string MILL_STARTY = "StartYLineText";
        private const string MILL_ENDX = "EndXLineText";
        private const string MILL_ENDY = "EndYLineText";





        public JoinRegionsPage()
        {
            InitializeComponent();

            BtnLoadA.Click += BtnLoadA_Click;
            BtnLoadB.Click += BtnLoadB_Click;
            BtnExecuteJoin.Click += BtnExecuteJoin_Click;

            DetectKindAndPopulateList();
        }

        private MainWindow GetMain()
        {
            return Application.Current.MainWindow as MainWindow
                   ?? throw new InvalidOperationException("MainWindow not available.");
        }

        // ------------------------------------------------------------
        // UI list / load
        // ------------------------------------------------------------
        private void DetectKindAndPopulateList()
        {
            var main = GetMain();

            // Use what the user was working on LAST (set selection drives _activeKind)
            if (main._activeKind == RegionSetKind.Turn)
            {
                _kind = ActiveKind.Turn;
                PopulateRegionList();
                EnableJoinUi(true);
                return;
            }

            if (main._activeKind == RegionSetKind.Mill)
            {
                _kind = ActiveKind.Mill;
                PopulateRegionList();
                EnableJoinUi(true);
                return;
            }

            // Drill: disabled (per your rule)
            LstRegions.Items.Clear();
            TxtRegionA.Text = "(none)";
            TxtRegionB.Text = "(none)";
            TxtJoinStatus.Text = "Join Regions is only available for TURN or MILL.\n(Drill page -> join disabled)";
            EnableJoinUi(false);
        }

        private void EnableJoinUi(bool enabled)
        {
            BtnLoadA.IsEnabled = enabled;
            BtnLoadB.IsEnabled = enabled;
            BtnExecuteJoin.IsEnabled = enabled;
            LstRegions.IsEnabled = enabled;

            if (!enabled)
            {
                _setA = null;
                _setB = null;
            }
        }

        private void PopulateRegionList()
        {
            var main = GetMain();

            LstRegions.Items.Clear();

            IList<RegionSet>? sets = (_kind == ActiveKind.Turn) ? main.TurnSets : main.MillSets;
            if (sets == null) return;

            for (int i = 0; i < sets.Count; i++)
            {
                var s = sets[i];
                if (s == null) continue;
                LstRegions.Items.Add(s.Name);
            }
        }

        private RegionSet? FindSetByName(string name)
        {
            var main = GetMain();
            IList<RegionSet>? sets = (_kind == ActiveKind.Turn) ? main.TurnSets : main.MillSets;
            if (sets == null) return null;

            for (int i = 0; i < sets.Count; i++)
            {
                var s = sets[i];
                if (s != null && string.Equals(s.Name, name, StringComparison.Ordinal))
                    return s;
            }
            return null;
        }

        private void BtnLoadA_Click(object sender, RoutedEventArgs e)
        {
            if (LstRegions.SelectedItem == null) return;
            string name = LstRegions.SelectedItem.ToString() ?? "";
            _setA = FindSetByName(name);
            TxtRegionA.Text = _setA != null ? _setA.Name : "(none)";
        }

        private void BtnLoadB_Click(object sender, RoutedEventArgs e)
        {
            if (LstRegions.SelectedItem == null) return;
            string name = LstRegions.SelectedItem.ToString() ?? "";
            _setB = FindSetByName(name);
            TxtRegionB.Text = _setB != null ? _setB.Name : "(none)";
        }

        // ------------------------------------------------------------
        // Execute
        // ------------------------------------------------------------
        private void BtnExecuteJoin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var main = GetMain();
                SyncGcodeLinesFromEditor_Local(main);

                if (_setA == null) throw new Exception("Load A (Fixed) first.");
                if (_setB == null) throw new Exception("Load B (Edit) first.");
                if (ReferenceEquals(_setA, _setB)) throw new Exception("A and B must be different regions.");

                // Radio selection => endpoint selection
                EndSel aSel;
                EndSel bSel;

                if (Rb_AStart_BStart.IsChecked == true) { aSel = EndSel.Start; bSel = EndSel.Start; }
                else if (Rb_AStart_BEnd.IsChecked == true) { aSel = EndSel.Start; bSel = EndSel.End; }
                else if (Rb_AEnd_BStart.IsChecked == true) { aSel = EndSel.End; bSel = EndSel.Start; }
                else { aSel = EndSel.End; bSel = EndSel.End; }

                char axis2 = (_kind == ActiveKind.Turn) ? 'Z' : 'Y';

                // Resolve A and B using the SAME contract as TurningPage/MillPage:
                // - RegionLines match -> ResolvedStart/End
                // - Marker texts searched IN RANGE first
                RegionInfo aInfo = ResolveRegionInfo(_setA, main.GcodeLines, axis2);
                RegionInfo bInfo = ResolveRegionInfo(_setB, main.GcodeLines, axis2);

                Pt2 joinPoint = (aSel == EndSel.Start) ? aInfo.StartPoint : aInfo.EndPoint;

                int edits = ApplyJoinRules(main.GcodeLines, bInfo, axis2, bSel, joinPoint);

                // Write model -> editor (plain). Turning/Mill pages will re-render numbering when activated.
                WriteEditorFromLinesPlain(main);

                // Rebuild B.RegionLines from its updated markers (same as selection behavior)
                RebuildRegionLinesFromMarkers(_setB, main.GcodeLines, axis2);

                // Re-resolve status
                ResolveAndUpdateStatus(_setB, main.GcodeLines);

                TxtJoinStatus.Text = $"JOIN OK\nEdits: {edits}\nKind: {_kind}\nB: {_setB.Name}";
            }
            catch (Exception ex)
            {
                TxtJoinStatus.Text = "JOIN FAILED:\n" + ex.Message;
                MessageBox.Show(ex.Message, "Join Regions", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------
        // RULE IMPLEMENTATION (your line/arc start/end rules)
        // ------------------------------------------------------------
        private int ApplyJoinRules(
            List<string> allLines,
            RegionInfo bInfo,
            char axis2,
            EndSel editEnd,
            Pt2 joinPoint)
        {
            if (editEnd == EndSel.Start)
            {
                // RULE 1:
                // - If first valid motion is LINE: replace start markers with ONE G1 join line (single line).
                // - If ARC: keep arc; insert join line + keeper line immediately above arc.
                if (bInfo.FirstSeg == null || bInfo.FirstSeg.Type == SegType.Line)
                    return EditStart_Line(allLines, bInfo, axis2, joinPoint);
                else
                    return EditStart_Arc(allLines, bInfo, axis2, joinPoint);
            }
            else
            {
                // RULE 2:
                // - If last valid motion is LINE: replace end markers with ONE G1 join line (single line).
                // - If ARC: keep arc; insert ONE join line AFTER arc; do not break arc line.
                if (bInfo.LastSeg == null || bInfo.LastSeg.Type == SegType.Line)
                    return EditEnd_Line(allLines, bInfo, axis2, joinPoint);
                else
                    return EditEnd_Arc(allLines, bInfo, axis2, joinPoint);
            }
        }

        private int EditStart_Line(List<string> lines, RegionInfo b, char axis2, Pt2 join)
        {
            // Remove both start marker lines, insert one combined join line at the earliest marker index.
            int edits = 0;

            int insertAt = Math.Min(b.StartXIdx, b.StartA2Idx);
            if (insertAt < b.ResStart || insertAt > b.ResEnd)
                throw new Exception("B start markers are not inside resolved region.");

            // Remove start marker lines in descending order to keep indices valid
            var remove = new List<int>();
            remove.Add(b.StartXIdx);
            if (b.StartA2Idx != b.StartXIdx) remove.Add(b.StartA2Idx);
            remove = remove.Distinct().OrderByDescending(x => x).ToList();

            for (int i = 0; i < remove.Count; i++)
            {
                int idx = remove[i];
                if (idx < 0 || idx >= lines.Count) continue;
                lines.RemoveAt(idx);
                edits++;

                if (idx < insertAt) insertAt--;
                if (idx < b.ResEnd) b.ResEnd--;
                if (idx < b.ResStart) b.ResStart--;
            }

            string newLine = BuildTaggedJoinLine(join.X, join.A2, axis2, "START", b.Set.Name);
            lines.Insert(insertAt, newLine);
            edits++;

            // Update snapshot markers to point to this new single line
            SetMarkerText(b.Set, MarkerKeyStartX(axis2), NormalizeLineForMatch(newLine));
            SetMarkerText(b.Set, MarkerKeyStartA2(axis2), NormalizeLineForMatch(newLine));

            // After insertion, region end shifts if insertion is within region
            if (insertAt <= b.ResEnd) b.ResEnd++;

            return edits;
        }

        private int EditStart_Arc(List<string> lines, RegionInfo b, char axis2, Pt2 join)
        {
            if (b.FirstSeg == null)
                throw new Exception("B has no first motion segment (cannot apply arc-start rule).");

            int edits = 0;

            int arcLineIndex = b.FirstSeg.MotionLineIndex;

            // Remove start markers (but NEVER delete the arc line itself)
            var remove = new List<int>();
            if (b.StartXIdx != arcLineIndex) remove.Add(b.StartXIdx);
            if (b.StartA2Idx != arcLineIndex) remove.Add(b.StartA2Idx);
            remove = remove.Distinct().OrderByDescending(x => x).ToList();

            for (int i = 0; i < remove.Count; i++)
            {
                int idx = remove[i];
                if (idx < 0 || idx >= lines.Count) continue;
                lines.RemoveAt(idx);
                edits++;

                if (idx < arcLineIndex) arcLineIndex--;
                if (idx < b.ResEnd) b.ResEnd--;
                if (idx < b.ResStart) b.ResStart--;
            }

            // Insert immediately ABOVE the arc:
            //  1) JOIN point line (START tag)
            //  2) Keeper line with original arc start point (single line)
            string joinLine = BuildTaggedJoinLine(join.X, join.A2, axis2, "START", b.Set.Name);
            string keepLine = BuildPlainCoordLine(b.StartPoint.X, b.StartPoint.A2, axis2);

            if (arcLineIndex < 0 || arcLineIndex > lines.Count)
                throw new Exception("Internal error: arc insertion index invalid.");

            lines.Insert(arcLineIndex, joinLine);
            edits++;
            lines.Insert(arcLineIndex + 1, keepLine);
            edits++;

            // Update snapshot markers to JOIN line (not keeper)
            SetMarkerText(b.Set, MarkerKeyStartX(axis2), NormalizeLineForMatch(joinLine));
            SetMarkerText(b.Set, MarkerKeyStartA2(axis2), NormalizeLineForMatch(joinLine));

            // Region end shifts because we inserted within region
            if (arcLineIndex <= b.ResEnd) b.ResEnd += 2;

            return edits;
        }

        private int EditEnd_Line(List<string> lines, RegionInfo b, char axis2, Pt2 join)
        {
            // Remove both end marker lines, insert one combined join line at earliest end marker index.
            int edits = 0;

            int insertAt = Math.Min(b.EndXIdx, b.EndA2Idx);
            if (insertAt < b.ResStart || insertAt > b.ResEnd)
                throw new Exception("B end markers are not inside resolved region.");

            var remove = new List<int>();
            remove.Add(b.EndXIdx);
            if (b.EndA2Idx != b.EndXIdx) remove.Add(b.EndA2Idx);
            remove = remove.Distinct().OrderByDescending(x => x).ToList();

            for (int i = 0; i < remove.Count; i++)
            {
                int idx = remove[i];
                if (idx < 0 || idx >= lines.Count) continue;
                lines.RemoveAt(idx);
                edits++;

                if (idx < insertAt) insertAt--;
                if (idx < b.ResEnd) b.ResEnd--;
                if (idx < b.ResStart) b.ResStart--;
            }

            string newLine = BuildTaggedJoinLine(join.X, join.A2, axis2, "END", b.Set.Name);
            lines.Insert(insertAt, newLine);
            edits++;

            // Update snapshot end markers to this single line
            SetMarkerText(b.Set, MarkerKeyEndX(axis2), NormalizeLineForMatch(newLine));
            SetMarkerText(b.Set, MarkerKeyEndA2(axis2), NormalizeLineForMatch(newLine));

            if (insertAt <= b.ResEnd) b.ResEnd++;

            return edits;
        }

        private int EditEnd_Arc(List<string> lines, RegionInfo b, char axis2, Pt2 join)
        {
            if (b.LastSeg == null)
                throw new Exception("B has no last motion segment (cannot apply arc-end rule).");

            int edits = 0;

            int arcLineIndex = b.LastSeg.MotionLineIndex;

            // Remove end markers ONLY if they are NOT the arc line (keep arc intact)
            var remove = new List<int>();
            if (b.EndXIdx != arcLineIndex) remove.Add(b.EndXIdx);
            if (b.EndA2Idx != arcLineIndex) remove.Add(b.EndA2Idx);
            remove = remove.Distinct().OrderByDescending(x => x).ToList();

            for (int i = 0; i < remove.Count; i++)
            {
                int idx = remove[i];
                if (idx < 0 || idx >= lines.Count) continue;
                lines.RemoveAt(idx);
                edits++;

                if (idx < arcLineIndex) arcLineIndex--;
                if (idx < b.ResEnd) b.ResEnd--;
                if (idx < b.ResStart) b.ResStart--;
            }

            // Insert ONE join line immediately AFTER the arc
            int insertAt = arcLineIndex + 1;
            if (insertAt < 0) insertAt = 0;
            if (insertAt > lines.Count) insertAt = lines.Count;

            string newLine = BuildTaggedJoinLine(join.X, join.A2, axis2, "END", b.Set.Name);
            lines.Insert(insertAt, newLine);
            edits++;

            // Update snapshot end markers to JOIN line
            SetMarkerText(b.Set, MarkerKeyEndX(axis2), NormalizeLineForMatch(newLine));
            SetMarkerText(b.Set, MarkerKeyEndA2(axis2), NormalizeLineForMatch(newLine));

            // Region end shifts by +1 if insertion inside region (it is)
            if (insertAt <= b.ResEnd) b.ResEnd++;

            return edits;
        }

        // ------------------------------------------------------------
        // Resolve region contract (match TurningPage logic)
        // ------------------------------------------------------------
        private RegionInfo ResolveRegionInfo(RegionSet set, List<string> allLines, char axis2)
        {
            if (set == null) throw new Exception("RegionSet was null.");

            ResolveAndUpdateStatus(set, allLines);

            if (set.Status != RegionResolveStatus.Ok || !set.ResolvedStartLine.HasValue || !set.ResolvedEndLine.HasValue)
                throw new Exception($"Set '{set.Name}' is not resolved (Status: {set.Status}).");

            int resStart = set.ResolvedStartLine.Value;
            int resEnd = set.ResolvedEndLine.Value;

            // Marker texts from snapshot (TURN explicit keys; MILL explicit keys)
            string sxText = GetMarkerText(set, MarkerKeyStartX(axis2));
            string sa2Text = GetMarkerText(set, MarkerKeyStartA2(axis2));
            string exText = GetMarkerText(set, MarkerKeyEndX(axis2));
            string ea2Text = GetMarkerText(set, MarkerKeyEndA2(axis2));

            // Find marker indices IN RANGE first (same as TurningPage ApplyTurnSet)
            int sx = FindIndexByMarkerTextInRange(allLines, sxText, resStart, resEnd);
            int sa2 = FindIndexByMarkerTextInRange(allLines, sa2Text, resStart, resEnd);
            int ex = FindIndexByMarkerTextInRange(allLines, exText, resStart, resEnd);
            int ea2 = FindIndexByMarkerTextInRange(allLines, ea2Text, resStart, resEnd);

            // fallback global
            if (sx < 0) sx = FindIndexByMarkerText(allLines, sxText);
            if (sa2 < 0) sa2 = FindIndexByMarkerText(allLines, sa2Text);
            if (ex < 0) ex = FindIndexByMarkerText(allLines, exText);
            if (ea2 < 0) ea2 = FindIndexByMarkerText(allLines, ea2Text);

            if (sx < 0) throw new Exception($"Set '{set.Name}': StartX marker not found in editor.");
            if (sa2 < 0) throw new Exception($"Set '{set.Name}': Start{axis2} marker not found in editor.");
            if (ex < 0) throw new Exception($"Set '{set.Name}': EndX marker not found in editor.");
            if (ea2 < 0) throw new Exception($"Set '{set.Name}': End{axis2} marker not found in editor.");

            if (!TryGetCoord(allLines[sx], 'X', out double startX))
                throw new Exception($"Set '{set.Name}': StartX marker line has no X.");
            if (!TryGetCoord(allLines[sa2], axis2, out double startA2))
                throw new Exception($"Set '{set.Name}': Start{axis2} marker line has no {axis2}.");

            var info = new RegionInfo
            {
                Set = set,
                ResStart = resStart,
                ResEnd = resEnd,
                StartXIdx = sx,
                StartA2Idx = sa2,
                EndXIdx = ex,
                EndA2Idx = ea2,
                TrueStartIdx = Math.Max(sx, sa2),
                StartPoint = new Pt2(startX, startA2),
                FirstSeg = null,
                LastSeg = null
            };

            ScanSegmentsWithinResolvedRange(allLines, info, axis2);

            return info;
        }

        private void ScanSegmentsWithinResolvedRange(List<string> allLines, RegionInfo info, char axis2)
        {
            double lastX = info.StartPoint.X;
            double lastA2 = info.StartPoint.A2;

            MotionMode mode = MotionMode.None;

            // Determine modal motion mode before region start (best effort)
            for (int i = info.ResStart - 1; i >= 0; i--)
            {
                if (TryGetLastMotionGCode(allLines[i], out int g))
                {
                    if (g == 0 || g == 1) mode = MotionMode.G1;
                    else if (g == 2) mode = MotionMode.G2;
                    else if (g == 3) mode = MotionMode.G3;
                    break;
                }
            }

            bool started = false;

            for (int i = info.ResStart; i <= info.ResEnd; i++)
            {
                string raw = allLines[i] ?? "";
                string line = raw.ToUpperInvariant();

                // modal updates
                if (line.Contains("G1") || line.Contains("G01") || line.Contains("G0") || line.Contains("G00"))
                    mode = MotionMode.G1;
                if (line.Contains("G2") || line.Contains("G02"))
                    mode = MotionMode.G2;
                if (line.Contains("G3") || line.Contains("G03"))
                    mode = MotionMode.G3;

                // Only start producing geometry once we reach the "true start" line (max of start markers),
                // same intent as TurningPage BuildGeometryFromGcode_AbsoluteRange
                if (!started)
                {
                    if (i != info.TrueStartIdx)
                        continue;
                    started = true;
                }

                bool hasX = TryGetCoord(line, 'X', out double newX);
                bool hasA2 = TryGetCoord(line, axis2, out double newA2);

                if (!hasX) newX = lastX;
                if (!hasA2) newA2 = lastA2;

                if (mode == MotionMode.None)
                {
                    lastX = newX;
                    lastA2 = newA2;
                    continue;
                }

                if (!hasX && !hasA2)
                {
                    lastX = newX;
                    lastA2 = newA2;
                    continue;
                }

                if (newX == lastX && newA2 == lastA2)
                {
                    lastX = newX;
                    lastA2 = newA2;
                    continue;
                }

                var seg = new SegInfo
                {
                    MotionLineIndex = i,
                    Start = new Pt2(lastX, lastA2),
                    End = new Pt2(newX, newA2),
                    Type = (mode == MotionMode.G2 || mode == MotionMode.G3) ? SegType.Arc : SegType.Line
                };

                if (info.FirstSeg == null) info.FirstSeg = seg;
                info.LastSeg = seg;

                lastX = newX;
                lastA2 = newA2;
            }
        }

        // ------------------------------------------------------------
        // RegionLines rebuild (selection-equivalent: slice from marker min..max)
        // ------------------------------------------------------------
        private void RebuildRegionLinesFromMarkers(RegionSet set, List<string> allLines, char axis2)
        {
            string sxText = GetMarkerText(set, MarkerKeyStartX(axis2));
            string sa2Text = GetMarkerText(set, MarkerKeyStartA2(axis2));
            string exText = GetMarkerText(set, MarkerKeyEndX(axis2));
            string ea2Text = GetMarkerText(set, MarkerKeyEndA2(axis2));

            int sx = FindIndexByMarkerText(allLines, sxText);
            int sa2 = FindIndexByMarkerText(allLines, sa2Text);
            int ex = FindIndexByMarkerText(allLines, exText);
            int ea2 = FindIndexByMarkerText(allLines, ea2Text);

            if (sx < 0 || sa2 < 0 || ex < 0 || ea2 < 0)
                return;

            int start = Math.Min(sx, sa2);
            int end = Math.Max(ex, ea2);

            if (start < 0 || end < start || end >= allLines.Count)
                return;

            if (set.RegionLines == null)
                return;

            set.RegionLines.Clear();
            for (int i = start; i <= end; i++)
                set.RegionLines.Add(allLines[i] ?? "");
        }

        // ------------------------------------------------------------
        // Snapshot helpers (UiStateSnapshot) - same pattern as TurningPage
        // ------------------------------------------------------------
        private static UiStateSnapshot EnsureSnapshot(RegionSet set)
        {
            set.PageSnapshot ??= new UiStateSnapshot();
            return set.PageSnapshot;
        }

        private static string GetSnap(RegionSet set, string key, string def)
        {
            if (set.PageSnapshot?.Values != null && set.PageSnapshot.Values.TryGetValue(key, out string v))
                return v ?? def;
            return def;
        }

        private static void SetSnap(RegionSet set, string key, string value)
        {
            var snap = EnsureSnapshot(set);
            snap.Values[key] = value ?? "";
        }

        private string GetMarkerText(RegionSet set, string key)
        {
            // Stored already normalized by Turning/Mill pages
            return GetSnap(set, key, "");
        }

        private void SetMarkerText(RegionSet set, string key, string normalizedLine)
        {
            SetSnap(set, key, normalizedLine);
        }

        private string MarkerKeyStartX(char axis2)
        {
            // Same key name for turn/mill X start
            return (_kind == ActiveKind.Turn) ? TURN_STARTX : MILL_STARTX;
        }

        private string MarkerKeyEndX(char axis2)
        {
            return (_kind == ActiveKind.Turn) ? TURN_ENDX : MILL_ENDX;
        }

        private string MarkerKeyStartA2(char axis2)
        {
            if (_kind == ActiveKind.Turn) return TURN_STARTZ;
            return MILL_STARTY;
        }

        private string MarkerKeyEndA2(char axis2)
        {
            if (_kind == ActiveKind.Turn) return TURN_ENDZ;
            return MILL_ENDY;
        }

        // ------------------------------------------------------------
        // Region resolve (copied from TurningPage logic)
        // ------------------------------------------------------------
        private static void ResolveAndUpdateStatus(RegionSet set, List<string> allLines)
        {
            if (set.RegionLines == null || set.RegionLines.Count == 0)
            {
                set.Status = RegionResolveStatus.Unset;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            var matches = FindRegionMatches(allLines, set.RegionLines);

            if (matches.Count == 0)
            {
                set.Status = RegionResolveStatus.Missing;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            if (matches.Count > 1)
            {
                set.Status = RegionResolveStatus.Ambiguous;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            set.Status = RegionResolveStatus.Ok;
            set.ResolvedStartLine = matches[0].start;
            set.ResolvedEndLine = matches[0].end;
        }

        private static List<(int start, int end)> FindRegionMatches(List<string> allLines, System.Collections.ObjectModel.ObservableCollection<string> regionLines)
        {
            var matches = new List<(int start, int end)>();

            if (allLines == null || allLines.Count == 0) return matches;
            if (regionLines == null || regionLines.Count == 0) return matches;

            int n = regionLines.Count;
            if (n > allLines.Count) return matches;

            var needle = new string[n];
            for (int i = 0; i < n; i++)
                needle[i] = NormalizeLineForMatch(regionLines[i]);

            for (int start = 0; start <= allLines.Count - n; start++)
            {
                bool ok = true;
                for (int j = 0; j < n; j++)
                {
                    if (NormalizeLineForMatch(allLines[start + j]) != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok) matches.Add((start, start + n - 1));
            }

            return matches;
        }

        // ------------------------------------------------------------
        // Text matching + parsing
        // ------------------------------------------------------------
        private static string NormalizeLineForMatch(string? s)
        {
            // MUST match the SAME normalization used by the Region builders/search:
            // - strips optional "1234:" prefix
            // - strips leading "#...#" anchor block
            // - removes ALL whitespace
            // - uppercases invariant
            // - keeps the unique end-tag intact
            return GeneralNormalizers.NormalizeTextLineToGcodeAndEndTag(s ?? "");
        }


        private static int FindIndexByMarkerText(List<string> lines, string markerText)
        {
            string key = NormalizeLineForMatch(markerText);
            if (string.IsNullOrWhiteSpace(key)) return -1;

            for (int i = 0; i < lines.Count; i++)
            {
                if (NormalizeLineForMatch(lines[i]) == key)
                    return i;
            }
            return -1;
        }

        private static int FindIndexByMarkerTextInRange(List<string> lines, string markerText, int start, int end)
        {
            string key = NormalizeLineForMatch(markerText);
            if (string.IsNullOrWhiteSpace(key)) return -1;

            if (lines == null || lines.Count == 0) return -1;
            if (start < 0 || end < 0 || start >= lines.Count || end >= lines.Count || end < start) return -1;

            for (int i = start; i <= end; i++)
            {
                if (NormalizeLineForMatch(lines[i]) == key)
                    return i;
            }
            return -1;
        }

        private static bool TryGetCoord(string line, char axis, out double value)
        {
            value = double.NaN;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            string s = line.ToUpperInvariant();
            int idx = 0;

            while (true)
            {
                idx = s.IndexOf(axis, idx);
                if (idx < 0)
                    return false;

                int start = idx + 1;
                if (start >= s.Length)
                    return false;

                int end = start;
                while (end < s.Length)
                {
                    char c = s[end];
                    if (char.IsDigit(c) || c == '+' || c == '-' || c == '.')
                        end++;
                    else
                        break;
                }

                if (end > start)
                {
                    string numStr = s.Substring(start, end - start);
                    if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        return true;
                }

                idx = end;
            }
        }

        private static bool TryGetLastMotionGCode(string line, out int gMotion)
        {
            gMotion = -1;
            if (string.IsNullOrWhiteSpace(line)) return false;

            string s = line.ToUpperInvariant();
            int idx = 0;

            while (true)
            {
                idx = s.IndexOf('G', idx);
                if (idx < 0) break;

                int start = idx + 1;
                int end = start;
                while (end < s.Length && char.IsDigit(s[end])) end++;

                if (end > start)
                {
                    string numStr = s.Substring(start, end - start);
                    if (int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int g))
                    {
                        if (g == 0 || g == 1 || g == 2 || g == 3)
                            gMotion = g;
                    }
                }

                idx = end;
            }

            return gMotion >= 0;
        }

        // ------------------------------------------------------------
        // Join line formatting
        // ------------------------------------------------------------
        private static string BuildPlainCoordLine(double x, double a2, char axis2)
        {
            string sx = x.ToString("0.###############", CultureInfo.InvariantCulture);
            string sa2 = a2.ToString("0.###############", CultureInfo.InvariantCulture);
            return $"G1 X{sx} {axis2}{sa2}";
        }

        private static string BuildTaggedJoinLine(double x, double a2, char axis2, string tag, string regionName)
        {
            string sx = x.ToString("0.###############", CultureInfo.InvariantCulture);
            string sa2 = a2.ToString("0.###############", CultureInfo.InvariantCulture);
            return $"G1 X{sx} {axis2}{sa2}     ({tag} OF \"{regionName}\")";
        }

        // ------------------------------------------------------------
        // Editor sync (local copy; avoids calling private MainWindow method)
        // ------------------------------------------------------------
        private void SyncGcodeLinesFromEditor_Local(MainWindow main)
        {
            var lines = main.GcodeLines;
            var rtb = main.GcodeEditor;

            if (lines == null) throw new Exception("MainWindow.GcodeLines not available.");
            if (rtb == null) throw new Exception("MainWindow.GcodeEditor not available.");

            lines.Clear();

            TextRange tr = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
            string allText = tr.Text.Replace("\r\n", "\n");

            using (StringReader reader = new StringReader(allText))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line;

                    int colonIndex = trimmed.IndexOf(':');
                    if (colonIndex >= 0 && colonIndex < 10)
                        trimmed = trimmed.Substring(colonIndex + 1).TrimStart();

                    lines.Add(trimmed);
                }
            }
        }

        private void WriteEditorFromLinesPlain(MainWindow main)
        {
            var rtb = main.GcodeEditor;
            if (rtb == null) return;

            string text = string.Join(Environment.NewLine, main.GcodeLines);
            TextRange tr = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
            tr.Text = text;
        }
    }
}
