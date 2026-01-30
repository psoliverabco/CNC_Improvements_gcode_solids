using CNC_Improvements_gcode_solids.FreeCadIntegration;
using CNC_Improvements_gcode_solids.SetManagement;
using CNC_Improvements_gcode_solids.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;





namespace CNC_Improvements_gcode_solids.Pages
{
    public partial class DrillPage : Page, IGcodePage

    {
        // Access shared model from MainWindow (same pattern as TurningPage)
        private MainWindow Main => (MainWindow)Application.Current.MainWindow;
        private RichTextBox GcodeEditor => Main.GcodeEditor;
        private List<string> GcodeLines => Main.GcodeLines;

        // Drill Z selection (apex)
        private int drillDepthLineIndex = -1;
        private double _drillDepthZ = double.NaN;

        // Index of the currently clicked/selected line in the editor
        private int selectedLineIndex = -1;

        // Hole selection mode: when true, clicking G-code lines adds holes
        private bool holeSelectionActive = false;

        // Hole data for highlighting


        // ============================================================
        // NEW: Persist/restore Drill "set" state (like MillPage)
        // ============================================================
        private bool _isApplyingDrillSet = false;

        // Snapshot keys
        private const string KEY_DRILL_DEPTH_TEXT = "DrillDepthLineText";
        private const string KEY_HOLE_DIA = "TxtHoleDia";
        private const string KEY_Z_HOLE_TOP = "TxtZHoleTop";
        private const string KEY_POINT_ANGLE = "TxtPointAngle";
        private const string KEY_CHAMFER = "TxtChamfer";
        private const string KEY_Z_PLUS_EXT = "TxtZPlusExt";
        private const string KEY_COORD_MODE = "CoordMode"; // "Cartesian" or "Polar"
        private const string KEY_REGION_UID = "__RegionUid";

        private RegionSet? _lastAppliedDrillSetRef = null;

        private RegionSet? GetSelectedDrillSetSafe()
        {
            // Assumes MainWindow has SelectedDrillSet (same pattern as SelectedMillSet)
            return Main.SelectedDrillSet;
        }

       

        private static string GetSnapshotOrDefault(RegionSet set, string key, string def)
        {
            if (set.PageSnapshot?.Values == null)
                return def;

            if (set.PageSnapshot.Values.TryGetValue(key, out string? v))
            {
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return def;
        }

        private static void EnsureSnapshot(RegionSet set)
        {
            set.PageSnapshot ??= new UiStateSnapshot();
        }

        private static Dictionary<string, List<int>> BuildNormalizedLineIndexMap(List<string> allLines)
        {
            var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < allLines.Count; i++)
            {
                string key = TextSearching.NormalizeTextLineToGcodeAndEndTag(allLines[i] ?? "");
                if (!map.TryGetValue(key, out var lst))
                {
                    lst = new List<int>();
                    map[key] = lst;
                }
                lst.Add(i);
            }
            return map;
        }




        private static string EnsureRegionUid(RegionSet set)
        {
            EnsureSnapshot(set);

            if (set.PageSnapshot == null)
                throw new InvalidOperationException("PageSnapshot was null after EnsureSnapshot().");

            if (!set.PageSnapshot.Values.TryGetValue(KEY_REGION_UID, out string? uid) || string.IsNullOrWhiteSpace(uid))
            {
                uid = Guid.NewGuid().ToString("N");
                set.PageSnapshot.Values[KEY_REGION_UID] = uid;
            }

            return uid;
        }

        private static string MakeAnchorToken(string uid, int oneBasedN, string rawLine)
        {
            // FINAL STORED FORM (example):
            // #d99a0089f4bf4dacbffebc87dff04c1d,1#G1X334.6576Y-2.4477F0.2(U:A0000)
            return $"#{uid},{oneBasedN}#" + TextSearching.NormalizeTextLineAsIs(rawLine ?? "");
        }








        private class HolePoint
        {
            public int LineIndex;
            public double X;
            public double Y;
        }

        private readonly List<HolePoint> holePoints = new();

        // Last Cartesian hole (for missing X/Y carry-over)
        private bool _hasLastHoleCart = false;
        private double _lastHoleX = 0.0;
        private double _lastHoleY = 0.0;

        // Last Polar hole (for missing X/C carry-over)
        private bool _hasLastHolePolar = false;
        private double _lastPolarDiam = 0.0;     // diameter from X
        private double _lastPolarAngleDeg = 0.0; // angle from C

        public DrillPage()
        {
            InitializeComponent();

            Loaded += DrillPage_Loaded;
            Unloaded += DrillPage_Unloaded;

            // Persist params automatically when user edits them
            if (TxtHoleDia != null) TxtHoleDia.TextChanged += (_, __) => StoreSelectionIntoSelectedSet();
            if (TxtZHoleTop != null) TxtZHoleTop.TextChanged += (_, __) => StoreSelectionIntoSelectedSet();
            if (TxtPointAngle != null) TxtPointAngle.TextChanged += (_, __) => StoreSelectionIntoSelectedSet();
            if (TxtChamfer != null) TxtChamfer.TextChanged += (_, __) => StoreSelectionIntoSelectedSet();
            if (TxtZPlusExt != null) TxtZPlusExt.TextChanged += (_, __) => StoreSelectionIntoSelectedSet();

            if (RadCartesian != null) RadCartesian.Checked += (_, __) => StoreSelectionIntoSelectedSet();
            if (RadPolar != null) RadPolar.Checked += (_, __) => StoreSelectionIntoSelectedSet();
        }





        private void DrillPage_Loaded(object sender, RoutedEventArgs e)
        {
            // If MainWindow supports change notifications, react to SelectedDrillSet changes.
            if (Main is INotifyPropertyChanged npc)
                npc.PropertyChanged += MainWindow_PropertyChanged;

            // Apply current selection once when the page loads.
            TryApplySelectedDrillSetIfChanged(force: true);
        }

        private void DrillPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (Main is INotifyPropertyChanged npc)
                npc.PropertyChanged -= MainWindow_PropertyChanged;
        }

        private void MainWindow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // IMPORTANT: this string must match the property name in MainWindow.
            // If your property is actually named differently, change this string.
            if (e.PropertyName == "SelectedDrillSet")
                TryApplySelectedDrillSetIfChanged(force: false);
        }

        private void TryApplySelectedDrillSetIfChanged(bool force)
        {
            var set = GetSelectedDrillSetSafe();
            if (set == null)
                return;

            if (!force && ReferenceEquals(set, _lastAppliedDrillSetRef))
                return;

            _lastAppliedDrillSetRef = set;
            ApplyDrillSet(set);
        }


        private static string MakeLineToken(int lineIndex0, List<string> allLines)
        {
            // Store as 1-based line number for readability / stability across typical edits.
            int lineNo1 = lineIndex0 + 1;

            string txt = (lineIndex0 >= 0 && lineIndex0 < allLines.Count)
     ? TextSearching.NormalizeTextLineToGcodeAndEndTag(allLines[lineIndex0] ?? "")
     : "";


            return $"L{lineNo1}:{txt}";
        }

        private static bool TryParseLineToken(string token, out int lineIndex0, out string textPart)
        {
            lineIndex0 = -1;
            textPart = "";

            if (string.IsNullOrWhiteSpace(token))
                return false;

            // Token format: L123:rest...
            if (!token.StartsWith("L", StringComparison.OrdinalIgnoreCase))
            {
                // Old format: just text
                textPart = token;
                return true;
            }

            int colon = token.IndexOf(':');
            if (colon < 0)
            {
                // Treat as old format
                textPart = token;
                return true;
            }

            string num = token.Substring(1, colon - 1).Trim();
            if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineNo1) && lineNo1 > 0)
                lineIndex0 = lineNo1 - 1;

            textPart = token[(colon + 1)..];
            return true;
        }

        private int ResolveIndexFromToken(string token, List<string> allLines)
        {
            // Returns:
            //  >=0 : resolved index
            //  -1  : missing
            //  -2  : ambiguous (only possible for old-format tokens with duplicates)

            TryParseLineToken(token ?? "", out int hintedIdx0, out string textPart);

            string key = TextSearching.NormalizeTextLineToGcodeAndEndTag(textPart ?? "");
            if (string.IsNullOrWhiteSpace(key))
                return -1;

            // Build hits list
            var hits = new List<int>();
            for (int i = 0; i < allLines.Count; i++)
            {
                if (TextSearching.NormalizeTextLineToGcodeAndEndTag(allLines[i] ?? "") == key)
                    hits.Add(i);
            }


            if (hits.Count == 0)
                return -1;

            if (hits.Count == 1)
                return hits[0];

            // If we have a line-number hint, pick the closest match to that hint.
            if (hintedIdx0 >= 0)
            {
                int best = hits[0];
                int bestDist = Math.Abs(best - hintedIdx0);
                for (int i = 1; i < hits.Count; i++)
                {
                    int d = Math.Abs(hits[i] - hintedIdx0);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = hits[i];
                    }
                }
                return best;
            }

            // Old format + duplicates => ambiguous
            return -2;
        }



        public void ApplyDrillSet(RegionSet? set)
        {
            if (set == null)
            {
                RefreshHighlighting();
                return;
            }

            // Always match against latest edits before searching
            SyncGcodeLinesFromEditor();

            _isApplyingDrillSet = true;
            try
            {
                EnsureSnapshot(set);

                // Restore params
                TxtHoleDia.Text = GetSnapshotOrDefault(set, KEY_HOLE_DIA, "");
                TxtZHoleTop.Text = GetSnapshotOrDefault(set, KEY_Z_HOLE_TOP, "");
                TxtPointAngle.Text = GetSnapshotOrDefault(set, KEY_POINT_ANGLE, "");
                TxtChamfer.Text = GetSnapshotOrDefault(set, KEY_CHAMFER, "");
                TxtZPlusExt.Text = GetSnapshotOrDefault(set, KEY_Z_PLUS_EXT, "");

                // Restore coord mode
                string mode = GetSnapshotOrDefault(set, KEY_COORD_MODE, "Cartesian");
                if (mode.Equals("Polar", StringComparison.OrdinalIgnoreCase))
                {
                    RadPolar.IsChecked = true;
                    RadCartesian.IsChecked = false;
                }
                else
                {
                    RadCartesian.IsChecked = true;
                    RadPolar.IsChecked = false;
                }

                // Resolve status against current editor
                ResolveDrillAndUpdateStatus(set, GcodeLines);

                // -----------------------------
                // Depth (stored as #uid,n# + normalized text)
                // -----------------------------
                string depthTok = GetSnapshotOrDefault(set, KEY_DRILL_DEPTH_TEXT, "");

                drillDepthLineIndex = -1;
                _drillDepthZ = double.NaN;
                BtnDrillDepthLine.Content = "(none)";

                if (!string.IsNullOrWhiteSpace(depthTok))
                {
                    int depthIdx = TextSearching.FindSingleLine(GcodeLines, depthTok, -1, -1, preferLast: false);

                    if (depthIdx >= 0 && depthIdx < GcodeLines.Count && TryGetCoord(GcodeLines[depthIdx], 'Z', out double zVal))
                    {
                        drillDepthLineIndex = depthIdx;
                        _drillDepthZ = zVal;
                        BtnDrillDepthLine.Content = $"Drill Depth ={_drillDepthZ.ToString("0.###", CultureInfo.InvariantCulture)}";
                    }
                }

                // -----------------------------
                // Holes (stored in set.RegionLines as #uid,1#, #uid,2#, ...)
                // -----------------------------
                holePoints.Clear();
                if (LstHoleLines != null) LstHoleLines.Items.Clear();
                _hasLastHoleCart = false;
                _hasLastHolePolar = false;

                if (set.RegionLines != null)
                {
                    for (int i = 0; i < set.RegionLines.Count; i++)
                    {
                        string tok = set.RegionLines[i] ?? "";
                        if (string.IsNullOrWhiteSpace(tok))
                            continue;

                        int idx = TextSearching.FindSingleLine(GcodeLines, tok, -1, -1, preferLast: false);
                        if (idx >= 0 && idx < GcodeLines.Count)
                            AddHoleFromLine_NoUI(idx);
                    }
                }
            }
            finally
            {
                _isApplyingDrillSet = false;
            }

            RefreshHighlighting();
        }






        private int FindUniqueIndexByText(string markerText, List<string> allLines)
        {
            string key = TextSearching.NormalizeTextLineToGcodeAndEndTag(markerText);
            if (string.IsNullOrWhiteSpace(key))
                return -1;

            int found = -1;
            for (int i = 0; i < allLines.Count; i++)
            {
                if (TextSearching.NormalizeTextLineToGcodeAndEndTag(allLines[i] ?? "") == key)
                {
                    if (found != -1)
                        return -2; // ambiguous
                    found = i;
                }
            }
            return found; // -1 missing, -2 ambiguous, >=0 ok
        }


        private void ResolveDrillAndUpdateStatus(RegionSet set, List<string> allLines)
        {
            if (set == null)
                return;

            // Holes are stored in set.RegionLines
            if (set.RegionLines == null || set.RegionLines.Count == 0)
            {
                set.Status = RegionResolveStatus.Unset;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            // Resolve depth token via TextSearching (anchor + optional line-number prefix tolerated)
            string depthTok = GetSnapshotOrDefault(set, KEY_DRILL_DEPTH_TEXT, "");
            if (string.IsNullOrWhiteSpace(depthTok))
            {
                set.Status = RegionResolveStatus.Missing;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            int depthIdx = TextSearching.FindSingleLine(allLines, depthTok, -1, -1, preferLast: false);
            if (depthIdx < 0)
            {
                set.Status = RegionResolveStatus.Missing;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            // Resolve each hole line token
            int min = int.MaxValue;
            int max = int.MinValue;

            for (int i = 0; i < set.RegionLines.Count; i++)
            {
                string tok = set.RegionLines[i] ?? "";
                if (string.IsNullOrWhiteSpace(tok))
                {
                    set.Status = RegionResolveStatus.Missing;
                    set.ResolvedStartLine = null;
                    set.ResolvedEndLine = null;
                    return;
                }

                int idx = TextSearching.FindSingleLine(allLines, tok, -1, -1, preferLast: false);
                if (idx < 0)
                {
                    set.Status = RegionResolveStatus.Missing;
                    set.ResolvedStartLine = null;
                    set.ResolvedEndLine = null;
                    return;
                }

                if (idx < min) min = idx;
                if (idx > max) max = idx;
            }

            if (min == int.MaxValue || max == int.MinValue)
            {
                set.Status = RegionResolveStatus.Unset;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            // Store 0-based indices (same convention as TurningPage)
            set.Status = RegionResolveStatus.Ok;
            set.ResolvedStartLine = min;
            set.ResolvedEndLine = max;
        }





        private void StoreSelectionIntoSelectedSet()
        {
            if (_isApplyingDrillSet)
                return;

            var set = GetSelectedDrillSetSafe();
            if (set == null)
                return;

            EnsureSnapshot(set);

            // Ensure stable UID for this set (survives save/load and renames)
            string uid = EnsureRegionUid(set);

            // Store params
            set.PageSnapshot!.Values[KEY_HOLE_DIA] = TxtHoleDia?.Text ?? "";
            set.PageSnapshot!.Values[KEY_Z_HOLE_TOP] = TxtZHoleTop?.Text ?? "";
            set.PageSnapshot!.Values[KEY_POINT_ANGLE] = TxtPointAngle?.Text ?? "";
            set.PageSnapshot!.Values[KEY_CHAMFER] = TxtChamfer?.Text ?? "";
            set.PageSnapshot!.Values[KEY_Z_PLUS_EXT] = TxtZPlusExt?.Text ?? "";

            // Store coord mode
            set.PageSnapshot!.Values[KEY_COORD_MODE] = (RadPolar?.IsChecked == true) ? "Polar" : "Cartesian";

            // Store depth line token as: #uid,1#<normalized line text>
            if (drillDepthLineIndex >= 0 && drillDepthLineIndex < GcodeLines.Count)
            {
                set.PageSnapshot!.Values[KEY_DRILL_DEPTH_TEXT] =
                    MakeAnchorToken(uid, 1, GcodeLines[drillDepthLineIndex]);
            }
            else
            {
                // If depth isn't currently selected, leave whatever was stored (don’t destroy on UI refresh)
                set.PageSnapshot!.Values[KEY_DRILL_DEPTH_TEXT] =
                    (set.PageSnapshot!.Values.TryGetValue(KEY_DRILL_DEPTH_TEXT, out var oldDepth) ? oldDepth : "");
            }

            if (set.RegionLines == null)
                throw new InvalidOperationException("RegionSet.RegionLines is null. It must be initialized by RegionSet.");

            // Store holes as: #uid,1#..., #uid,2#..., in the SAME ORDER as holePoints
            set.RegionLines.Clear();
            for (int i = 0; i < holePoints.Count; i++)
            {
                int li = holePoints[i].LineIndex;
                if (li < 0 || li >= GcodeLines.Count)
                    continue;

                // 1-based sequence number inside THIS hole list (this is what you asked for)
                int n1 = i + 1;

                set.RegionLines.Add(
                    MakeAnchorToken(uid, n1, GcodeLines[li])
                );
            }

            ResolveDrillAndUpdateStatus(set, GcodeLines);
        }




        private void AddHoleFromLine_NoUI(int lineIndex)
        {
            // Minimal, no MessageBoxes. This is used during ApplyDrillSet.
            // It uses the same parsing rules as TryAddHoleFromLine.

            if (GcodeLines == null || lineIndex < 0 || lineIndex >= GcodeLines.Count)
                return;

            string line = GcodeLines[lineIndex] ?? string.Empty;
            var inv = CultureInfo.InvariantCulture;

            double xCart;
            double yCart;

            if (RadCartesian.IsChecked == true)
            {
                bool hasX = TryGetCoord(line, 'X', out double xVal);
                bool hasY = TryGetCoord(line, 'Y', out double yVal);

                if (!hasX && !hasY)
                    return;

                if (!hasX)
                {
                    if (!_hasLastHoleCart) return;
                    xVal = _lastHoleX;
                }

                if (!hasY)
                {
                    if (!_hasLastHoleCart) return;
                    yVal = _lastHoleY;
                }

                xCart = xVal;
                yCart = yVal;

                _lastHoleX = xCart;
                _lastHoleY = yCart;
                _hasLastHoleCart = true;
            }
            else // Polar
            {
                bool hasDiam = TryGetCoord(line, 'X', out double diam);
                bool hasAngle = TryGetCoord(line, 'C', out double angleDeg);

                if (!hasDiam && !hasAngle)
                    return;

                if (!hasDiam)
                {
                    if (!_hasLastHolePolar) return;
                    diam = _lastPolarDiam;
                }

                if (!hasAngle)
                {
                    if (!_hasLastHolePolar) return;
                    angleDeg = _lastPolarAngleDeg;
                }

                double radius = diam / 2.0;
                double angleRad = angleDeg * Math.PI / 180.0;

                xCart = radius * Math.Cos(angleRad);
                yCart = radius * Math.Sin(angleRad);

                _lastPolarDiam = diam;
                _lastPolarAngleDeg = angleDeg;
                _hasLastHolePolar = true;

                _lastHoleX = xCart;
                _lastHoleY = yCart;
                _hasLastHoleCart = true;
            }

            holePoints.Add(new HolePoint { LineIndex = lineIndex, X = xCart, Y = yCart });

            if (LstHoleLines != null)
            {
                string formatted = $"{xCart.ToString("0.###", inv)} {yCart.ToString("0.###", inv)}";
                LstHoleLines.Items.Add(formatted);
            }
        }















        /// <summary>
        /// Called by MainWindow after GcodeLines has been loaded or reloaded.
        /// Resets DrillPage-specific state and renders the editor with line numbers.
        /// </summary>
        public void OnGcodeModelLoaded()
        {
            drillDepthLineIndex = -1;
            _drillDepthZ = double.NaN;
            selectedLineIndex = -1;

            holeSelectionActive = false;
            holePoints.Clear();
            _hasLastHoleCart = false;
            _lastHoleX = 0.0;
            _lastHoleY = 0.0;
            _hasLastHolePolar = false;
            _lastPolarDiam = 0.0;
            _lastPolarAngleDeg = 0.0;

            // Clear visible UI WITHOUT pushing defaults into the selected set.
            _isApplyingDrillSet = true;
            try
            {
                if (BtnDrillDepthLine != null)
                    BtnDrillDepthLine.Content = "(none)";

                if (LstHoleLines != null)
                    LstHoleLines.Items.Clear();

                if (TxtHoleDia != null) TxtHoleDia.Text = "";
                if (TxtZHoleTop != null) TxtZHoleTop.Text = "";
                if (TxtPointAngle != null) TxtPointAngle.Text = "";
                if (TxtChamfer != null) TxtChamfer.Text = "";
                if (TxtZPlusExt != null) TxtZPlusExt.Text = "";

                if (RadCartesian != null) RadCartesian.IsChecked = true;
                if (RadPolar != null) RadPolar.IsChecked = false;
            }
            finally
            {
                _isApplyingDrillSet = false;
            }

            RefreshHighlighting();

            // If a drill set is selected, apply it immediately (restore from store).
            var set = GetSelectedDrillSetSafe();
            if (set != null)
                ApplyDrillSet(set);
        }



        public void OnPageActivated()
        {
            // Keep your existing behavior: no resets.
            // But if a DrillSet is selected, apply it so remembered holes/params reappear.
            RefreshHighlighting();

            var set = GetSelectedDrillSetSafe();
            if (set != null)
                ApplyDrillSet(set);
        }




        /// <summary>
        /// Sync from the RichTextBox back into GcodeLines, stripping line-number prefixes.
        /// This makes edits "stick" into the model, then we can safely re-render.
        /// </summary>
        private void SyncGcodeLinesFromEditor()
        {
            if (GcodeEditor == null)
                return;

            TextRange tr = new TextRange(
                GcodeEditor.Document.ContentStart,
                GcodeEditor.Document.ContentEnd);

            string allText = tr.Text.Replace("\r\n", "\n");

            GcodeLines.Clear();

            using (StringReader reader = new StringReader(allText))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line;

                    // Strip "line number:" prefix we add in RefreshHighlighting
                    // e.g. "   27: G03 X320..." -> "G03 X320..."
                    int colonIndex = trimmed.IndexOf(':');
                    if (colonIndex >= 0 && colonIndex < 10)
                    {
                        trimmed = trimmed[(colonIndex + 1)..].TrimStart();
                    }

                    GcodeLines.Add(trimmed);
                }
            }
        }

        /// <summary>
        /// Numbered view for Drill:
        /// Writes each G-code line as:
        ///   "   1: G01 X100 Z-10"
        /// The currently selected line (by click) is drawn in blue.
        /// The Z-depth line gets a red background, hole lines get yellow background.
        /// </summary>
        // File: Pages/DrillPage.xaml.cs
        // Method: RefreshHighlighting()
        // Change: REMOVE unique-tag colouring entirely. Display the full line as normal text.

        private void RefreshHighlighting()
        {
            // return if shift key is down
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
                return;

            if (GcodeEditor == null || GcodeLines == null)
                return;

            var rtb = GcodeEditor;
            rtb.Document.Blocks.Clear();

            Debug.WriteLine("renum fired drill");

            for (int i = 0; i < GcodeLines.Count; i++)
            {
                string line = GcodeLines[i] ?? string.Empty;

                // Foreground colour: blue if this line is selected, black otherwise
                Brush fg = (i == selectedLineIndex) ? Brushes.Blue : Brushes.Black;

                // Line background:
                // - Z-depth line: red
                // - Hole lines: yellow
                // - Otherwise: transparent
                Brush bg = Brushes.Transparent;

                if (i == drillDepthLineIndex)
                {
                    bg = Brushes.Red;
                }
                else
                {
                    bool isHole = false;
                    for (int h = 0; h < holePoints.Count; h++)
                    {
                        if (holePoints[h].LineIndex == i)
                        {
                            isHole = true;
                            break;
                        }
                    }

                    if (isHole)
                        bg = Brushes.Yellow;
                }

                Paragraph p = new Paragraph { Margin = new Thickness(0), Background = bg };

                // Tag this paragraph with its line index and hook click
                p.Tag = i;
                p.MouseLeftButtonDown += GcodeLine_MouseLeftButtonDown;

                // Line number prefix
                UiUtilities.AddNumberedLinePrefix(p, i + 1, fg);

                // Full line text — NO unique-tag colouring
                p.Inlines.Add(new Run(line) { Foreground = fg });

                rtb.Document.Blocks.Add(p);
            }

            UiUtilities.RebuildAndStoreNumberedLineStartIndex(rtb);
        }




        // Unique tag styling: (u:xxxx) — light blue @ ~50% opacity
        private static readonly Brush UniqueTagBrush = UniqueTagColor.UniqueTagBrush;

        private static bool TrySplitUniqueTag(string line, out string mainText, out string tagText)
        {
            mainText = line ?? string.Empty;
            tagText = string.Empty;

            if (string.IsNullOrEmpty(mainText))
                return false;

            // Look for the unique suffix tag start: "(u:"
            int idx = mainText.LastIndexOf("(u:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;

            // Must have a closing ')'
            int close = mainText.IndexOf(')', idx);
            if (close < 0)
                return false;

            // Keep ALL text from the tag start (including any trailing spacing)
            tagText = mainText.Substring(idx);
            mainText = mainText.Substring(0, idx);
            return true;
        }












        /// <summary>
        /// Paragraph click handler:
        /// - Updates selectedLineIndex (blue text)
        /// - If holeSelectionActive, adds a hole from this line (Cartesian or Polar)
        /// </summary>
        private void GcodeLine_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // First sync edits from the editor into GcodeLines, so we don't lose changes.
            SyncGcodeLinesFromEditor();

            if (sender is Paragraph p && p.Tag is int lineIndex)
            {
                selectedLineIndex = lineIndex;

                // If in hole selection mode, this click adds a hole
                if (holeSelectionActive)
                {
                    TryAddHoleFromLine(lineIndex);
                }
                else
                {
                    // Just selection, no hole added
                    RefreshHighlighting();
                }
            }
        }

        /// <summary>
        /// Parse a coordinate value from a G-code line:
        /// X100.0, Y-25, Z-10, C90 etc. (no spaces required).
        /// </summary>
        private bool TryGetCoord(string line, char axis, out double value)
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
                    return false; // no more occurrences

                int start = idx + 1;
                if (start >= s.Length)
                    return false;

                int end = start;

                // Allow sign, digits and decimal point
                while (end < s.Length)
                {
                    char c = s[end];
                    if (char.IsDigit(c) || c == '+' || c == '-' || c == '.')
                    {
                        end++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (end > start)
                {
                    string numStr = s.Substring(start, end - start);

                    if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        return true; // first valid occurrence wins
                    }
                }

                idx = end;
            }
        }

        private void TryAddHoleFromLine(int lineIndex)
        {
            if (GcodeLines == null || lineIndex < 0 || lineIndex >= GcodeLines.Count)
                return;

            string line = GcodeLines[lineIndex] ?? string.Empty;
            var inv = CultureInfo.InvariantCulture;

            double xCart;
            double yCart;

            if (RadCartesian.IsChecked == true)
            {
                bool hasX = TryGetCoord(line, 'X', out double xVal);
                bool hasY = TryGetCoord(line, 'Y', out double yVal);

                if (!hasX && !hasY)
                {
                    MessageBox.Show(
                        $"Line {lineIndex + 1} does not contain X or Y for Cartesian mode.",
                        "Hole Selection",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!hasX)
                {
                    if (!_hasLastHoleCart)
                    {
                        MessageBox.Show(
                            $"Line {lineIndex + 1} is missing X and there is no previous Cartesian hole to copy from.",
                            "Hole Selection",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                    xVal = _lastHoleX;
                }

                if (!hasY)
                {
                    if (!_hasLastHoleCart)
                    {
                        MessageBox.Show(
                            $"Line {lineIndex + 1} is missing Y and there is no previous Cartesian hole to copy from.",
                            "Hole Selection",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                    yVal = _lastHoleY;
                }

                xCart = xVal;
                yCart = yVal;

                _lastHoleX = xCart;
                _lastHoleY = yCart;
                _hasLastHoleCart = true;
            }
            else if (RadPolar.IsChecked == true)
            {
                bool hasDiam = TryGetCoord(line, 'X', out double diam);
                bool hasAngle = TryGetCoord(line, 'C', out double angleDeg);

                if (!hasDiam && !hasAngle)
                {
                    MessageBox.Show(
                        $"Line {lineIndex + 1} does not contain X (diameter) or C (angle) for Polar mode.",
                        "Hole Selection",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!hasDiam)
                {
                    if (!_hasLastHolePolar)
                    {
                        MessageBox.Show(
                            $"Line {lineIndex + 1} is missing X (diameter) and there is no previous polar hole to copy from.",
                            "Hole Selection",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                    diam = _lastPolarDiam;
                }

                if (!hasAngle)
                {
                    if (!_hasLastHolePolar)
                    {
                        MessageBox.Show(
                            $"Line {lineIndex + 1} is missing C (angle) and there is no previous polar hole to copy from.",
                            "Hole Selection",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                    angleDeg = _lastPolarAngleDeg;
                }

                double radius = diam / 2.0;
                double angleRad = angleDeg * Math.PI / 180.0;

                xCart = radius * Math.Cos(angleRad);
                yCart = radius * Math.Sin(angleRad);

                _lastPolarDiam = diam;
                _lastPolarAngleDeg = angleDeg;
                _hasLastHolePolar = true;

                _lastHoleX = xCart;
                _lastHoleY = yCart;
                _hasLastHoleCart = true;
            }
            else
            {
                MessageBox.Show("Select Cartesian or Polar coordinate mode.", "Hole Selection",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            holePoints.Add(new HolePoint
            {
                LineIndex = lineIndex,
                X = xCart,
                Y = yCart
            });

            string formatted = $"{xCart.ToString("0.###", inv)} {yCart.ToString("0.###", inv)}";
            LstHoleLines.Items.Add(formatted);

            RefreshHighlighting();

            // NEW: persist holes/params + set status
            StoreSelectionIntoSelectedSet();
        }


        // 1) Select line for drill Z depth
        private void BtnDrillDepthLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (selectedLineIndex < 0)
                {
                    MessageBox.Show("Click a G-code line in the editor first.");
                    return;
                }

                var lines = GcodeLines;
                if (selectedLineIndex >= lines.Count)
                {
                    MessageBox.Show("Selected line index is out of range.");
                    return;
                }

                string line = lines[selectedLineIndex];

                if (!TryGetCoord(line, 'Z', out double zValue))
                {
                    MessageBox.Show("Selected line does not contain a valid Z value.");
                    return;
                }

                _drillDepthZ = zValue;
                drillDepthLineIndex = selectedLineIndex;

                BtnDrillDepthLine.Content =
                    $"Driil Depth ={_drillDepthZ.ToString("0.###", CultureInfo.InvariantCulture)}";

                RefreshHighlighting();

                // NEW: persist depth/params + set status
                StoreSelectionIntoSelectedSet();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Drill Z Depth Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        // 2) Start / reset hole selection:
        //    - Clears existing holes and list.
        //    - Enables selection mode: subsequent line clicks add holes.
        // ------------------------------------------------------------
        // Helper: refresh the ListBox so it always matches holePoints
        // ------------------------------------------------------------
        private void RebuildHoleListUI()
        {
            if (LstHoleLines == null)
                return;

            var inv = CultureInfo.InvariantCulture;

            LstHoleLines.Items.Clear();
            for (int i = 0; i < holePoints.Count; i++)
            {
                var hp = holePoints[i];
                string formatted = $"{hp.X.ToString("0.###", inv)} {hp.Y.ToString("0.###", inv)}";
                LstHoleLines.Items.Add(formatted);
            }
        }

        // ------------------------------------------------------------
        // Helper: set button visuals + carry-forward state
        // ------------------------------------------------------------
        private void SetHoleAddMode(bool isActive)
        {
            holeSelectionActive = isActive;

            if (BtnAddHoleLine != null)
            {
                if (holeSelectionActive)
                {
                    BtnAddHoleLine.Background = Brushes.Orange;
                    BtnAddHoleLine.Content = "Stop Add";
                }
                else
                {
                    BtnAddHoleLine.Background = Utilities.UiUtilities.HexBrush("#FF007ACC");
                    BtnAddHoleLine.Content = "Start Add";
                }
            }

            // Carry-forward state should follow the current last hole
            if (holePoints.Count > 0)
            {
                var last = holePoints[holePoints.Count - 1];
                _lastHoleX = last.X;
                _lastHoleY = last.Y;
                _hasLastHoleCart = true;

                // We don't store polar (diam/angle) per hole, so reset polar carry.
                _hasLastHolePolar = false;
                _lastPolarDiam = 0.0;
                _lastPolarAngleDeg = 0.0;
            }
            else
            {
                _hasLastHoleCart = false;
                _lastHoleX = 0.0;
                _lastHoleY = 0.0;

                _hasLastHolePolar = false;
                _lastPolarDiam = 0.0;
                _lastPolarAngleDeg = 0.0;
            }
        }

        private void BtnAddHoleLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Toggle add mode:
                //  - ON  => clicking in the editor adds holes
                //  - OFF => clicking in the editor is normal selection/editing
                SetHoleAddMode(!holeSelectionActive);

                // Just redraw highlights to reflect current state
                RefreshHighlighting();

                // No StoreSelectionIntoSelectedSet() here because Add/Stop is not part of the saved set state.
                // Holes are stored when they are actually added/removed/reset.
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Add Hole Line Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnResetHoleLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Reset = remove all holes
                holePoints.Clear();
                if (LstHoleLines != null)
                    LstHoleLines.Items.Clear();

                // Stop add mode so user can edit text immediately
                SetHoleAddMode(false);

                RefreshHighlighting();

                // Persist “cleared holes” to the set (so status updates to Unset/Missing appropriately)
                StoreSelectionIntoSelectedSet();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Reset Holes Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelHoleLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Delete rule:
                // "if a selected line in the textbox matches a hole on the list then it is removed"
                if (selectedLineIndex < 0)
                {
                    MessageBox.Show("Click a G-code line in the editor first (the hole line you want to delete).",
                                    "Delete Hole", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int removed = holePoints.RemoveAll(h => h.LineIndex == selectedLineIndex);

                if (removed == 0)
                {
                    MessageBox.Show($"No hole is stored for the currently selected G-code line {selectedLineIndex + 1}.",
                                    "Delete Hole", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Rebuild list + carry-forward state
                RebuildHoleListUI();
                SetHoleAddMode(holeSelectionActive); // keeps current mode but refreshes carry-forward state

                RefreshHighlighting();

                // Persist new hole list to the set + status update
                StoreSelectionIntoSelectedSet();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Delete Hole Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }














        private void BtnExportDrill_Click(object sender, RoutedEventArgs e)
        {
            FreeCadRunSuffix.ResetDrill();

            try
            {
                UiUtilities.CloseAllToolWindows();

                string exportDir = Main.GetExportDirectory();

                var set = GetSelectedDrillSetSafe();

                bool ok;
                string reason;

                if (set != null)
                {
                    ok = ExportSetBatch(set, exportDir, out reason);
                }
                else
                {
                    // Fallback: selection-based export (no set selected)
                    ok = ExportCurrentSelectionDrillCore(exportDir, "holes", batchMode: Main.IsExportAllRunning, out reason);
                }

                if (!ok)
                {
                    MessageBox.Show(reason, "Drill Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!Main.IsExportAllRunning)
                    MessageBox.Show("Export complete.", "Drill Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Drill Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }





        // ============================================================
        // BATCH EXPORT API (called by MainWindow ExportAll)
        // ============================================================
        public bool ExportSetBatch(RegionSet set, string exportDir, out string failReason)
        {
            failReason = "";

            try
            {
                if (set == null)
                {
                    failReason = "Set was null.";
                    return false;
                }

                SyncGcodeLinesFromEditor();
                EnsureSnapshot(set);

                // Gate by status (re-resolve against current editor)
                ResolveDrillAndUpdateStatus(set, GcodeLines);
                if (set.Status != RegionResolveStatus.Ok)
                {
                    failReason = $"Status is {set.Status}.";
                    return false;
                }

                // Build export data from snapshot (NO UI state)
                if (!TryBuildHoleGroupFromSet(
                        set,
                        GcodeLines,
                        out DrillViewWindowV2.HoleGroup? grp,
                        out string reason))
                {
                    failReason = reason;
                    return false;
                }

                if (grp == null || grp.Holes == null || grp.Holes.Count == 0)
                {
                    failReason = "HoleGroup was empty.";
                    return false;
                }

                Directory.CreateDirectory(exportDir);
                var inv = CultureInfo.InvariantCulture;

                // ------------------------------------------------------------
                // Output filenames
                // ------------------------------------------------------------
                string safe = MainWindow.SanitizeFileStem(set.Name);
                string txtPath = Path.Combine(exportDir, $"{safe}_holes.txt");
                string stepPath = Path.Combine(exportDir, $"{safe}_Holes_stp.stp");

                // ------------------------------------------------------------
                // RAW TXT (debug / audit)
                // ------------------------------------------------------------
                var rawTxtLines = new List<string>
        {
            $"PARAMS {grp.HoleDia.ToString("0.###", inv)} " +
            $"{grp.ZHoleTop.ToString("0.###", inv)} " +
            $"{grp.PointAngle.ToString("0.###", inv)} " +
            $"{grp.ChamferLen.ToString("0.###", inv)} " +
            $"{grp.ZPlusExt.ToString("0.###", inv)} " +
            $"{grp.DrillZApex.ToString("0.###", inv)}"
        };

                foreach (var h in grp.Holes)
                    rawTxtLines.Add($"{h.X.ToString("0.###", inv)} {h.Y.ToString("0.###", inv)}");


                if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
                { File.WriteAllLines(txtPath, rawTxtLines); }



                // ------------------------------------------------------------
                // Inject Python parameters
                // ------------------------------------------------------------

                //this is a work around to move the chtop above the flat face of z_hole_top the fix cut fails
                double ChExtended = grp.ChamferLen + .1;
                double HoletopExtended = grp.ZHoleTop + .1;

                FreeCadScriptDrill.HoleShape = $@"
hole_dia    = {grp.HoleDia.ToString("0.###", inv)}
z_hole_top  = {HoletopExtended.ToString("0.###", inv)}
point_angle = {grp.PointAngle.ToString("0.###", inv)}
chamfer_len = {ChExtended.ToString("0.###", inv)}
z_plus_ext  = {grp.ZPlusExt.ToString("0.###", inv)}
drill_z     = {grp.DrillZApex.ToString("0.###", inv)}
";



                //TRANSFORM_ROTZ = 45.0
                //TRANSFORM_ROTY = 180.0
                //TRANSFORM_TX = 0.0
                //TRANSFORM_TY = 0.0
                //TRANSFORM_TZ = -150.0

                // Fetch transform for THIS region/set name (same helper as Mill)
                Main.TryGetTransformForRegion(
                    set.Name,
                    out double rotYDeg,
                    out double rotZDeg,
                    out double tx,
                    out double ty,
                    out double tz,
                    out string matrixName);

                FreeCadScriptDrill.TransPY = $@"
#{matrixName}
TRANSFORM_ROTZ = {rotZDeg.ToString("0.###", inv)}
TRANSFORM_ROTY  = {rotYDeg.ToString("0.###", inv)}
TRANSFORM_TX = {tx.ToString("0.###", inv)}
TRANSFORM_TY = {ty.ToString("0.###", inv)}
TRANSFORM_TZ  = {tz.ToString("0.###", inv)}
";




                var sbPos = new StringBuilder();
                sbPos.AppendLine("hole_coords = [");
                foreach (var h in grp.Holes)
                    sbPos.AppendLine($"    ({h.X.ToString("0.###", inv)}, {h.Y.ToString("0.###", inv)}),");
                sbPos.AppendLine("]");
                FreeCadScriptDrill.Positions = sbPos.ToString();

                // ------------------------------------------------------------
                // Run FreeCAD (per-set STEP generation)
                // ------------------------------------------------------------
                string scriptPath = FreeCadRunnerDrill.SaveScript(stepPath);

                try
                {
                    _ = FreeCadRunnerDrill.RunFreeCad(scriptPath, exportDir);
                }
                catch (Exception fcEx)
                {
                    failReason = $"FreeCAD failed: {fcEx.Message}";
                    return false;
                }

                // ------------------------------------------------------------
                // EXPORT-ALL TRACKING (NO MERGE YET)
                // ------------------------------------------------------------
                Main.ExportAllCreatedStepFiles.Add(stepPath);

                return true;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                return false;
            }
        }



        // ============================================================
        // Single-export fallback (current selection state)
        // ============================================================
        private bool ExportCurrentSelectionDrillCore(string exportDir, string fileStem, bool batchMode, out string failReason)
        {
            failReason = "";

            try
            {
                var inv = CultureInfo.InvariantCulture;

                if (!double.TryParse(TxtHoleDia.Text, NumberStyles.Float, inv, out double holeDia))
                {
                    failReason = "Invalid Hole Dia value.";
                    return false;
                }

                if (!double.TryParse(TxtZHoleTop.Text, NumberStyles.Float, inv, out double zHoleTop))
                {
                    failReason = "Invalid Z Hole Top value.";
                    return false;
                }

                if (!double.TryParse(TxtPointAngle.Text, NumberStyles.Float, inv, out double pointAngle))
                {
                    failReason = "Invalid Point Angle value.";
                    return false;
                }

                if (pointAngle <= 0.0 || pointAngle > 180.0)
                {
                    failReason = "Point Angle (included) must be > 0 and <= 180 degrees.";
                    return false;
                }

                if (!double.TryParse(TxtChamfer.Text, NumberStyles.Float, inv, out double chamferLen))
                {
                    failReason = "Invalid Chamfer value.";
                    return false;
                }

                if (!double.TryParse(TxtZPlusExt.Text, NumberStyles.Float, inv, out double zPlusExt))
                {
                    failReason = "Invalid Z+ Extension value.";
                    return false;
                }

                if (double.IsNaN(_drillDepthZ))
                {
                    failReason = "Drill depth (apex Z) is not set.";
                    return false;
                }

                if (holePoints.Count == 0)
                {
                    failReason = "No holes selected.";
                    return false;
                }

                Directory.CreateDirectory(exportDir);

                string txtPath = System.IO.Path.Combine(exportDir, $"{fileStem}_holes.txt");
                string stepPath = System.IO.Path.Combine(exportDir, $"{fileStem}_Holes_stp.stp");

                // Raw TXT
                var rawTxtLines = new List<string>
        {
            $"PARAMS {holeDia.ToString("0.###", inv)} {zHoleTop.ToString("0.###", inv)} {pointAngle.ToString("0.###", inv)} {chamferLen.ToString("0.###", inv)} {zPlusExt.ToString("0.###", inv)} {_drillDepthZ.ToString("0.###", inv)}"
        };

                foreach (var hp in holePoints)
                    rawTxtLines.Add($"{hp.X.ToString("0.###", inv)} {hp.Y.ToString("0.###", inv)}");

                File.WriteAllLines(txtPath, rawTxtLines);

                // Python
                FreeCadScriptDrill.HoleShape = $@"
hole_dia    = {holeDia.ToString("0.###", inv)}
z_hole_top  = {zHoleTop.ToString("0.###", inv)}
point_angle = {pointAngle.ToString("0.###", inv)}
chamfer_len = {chamferLen.ToString("0.###", inv)}
z_plus_ext  = {zPlusExt.ToString("0.###", inv)}
drill_z     = {_drillDepthZ.ToString("0.###", inv)}
";


                //TRANSFORM_ROTZ = 45.0
                //TRANSFORM_ROTY = 180.0
                //TRANSFORM_TX = 0.0
                //TRANSFORM_TY = 0.0
                //TRANSFORM_TZ = -150.0

                // Fetch transform for THIS region/set name (same helper as Mill)
                Main.TryGetTransformForRegion(
                    fileStem,
                    out double rotYDeg,
                    out double rotZDeg,
                    out double tx,
                    out double ty,
                    out double tz,
                    out string matrixName);

                FreeCadScriptDrill.TransPY = $@"
#{matrixName}
TRANSFORM_ROTZ = {rotZDeg.ToString("0.###", inv)}
TRANSFORM_ROTY  = {rotYDeg.ToString("0.###", inv)}
TRANSFORM_TX = {tx.ToString("0.###", inv)}
TRANSFORM_TY = {ty.ToString("0.###", inv)}
TRANSFORM_TZ  = {tz.ToString("0.###", inv)}
";








                var sbPos = new StringBuilder();
                sbPos.AppendLine("hole_coords = [");
                foreach (var hp in holePoints)
                    sbPos.AppendLine($"    ({hp.X.ToString("0.###", inv)}, {hp.Y.ToString("0.###", inv)}),");
                sbPos.AppendLine("]");
                FreeCadScriptDrill.Positions = sbPos.ToString();






                string scriptPath = FreeCadRunnerDrill.SaveScript(stepPath);
                _ = FreeCadRunnerDrill.RunFreeCad(scriptPath, exportDir);

                return true;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                return false;
            }
        }








        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "export";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            // Collapse whitespace
            name = System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ");

            if (name == "." || name == "..")
                name = "export";

            return name;
        }



        private void BtnViewAllDrilling_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiUtilities.CloseAllToolWindows();

                // Ensure editor edits are in the model
                SyncGcodeLinesFromEditor();

                if (Main?.DrillSets == null || Main.DrillSets.Count == 0)
                    throw new Exception("There are no DRILL sets to display.");

                // VIEWER-ONLY transform:
                // - RotZ is CW about 0,0 (larger = more CW)
                // - RotY only if 180 => mirror about Y0 (flip X)
                // NOTE: Export is NOT affected because we only transform the groups passed to the viewer.
                static (double X, double Y) ApplyViewerTransform(double x, double y, double rotZDeg, double rotYDeg)
                {
                    // RotZ (CW)
                    if (double.IsFinite(rotZDeg))
                    {
                        double rz = rotZDeg * (Math.PI / 180.0);
                        double c = Math.Cos(rz);
                        double s = Math.Sin(rz);

                        // CW rotation:
                        // x' = x*cos + y*sin
                        // y' = -x*sin + y*cos
                        double x1 = x * c + y * s;
                        double y1 = -x * s + y * c;
                        x = x1;
                        y = y1;
                    }

                    // RotY only if 180 => mirror about Y axis (Y0)
                    if (double.IsFinite(rotYDeg) && Math.Abs(rotYDeg - 180.0) <= 1e-6)
                    {
                        x = -x;
                    }

                    return (x, y);
                }

                var groups = new List<DrillViewWindowV2.HoleGroup>();
                var skipped = new List<string>();

                // DEBUG LOG (like Mill)
                var sbLog = new StringBuilder();
                var inv = CultureInfo.InvariantCulture;

                sbLog.AppendLine("=== VIEW ALL DRILLING : DEBUG ===");
                sbLog.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sbLog.AppendLine($"DrillSets: {Main.DrillSets.Count}");
                sbLog.AppendLine("Transform handling (VIEWER ONLY): RotZ(CW about 0,0), then RotY only if 180 => mirror about Y-axis (flip X).");
                sbLog.AppendLine("NOTE: Export is NOT changed by this view-only transform.");
                sbLog.AppendLine();

                int built = 0;
                int skippedCount = 0;
                int totalHolesRaw = 0;
                int totalHolesView = 0;

                // One HoleGroup per Drill Set (drives per-group colour in the V2 viewer)
                for (int i = 0; i < Main.DrillSets.Count; i++)
                {
                    var set = Main.DrillSets[i];
                    if (set == null)
                    {
                        skippedCount++;
                        continue;
                    }

                    // NEW: only include sets that are enabled for "View All"
                    if (!set.ShowInViewAll)
                    {
                        skippedCount++;
                        continue;
                    }

                    string setName = string.IsNullOrWhiteSpace(set.Name) ? $"(unnamed set #{i + 1})" : set.Name.Trim();
                    sbLog.AppendLine($"--- SET {i + 1}/{Main.DrillSets.Count}: {setName} ---");

                    if (TryBuildHoleGroupFromSet(set, GcodeLines, out DrillViewWindowV2.HoleGroup? grpRaw, out string reason))
                    {
                        // Fetch transform for THIS region/set name (same helper as Mill)
                        Main.TryGetTransformForRegion(
                            set.Name,
                            out double rotYDeg,
                            out double rotZDeg,
                            out double tx,
                            out double ty,
                            out double tz,
                            out string matrixName);

                        sbLog.AppendLine($"@TRANS Matrix: {matrixName}");
                        sbLog.AppendLine($"@TRANS RotZ(CW+): {rotZDeg.ToString("0.###", inv)}");
                        sbLog.AppendLine($"@TRANS RotY: {rotYDeg.ToString("0.###", inv)}");
                        sbLog.AppendLine($"@TRANS Tx/Ty/Tz (ignored for viewer now): {tx.ToString("0.###", inv)} {ty.ToString("0.###", inv)} {tz.ToString("0.###", inv)}");

                        sbLog.AppendLine("PARAMS:");
                        sbLog.AppendLine($"  HoleDia    : {grpRaw!.HoleDia.ToString("0.###", inv)}");
                        sbLog.AppendLine($"  ZHoleTop   : {grpRaw.ZHoleTop.ToString("0.###", inv)}");
                        sbLog.AppendLine($"  PointAngle : {grpRaw.PointAngle.ToString("0.###", inv)}");
                        sbLog.AppendLine($"  ChamferLen : {grpRaw.ChamferLen.ToString("0.###", inv)}");
                        sbLog.AppendLine($"  ZPlusExt   : {grpRaw.ZPlusExt.ToString("0.###", inv)}");
                        sbLog.AppendLine($"  DrillZApex : {grpRaw.DrillZApex.ToString("0.###", inv)}");

                        int holeCount = (grpRaw.Holes != null) ? grpRaw.Holes.Count : 0;
                        sbLog.AppendLine($"Holes(raw): {holeCount}");

                        totalHolesRaw += holeCount;

                        // Clone group for viewer with transformed XY only (DO NOT affect export data path)
                        var grpView = new DrillViewWindowV2.HoleGroup
                        {
                            GroupName = grpRaw.GroupName,
                            HoleDia = grpRaw.HoleDia,
                            ZHoleTop = grpRaw.ZHoleTop,
                            PointAngle = grpRaw.PointAngle,
                            ChamferLen = grpRaw.ChamferLen,
                            ZPlusExt = grpRaw.ZPlusExt,
                            DrillZApex = grpRaw.DrillZApex,
                            Holes = new List<(double X, double Y, int LineIndex)>(holeCount)
                        };

                        sbLog.AppendLine("HOLES (raw => view):");
                        for (int h = 0; h < holeCount; h++)
                        {
                            var p = grpRaw.Holes[h];
                            var t = ApplyViewerTransform(p.X, p.Y, rotZDeg, rotYDeg);
                            grpView.Holes.Add((t.X, t.Y, p.LineIndex));

                            sbLog.AppendLine(
                                $"  #{h + 1,3}  RAW  X={p.X.ToString("0.###", inv)}  Y={p.Y.ToString("0.###", inv)}" +
                                $"   => VIEW  X={t.X.ToString("0.###", inv)}  Y={t.Y.ToString("0.###", inv)}" +
                                $"   (Gcode L{p.LineIndex + 1})");
                        }

                        totalHolesView += holeCount;

                        groups.Add(grpView);
                        built++;

                        sbLog.AppendLine();
                    }
                    else
                    {
                        skippedCount++;
                        skipped.Add($"{setName}: {reason}");
                        sbLog.AppendLine($"SKIP: {reason}");
                        sbLog.AppendLine();
                    }
                }

                if (groups.Count == 0)
                {
                    string msg = "No valid DRILL sets could be displayed.";
                    if (skipped.Count > 0)
                        msg += "\n\n" + string.Join("\n", skipped);

                    throw new Exception(msg);
                }

                sbLog.AppendLine("=== SUMMARY ===");
                sbLog.AppendLine($"Sets built: {built}");
                sbLog.AppendLine($"Sets skipped: {skippedCount}");
                sbLog.AppendLine($"Groups shown: {groups.Count}");
                sbLog.AppendLine($"Total holes (raw): {totalHolesRaw}");
                sbLog.AppendLine($"Total holes (view): {totalHolesView}");
                sbLog.AppendLine();

                if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
                {
                    var ownerLog = Window.GetWindow(this);
                    var logWindow = new LogWindow("VIEW ALL DRILLING : DEBUG", sbLog.ToString());
                    if (ownerLog != null)
                        logWindow.Owner = ownerLog;
                    logWindow.Show();
                }

                var win = new DrillViewWindowV2(groups);

                var owner = Window.GetWindow(this);
                if (owner != null)
                    win.Owner = owner;

                win.Show();

                // Optional warning if some sets were skipped (viewer still opens)
                if (skipped.Count > 0)
                {
                    MessageBox.Show(
                        "Some drill sets could not be displayed:\n\n" + string.Join("\n", skipped),
                        "Drill View All",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Drill View All",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }







        private bool TryBuildHoleGroupFromSet(
    RegionSet set,
    List<string> allLines,
    out DrillViewWindowV2.HoleGroup? group,
    out string reason)
        {
            group = null;
            reason = "";

            if (set == null)
            {
                reason = "Set was null.";
                return false;
            }

            EnsureSnapshot(set);

            if (set.RegionLines == null || set.RegionLines.Count == 0)
            {
                reason = "No hole lines saved in this set.";
                return false;
            }

            // ---- Read per-set scalar params from snapshot ----
            if (!TryParseInvariantDouble(GetSnapshotOrDefault(set, KEY_HOLE_DIA, ""), out double holeDia) || holeDia <= 0)
            {
                reason = "Invalid Hole Dia in set snapshot.";
                return false;
            }

            if (!TryParseInvariantDouble(GetSnapshotOrDefault(set, KEY_Z_HOLE_TOP, ""), out double zHoleTop))
            {
                reason = "Invalid Z Hole Top in set snapshot.";
                return false;
            }

            if (!TryParseInvariantDouble(GetSnapshotOrDefault(set, KEY_POINT_ANGLE, ""), out double pointAngle) ||
                pointAngle <= 0.0 || pointAngle > 180.0)
            {
                reason = "Invalid Point Angle in set snapshot (must be >0 and <=180).";
                return false;
            }

            if (!TryParseInvariantDouble(GetSnapshotOrDefault(set, KEY_CHAMFER, ""), out double chamferLen))
            {
                reason = "Invalid Chamfer value in set snapshot.";
                return false;
            }

            if (!TryParseInvariantDouble(GetSnapshotOrDefault(set, KEY_Z_PLUS_EXT, ""), out double zPlusExt))
            {
                reason = "Invalid Z+ Extension value in set snapshot.";
                return false;
            }

            // ---- Resolve depth token -> apex Z ----
            string depthTok = GetSnapshotOrDefault(set, KEY_DRILL_DEPTH_TEXT, "");
            if (string.IsNullOrWhiteSpace(depthTok))
            {
                reason = "Drill depth line is not set for this drill set.";
                return false;
            }

            int depthIdx = ResolveIndexFromToken(depthTok, allLines);
            if (depthIdx < 0 || depthIdx >= allLines.Count)
            {
                reason = "Drill depth line could not be resolved in the current G-code.";
                return false;
            }

            if (!TryGetCoord(allLines[depthIdx], 'Z', out double drillZApex) || double.IsNaN(drillZApex))
            {
                reason = "Resolved drill depth line does not contain a valid Z value.";
                return false;
            }

            // ---- Coord mode per set ----
            string mode = GetSnapshotOrDefault(set, KEY_COORD_MODE, "Cartesian");
            bool isPolar = mode.Equals("Polar", StringComparison.OrdinalIgnoreCase);

            // ---- Build holes in order, matching your carry-forward rules ----
            bool hasLastCart = false;
            double lastX = 0.0, lastY = 0.0;

            bool hasLastPolar = false;
            double lastDiam = 0.0, lastAngle = 0.0;

            var holes = new List<(double X, double Y, int LineIndex)>();

            for (int i = 0; i < set.RegionLines.Count; i++)
            {
                string tok = set.RegionLines[i] ?? "";
                int idx = ResolveIndexFromToken(tok, allLines);

                if (idx < 0 || idx >= allLines.Count)
                {
                    reason = $"A hole line could not be resolved in the current G-code (item #{i + 1}).";
                    return false;
                }

                string line = allLines[idx] ?? "";

                double xCart, yCart;

                if (!isPolar)
                {
                    bool hasX = TryGetCoord(line, 'X', out double xVal);
                    bool hasY = TryGetCoord(line, 'Y', out double yVal);

                    if (!hasX && !hasY)
                    {
                        reason = $"Hole line #{i + 1} has no X or Y (Cartesian).";
                        return false;
                    }

                    if (!hasX)
                    {
                        if (!hasLastCart)
                        {
                            reason = $"Hole line #{i + 1} is missing X and there is no previous hole to copy from.";
                            return false;
                        }
                        xVal = lastX;
                    }

                    if (!hasY)
                    {
                        if (!hasLastCart)
                        {
                            reason = $"Hole line #{i + 1} is missing Y and there is no previous hole to copy from.";
                            return false;
                        }
                        yVal = lastY;
                    }

                    xCart = xVal;
                    yCart = yVal;

                    lastX = xCart;
                    lastY = yCart;
                    hasLastCart = true;
                }
                else
                {
                    bool hasDiamHere = TryGetCoord(line, 'X', out double diam);
                    bool hasAngHere = TryGetCoord(line, 'C', out double ang);

                    if (!hasDiamHere && !hasAngHere)
                    {
                        reason = $"Hole line #{i + 1} has no X (diam) or C (angle) (Polar).";
                        return false;
                    }

                    if (!hasDiamHere)
                    {
                        if (!hasLastPolar)
                        {
                            reason = $"Hole line #{i + 1} is missing X (diam) and there is no previous polar hole to copy from.";
                            return false;
                        }
                        diam = lastDiam;
                    }

                    if (!hasAngHere)
                    {
                        if (!hasLastPolar)
                        {
                            reason = $"Hole line #{i + 1} is missing C (angle) and there is no previous polar hole to copy from.";
                            return false;
                        }
                        ang = lastAngle;
                    }

                    double radius = diam / 2.0;
                    double aRad = ang * Math.PI / 180.0;

                    xCart = radius * Math.Cos(aRad);
                    yCart = radius * Math.Sin(aRad);

                    lastDiam = diam;
                    lastAngle = ang;
                    hasLastPolar = true;

                    lastX = xCart;
                    lastY = yCart;
                    hasLastCart = true;
                }

                holes.Add((xCart, yCart, idx));
            }

            if (holes.Count == 0)
            {
                reason = "No holes could be built from saved hole lines.";
                return false;
            }

            group = new DrillViewWindowV2.HoleGroup
            {
                GroupName = string.IsNullOrWhiteSpace(set.Name) ? "(unnamed)" : set.Name.Trim(),
                HoleDia = holeDia,
                ZHoleTop = zHoleTop,
                PointAngle = pointAngle,
                ChamferLen = chamferLen,
                ZPlusExt = zPlusExt,
                DrillZApex = drillZApex,
                Holes = holes
            };

            return true;
        }


        private bool TryParseInvariantDouble(string s, out double v)
        {
            var inv = CultureInfo.InvariantCulture;
            return double.TryParse(s ?? "", NumberStyles.Float, inv, out v);
        }




        // ============================================================
        // NEW: Always re-sync selected DRILL set before any viewer run.
        // Fixes: inserting/deleting lines above a saved drill set.
        // ============================================================
        private void ResyncSelectedDrillSetBeforeViewer()
        {
            var set = GetSelectedDrillSetSafe();
            if (set == null)
                return;

            // Force a fresh resolve/build against the latest editor text
            ApplyDrillSet(set);
        }




        private void BtnViewProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiUtilities.CloseAllToolWindows();
                ResyncSelectedDrillSetBeforeViewer();
                // STORE-FIRST: if a set is selected, build/view ONLY from the stored snapshot/tokens
                SyncGcodeLinesFromEditor();

                var set = GetSelectedDrillSetSafe();
                if (set != null)
                {
                    if (!TryBuildHoleGroupFromSet(set, GcodeLines, out DrillViewWindowV2.HoleGroup? grp, out string why))
                        throw new Exception(why);

                    var holes = new List<DrillViewWindow.HoleCenter>();
                    for (int i = 0; i < grp!.Holes.Count; i++)
                    {
                        var h = grp.Holes[i];
                        holes.Add(new DrillViewWindow.HoleCenter
                        {
                            Index = i + 1,
                            LineIndex = h.LineIndex,
                            X = h.X,
                            Y = h.Y
                        });
                    }

                    var win = new DrillViewWindow(
                        holeDia: grp.HoleDia,
                        zHoleTop: grp.ZHoleTop,
                        pointAngle: grp.PointAngle,
                        chamferLen: grp.ChamferLen,
                        zPlusExt: grp.ZPlusExt,
                        drillZApex: grp.DrillZApex,
                        holes: holes);

                    var owner = Window.GetWindow(this);
                    if (owner != null)
                        win.Owner = owner;

                    win.Show();
                    return;
                }

            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Drill Viewer",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }


    }
}
