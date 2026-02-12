using CNC_Improvements_gcode_solids.FreeCadIntegration;
using CNC_Improvements_gcode_solids.SetManagement;
using CNC_Improvements_gcode_solids.SetManagement.Builders;
using CNC_Improvements_gcode_solids.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNC_Improvements_gcode_solids;



namespace CNC_Improvements_gcode_solids.Pages
{
    public partial class DrillPage : Page, IGcodePage
    {
        // ============================================================
        // MainWindow model access
        // ============================================================
        private MainWindow Main => (MainWindow)Application.Current.MainWindow;
        private RichTextBox GcodeEditor => Main.GcodeEditor;
        private List<string> GcodeLines => Main.GcodeLines;

        // ============================================================
        // Snapshot keys (stored in RegionSet.PageSnapshot.Values)
        //
        // NOTE:
        // - RegionLines are always stored anchored (#uid,n#...).
        // - DrillDepthLineText is stored as an anchored RegionLine.
        // - HoleLineTexts MUST ALSO be stored as anchored RegionLines (newline separated).
        //   This is critical so cleanup/retag can remap correctly.
        // ============================================================
        private const string KEY_COORD_MODE = "CoordMode";                 // "Cartesian" / "Polar"
        private const string KEY_DRILL_DEPTH_TEXT = "DrillDepthLineText";  // anchored line (#uid,n#...)
        private const string KEY_HOLE_LINES_TEXT = "HoleLineTexts";        // newline-separated ANCHORED lines (#uid,n#...)

        private const string KEY_HOLE_DIA = "TxtHoleDia";
        private const string KEY_Z_HOLE_TOP = "TxtZHoleTop";
        private const string KEY_POINT_ANGLE = "TxtPointAngle";
        private const string KEY_CHAMFER = "TxtChamfer";
        private const string KEY_Z_PLUS_EXT = "TxtZPlusExt";

        // ============================================================
        // Non-stored UI / interaction state
        // ============================================================
        private bool _isApplyingDrillSet = false;
        private RegionSet? _lastAppliedDrillSetRef = null;

        private int _selectedLineIndex = -1;
        private bool _holeAddMode = false;

        // Depth marker selection (absolute index into GcodeLines)
        private int _depthLineIndex = -1;
        private double _depthZ = double.NaN;

        // Holes selection (absolute indices into GcodeLines + computed XY for display)
        private sealed class HolePoint
        {
            public int LineIndex;
            public double X;
            public double Y;
        }

        private readonly List<HolePoint> _holePoints = new();

        // Carry-forward rules (match legacy behavior)
        private bool _hasLastHoleCart = false;
        private double _lastHoleX = 0.0;
        private double _lastHoleY = 0.0;

        private bool _hasLastHolePolar = false;
        private double _lastPolarDiam = 0.0;
        private double _lastPolarAngleDeg = 0.0;

        // ============================================================
        // Ctor + lifecycle
        // ============================================================
        public DrillPage()
        {
            InitializeComponent();

            Loaded += DrillPage_Loaded;
            Unloaded += DrillPage_Unloaded;

            // Persist scalar params on edit (do NOT rebuild RegionLines here)
            if (TxtHoleDia != null) TxtHoleDia.TextChanged += (_, __) => StoreDrillParamsIntoSelectedSet();
            if (TxtZHoleTop != null) TxtZHoleTop.TextChanged += (_, __) => StoreDrillParamsIntoSelectedSet();
            if (TxtPointAngle != null) TxtPointAngle.TextChanged += (_, __) => StoreDrillParamsIntoSelectedSet();
            if (TxtChamfer != null) TxtChamfer.TextChanged += (_, __) => StoreDrillParamsIntoSelectedSet();
            if (TxtZPlusExt != null) TxtZPlusExt.TextChanged += (_, __) => StoreDrillParamsIntoSelectedSet();

            if (RadCartesian != null) RadCartesian.Checked += (_, __) => StoreDrillParamsIntoSelectedSet();
            if (RadPolar != null) RadPolar.Checked += (_, __) => StoreDrillParamsIntoSelectedSet();
        }

        private void DrillPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (Main is INotifyPropertyChanged npc)
                npc.PropertyChanged += MainWindow_PropertyChanged;

            TryApplySelectedDrillSetIfChanged(force: true);
        }

        private void DrillPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (Main is INotifyPropertyChanged npc)
                npc.PropertyChanged -= MainWindow_PropertyChanged;
        }

        private void MainWindow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "SelectedDrillSet")
                TryApplySelectedDrillSetIfChanged(force: false);
        }

        // ============================================================
        // IGcodePage
        // ============================================================
        public void OnGcodeModelLoaded()
        {
            _depthLineIndex = -1;
            _depthZ = double.NaN;
            _selectedLineIndex = -1;

            _holeAddMode = false;
            _holePoints.Clear();

            ResetCarryState();

            // Clear UI without pushing defaults into any set
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

                UpdateHoleAddButtonUI();
            }
            finally
            {
                _isApplyingDrillSet = false;
            }

            RefreshHighlighting();

            // If a drill set is selected, apply it immediately
            var set = GetSelectedDrillSetSafe();
            if (set != null)
                ApplyDrillSet(set);
        }

        public void OnPageActivated()
        {
            RefreshHighlighting();

            var set = GetSelectedDrillSetSafe();
            if (set != null)
                ApplyDrillSet(set);
        }

        // ============================================================
        // Selected set tracking
        // ============================================================
        private RegionSet? GetSelectedDrillSetSafe() => Main.SelectedDrillSet;

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

        // ============================================================
        // Apply stored set -> UI + resolve indices
        // ============================================================
        public void ApplyDrillSet(RegionSet? set)
        {
            if (set == null)
            {
                RefreshHighlighting();
                return;
            }

            SyncGcodeLinesFromEditor();

            _isApplyingDrillSet = true;
            try
            {
                EnsureSnapshot(set);

                // Scalars
                TxtHoleDia.Text = GetSnapshotOrDefault(set, KEY_HOLE_DIA, "");
                TxtZHoleTop.Text = GetSnapshotOrDefault(set, KEY_Z_HOLE_TOP, "");
                TxtPointAngle.Text = GetSnapshotOrDefault(set, KEY_POINT_ANGLE, "");
                TxtChamfer.Text = GetSnapshotOrDefault(set, KEY_CHAMFER, "");
                TxtZPlusExt.Text = GetSnapshotOrDefault(set, KEY_Z_PLUS_EXT, "");

                // Coord mode
                string mode = GetSnapshotOrDefault(set, KEY_COORD_MODE, "Cartesian");
                bool isPolar = mode.Equals("Polar", StringComparison.OrdinalIgnoreCase);
                RadPolar.IsChecked = isPolar;
                RadCartesian.IsChecked = !isPolar;

                // Resolve region + status
                ResolveDrillAndUpdateStatus(set, GcodeLines);

                // Reset selection state (UI + internal)
                _depthLineIndex = -1;
                _depthZ = double.NaN;
                if (BtnDrillDepthLine != null)
                    BtnDrillDepthLine.Content = "(none)";

                _holePoints.Clear();
                if (LstHoleLines != null) LstHoleLines.Items.Clear();
                ResetCarryState();

                // If the region resolves uniquely, rebuild depth + holes from snapshot *within that resolved region block*
                if (set.Status == RegionResolveStatus.Ok && set.ResolvedStartLine.HasValue && set.ResolvedEndLine.HasValue)
                {
                    int rStart = set.ResolvedStartLine.Value;
                    int rEnd = set.ResolvedEndLine.Value;

                    // Depth (anchored key)
                    string depthTok = GetSnapshotOrDefault(set, KEY_DRILL_DEPTH_TEXT, "");
                    if (!string.IsNullOrWhiteSpace(depthTok))
                    {
                        int depthIdx = FindSingleInRangeUnique(GcodeLines, depthTok, rStart, rEnd, out RegionResolveStatus depthStatus);
                        if (depthStatus == RegionResolveStatus.Ok && depthIdx >= 0 && depthIdx < GcodeLines.Count)
                        {
                            if (TryGetCoord(GcodeLines[depthIdx], 'Z', out double zVal))
                            {
                                _depthLineIndex = depthIdx;
                                _depthZ = zVal;
                                if (BtnDrillDepthLine != null)
                                    BtnDrillDepthLine.Content = $"Drill Depth ={_depthZ.ToString("0.###", CultureInfo.InvariantCulture)}";
                            }
                        }
                    }

                    // Holes (anchored keys)
                    foreach (var tok in ReadHoleTokensFromSnapshot(set))
                    {
                        int idx = FindSingleInRangeUnique(GcodeLines, tok, rStart, rEnd, out RegionResolveStatus holeStatus);
                        if (holeStatus == RegionResolveStatus.Ok && idx >= 0 && idx < GcodeLines.Count)
                            AddHoleFromLine_NoUI(idx);
                        else
                            AddMissingHoleToUI(tok);
                    }
                }

                UpdateHoleAddButtonUI();
            }
            finally
            {
                _isApplyingDrillSet = false;
            }

            RefreshHighlighting();
        }

        // ============================================================
        // Store UI -> set (all structural changes go through BuildDrillRegion)
        // ============================================================
        private void StoreDrillParamsIntoSelectedSet()
        {
            if (_isApplyingDrillSet)
                return;

            // MainWindow (no GetMain helper)
            var main = Application.Current?.MainWindow as MainWindow;
            if (main == null)
                return;

            // IMPORTANT: call the method with ()  (prevents "method group" errors)
            var sets = main.GetBatchSelectedDrillSets();
            if (sets == null || sets.Count == 0)
                return;

            string coordMode = (RadPolar?.IsChecked == true) ? "Polar" : "Cartesian";
            string txtChamfer = TxtChamfer?.Text ?? "";
            string txtHoleDia = TxtHoleDia?.Text ?? "";
            string txtPointAngle = TxtPointAngle?.Text ?? "";
            string txtZHoleTop = TxtZHoleTop?.Text ?? "";
            string txtZPlusExt = TxtZPlusExt?.Text ?? "";

            foreach (var set in sets)
            {
                if (set == null) continue;

                BuildDrillRegion.EditExisting(
                    set,
                    coordMode: coordMode,
                    txtChamfer: txtChamfer,
                    txtHoleDia: txtHoleDia,
                    txtPointAngle: txtPointAngle,
                    txtZHoleTop: txtZHoleTop,
                    txtZPlusExt: txtZPlusExt
                );

                ResolveDrillAndUpdateStatus(set, GcodeLines);
            }
        }



        /// <summary>
        /// Stores the current depth + holes selection.
        ///
        /// When rebuildRegionRange=true:
        /// - RegionLines are rebuilt as a contiguous block from min(selected) to max(selected).
        /// - DrillDepthLineText is stored as anchored RegionLine (handled by BuildDrillRegion).
        /// - HoleLineTexts is stored as newline-separated ANCHORED RegionLines (we set this after rebuild).
        /// </summary>
        private void StoreDrillSelectionIntoSelectedSet(bool rebuildRegionRange)
        {
            if (_isApplyingDrillSet)
                return;

            var set = GetSelectedDrillSetSafe();
            if (set == null)
                return;

            SyncGcodeLinesFromEditor();
            EnsureSnapshot(set);

            IReadOnlyList<string>? regionLines = null;
            int? depthLocalIndex = null;
            List<int> holeLocalIndices = new();

            if (rebuildRegionRange)
            {
                var selectedAbs = new List<int>();

                if (_depthLineIndex >= 0)
                    selectedAbs.Add(_depthLineIndex);

                for (int i = 0; i < _holePoints.Count; i++)
                {
                    int idx = _holePoints[i].LineIndex;
                    if (idx >= 0)
                        selectedAbs.Add(idx);
                }

                if (selectedAbs.Count > 0)
                {
                    int startAbs = selectedAbs.Min();
                    int endAbs = selectedAbs.Max();

                    if (startAbs >= 0 && endAbs >= startAbs && endAbs < GcodeLines.Count)
                    {
                        var region = GcodeLines.GetRange(startAbs, endAbs - startAbs + 1);
                        regionLines = region;

                        // Depth local
                        if (_depthLineIndex >= 0)
                        {
                            int local = _depthLineIndex - startAbs;
                            if (local >= 0 && local < region.Count)
                                depthLocalIndex = local;
                        }

                        // Holes local (preserve order of _holePoints)
                        for (int i = 0; i < _holePoints.Count; i++)
                        {
                            int abs = _holePoints[i].LineIndex;
                            int local = abs - startAbs;
                            if (local >= 0 && local < region.Count)
                                holeLocalIndices.Add(local);
                        }
                    }
                }
            }

            // Rebuild anchored RegionLines (and optionally DrillDepthLineText anchored)
            BuildDrillRegion.EditExisting(
                set,
                regionLines: regionLines,
                drillDepthIndex: (regionLines != null && depthLocalIndex.HasValue) ? depthLocalIndex : null,
                coordMode: (RadPolar?.IsChecked == true) ? "Polar" : "Cartesian",
                txtChamfer: TxtChamfer?.Text ?? "",
                txtHoleDia: TxtHoleDia?.Text ?? "",
                txtPointAngle: TxtPointAngle?.Text ?? "",
                txtZHoleTop: TxtZHoleTop?.Text ?? "",
                txtZPlusExt: TxtZPlusExt?.Text ?? ""
            );

            // IMPORTANT FIX:
            // Store holes as ANCHORED region lines so they survive cleanup/retag.
            if (rebuildRegionRange)
            {
                set.PageSnapshot.Values[KEY_HOLE_LINES_TEXT] = SerializeAnchoredHoleLinesFromRegion(set, holeLocalIndices);
            }

            ResolveDrillAndUpdateStatus(set, GcodeLines);
        }

        private static string SerializeAnchoredHoleLinesFromRegion(RegionSet set, List<int> holeLocalIndices)
        {
            if (set == null || set.RegionLines == null || set.RegionLines.Count == 0)
                return "";

            if (holeLocalIndices == null || holeLocalIndices.Count == 0)
                return "";

            var sb = new StringBuilder(1024);

            for (int i = 0; i < holeLocalIndices.Count; i++)
            {
                int local = holeLocalIndices[i];
                if (local < 0 || local >= set.RegionLines.Count)
                    continue;

                string s = set.RegionLines[local] ?? "";
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                sb.AppendLine(s);
            }

            return sb.ToString().Trim();
        }

        // ============================================================
        // Region resolve
        //
        // VALID RULE (as you stated):
        // - RegionLines block must match the editor uniquely.
        // - DrillDepthLineText AND all HoleLineTexts must be found uniquely within that block.
        // ============================================================
        private void ResolveDrillAndUpdateStatus(RegionSet set, List<string> allLines)
        {
            if (set == null)
                return;

            allLines ??= new List<string>();

            if (set.RegionLines == null || set.RegionLines.Count == 0)
            {
                set.Status = RegionResolveStatus.Unset;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            bool found = BuiltRegionSearches.FindMultiLine(allLines, set.RegionLines, out int start, out int end, out int matchCount);

            if (!found || matchCount == 0)
            {
                set.Status = RegionResolveStatus.Missing;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            if (matchCount > 1)
            {
                set.Status = RegionResolveStatus.Ambiguous;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            // Must have depth + holes stored to be "complete"
            string depthTok = GetSnapshotOrDefault(set, KEY_DRILL_DEPTH_TEXT, "");
            var holeToks = ReadHoleTokensFromSnapshot(set);

            if (string.IsNullOrWhiteSpace(depthTok) || holeToks.Count == 0)
            {
                set.Status = RegionResolveStatus.Unset;
                set.ResolvedStartLine = start;
                set.ResolvedEndLine = end;
                return;
            }

            // Depth must resolve uniquely inside the matched region
            int depthIdx = FindSingleInRangeUnique(allLines, depthTok, start, end, out RegionResolveStatus depthStatus);
            if (depthStatus != RegionResolveStatus.Ok)
            {
                set.Status = depthStatus;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            // Every hole must resolve uniquely inside the matched region
            for (int i = 0; i < holeToks.Count; i++)
            {
                int hIdx = FindSingleInRangeUnique(allLines, holeToks[i], start, end, out RegionResolveStatus hStatus);
                if (hStatus != RegionResolveStatus.Ok)
                {
                    set.Status = hStatus;
                    set.ResolvedStartLine = null;
                    set.ResolvedEndLine = null;
                    return;
                }
            }

            set.Status = RegionResolveStatus.Ok;
            set.ResolvedStartLine = start;
            set.ResolvedEndLine = end;
        }

        private int FindSingleInRangeUnique(IReadOnlyList<string> allLines, string keyText, int rangeStart, int rangeEnd, out RegionResolveStatus status)
        {
            status = RegionResolveStatus.Missing;

            if (allLines == null || allLines.Count == 0)
                return -1;

            if (rangeStart < 0) rangeStart = 0;
            if (rangeEnd < 0 || rangeEnd >= allLines.Count) rangeEnd = allLines.Count - 1;
            if (rangeEnd < rangeStart)
                return -1;

            int foundIndex = -1;
            int count = 0;

            for (int i = rangeStart; i <= rangeEnd; i++)
            {
                if (BuiltRegionSearches.FindKeyMatch(allLines[i] ?? "", keyText ?? ""))
                {
                    count++;
                    if (count == 1)
                        foundIndex = i;
                }
            }

            if (count == 0)
            {
                status = RegionResolveStatus.Missing;
                return -1;
            }

            if (count > 1)
            {
                status = RegionResolveStatus.Ambiguous;
                return -1;
            }

            status = RegionResolveStatus.Ok;
            return foundIndex;
        }

        // ============================================================
        // Snapshot helpers
        // ============================================================
        private static void EnsureSnapshot(RegionSet set)
        {
            set.PageSnapshot ??= new UiStateSnapshot();
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

        private List<string> ReadHoleTokensFromSnapshot(RegionSet set)
        {
            string raw = GetSnapshotOrDefault(set, KEY_HOLE_LINES_TEXT, "");
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        // ============================================================
        // Editor sync + highlighting
        // ============================================================
        private void SyncGcodeLinesFromEditor()
        {
            if (GcodeEditor == null)
                return;

            TextRange tr = new TextRange(GcodeEditor.Document.ContentStart, GcodeEditor.Document.ContentEnd);
            string allText = tr.Text.Replace("\r\n", "\n");

            GcodeLines.Clear();

            using (StringReader reader = new StringReader(allText))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line;

                    // Strip "   27: " prefix if present (first colon near start)
                    int colonIndex = trimmed.IndexOf(':');
                    if (colonIndex >= 0 && colonIndex < 10)
                        trimmed = trimmed[(colonIndex + 1)..].TrimStart();

                    GcodeLines.Add(trimmed);
                }
            }
        }

        private void RefreshHighlighting()
        {
            // Shift held: do nothing (matches current debug behavior)
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                return;

            if (GcodeEditor == null || GcodeLines == null)
                return;

            UiUtilities.ForceLinesUppercaseInPlace(GcodeLines);

            var rtb = GcodeEditor;
            rtb.Document.Blocks.Clear();

            for (int i = 0; i < GcodeLines.Count; i++)
            {
                string line = GcodeLines[i] ?? string.Empty;

                Brush fg = (i == _selectedLineIndex) ? Brushes.Blue : Brushes.Black;
                Brush bg = Brushes.Transparent;

                if (i == _depthLineIndex)
                {
                    bg = Brushes.Red;
                }
                else
                {
                    bool isHole = false;
                    for (int h = 0; h < _holePoints.Count; h++)
                    {
                        if (_holePoints[h].LineIndex == i)
                        {
                            isHole = true;
                            break;
                        }
                    }

                    if (isHole)
                        bg = Brushes.Yellow;
                }

                Paragraph p = new Paragraph { Margin = new Thickness(0), Background = bg };
                p.Tag = i;
                p.MouseLeftButtonDown += GcodeLine_MouseLeftButtonDown;

                UiUtilities.AddNumberedLinePrefix(p, i + 1, fg);
                p.Inlines.Add(new Run(line) { Foreground = fg });

                rtb.Document.Blocks.Add(p);
            }

            UiUtilities.RebuildAndStoreNumberedLineStartIndex(rtb);
        }

        private void GcodeLine_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SyncGcodeLinesFromEditor();

            if (sender is Paragraph p && p.Tag is int lineIndex)
            {
                _selectedLineIndex = lineIndex;

                if (_holeAddMode)
                    TryAddHoleFromLine(lineIndex);
                else
                    RefreshHighlighting();
            }
        }

        // ============================================================
        // Depth + holes selection UI
        // ============================================================
        private void BtnDrillDepthLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedLineIndex < 0)
                {
                    MessageBox.Show("Click a G-code line in the editor first.");
                    return;
                }

                if (_selectedLineIndex >= GcodeLines.Count)
                {
                    MessageBox.Show("Selected line index is out of range.");
                    return;
                }

                string line = GcodeLines[_selectedLineIndex];

                if (!TryGetCoord(line, 'Z', out double zValue))
                {
                    MessageBox.Show("Selected line does not contain a valid Z value.");
                    return;
                }

                _depthZ = zValue;
                _depthLineIndex = _selectedLineIndex;

                BtnDrillDepthLine.Content = $"Drill Depth ={_depthZ.ToString("0.###", CultureInfo.InvariantCulture)}";

                RefreshHighlighting();

                // Selection changed -> rebuild region range + anchored keys
                StoreDrillSelectionIntoSelectedSet(rebuildRegionRange: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Drill Z Depth Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAddHoleLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _holeAddMode = !_holeAddMode;
                UpdateHoleAddButtonUI();
                RefreshHighlighting();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Add Hole Line Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnResetHoleLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _holePoints.Clear();
                if (LstHoleLines != null)
                    LstHoleLines.Items.Clear();

                _holeAddMode = false;
                UpdateHoleAddButtonUI();

                ResetCarryState();
                RefreshHighlighting();

                StoreDrillSelectionIntoSelectedSet(rebuildRegionRange: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Reset Holes Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDelHoleLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedLineIndex < 0)
                {
                    MessageBox.Show("Click a G-code line in the editor first (the hole line you want to delete).", "Delete Hole", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int removed = _holePoints.RemoveAll(h => h.LineIndex == _selectedLineIndex);

                if (removed == 0)
                {
                    MessageBox.Show($"No hole is stored for the currently selected G-code line {_selectedLineIndex + 1}.", "Delete Hole", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                RebuildHoleListUI();
                ResetCarryStateFromLastHole();
                RefreshHighlighting();

                StoreDrillSelectionIntoSelectedSet(rebuildRegionRange: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Delete Hole Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateHoleAddButtonUI()
        {
            if (BtnAddHoleLine == null)
                return;

            if (_holeAddMode)
            {
                BtnAddHoleLine.Background = Brushes.Orange;
                BtnAddHoleLine.Content = "Stop Add";
            }
            else
            {
                BtnAddHoleLine.Background = UiUtilities.HexBrush("#FF007ACC");
                BtnAddHoleLine.Content = "Start Add";
            }
        }

        private void RebuildHoleListUI()
        {
            if (LstHoleLines == null)
                return;

            var inv = CultureInfo.InvariantCulture;
            LstHoleLines.Items.Clear();

            for (int i = 0; i < _holePoints.Count; i++)
            {
                var hp = _holePoints[i];
                string formatted = $"{hp.X.ToString("0.###", inv)} {hp.Y.ToString("0.###", inv)}";
                LstHoleLines.Items.Add(formatted);
            }
        }

        private void AddMissingHoleToUI(string tok)
        {
            if (LstHoleLines == null)
                return;

            // For display, strip the anchor for readability
            string clean = BuiltRegionSearches.NormalizeRemoveUid(tok ?? "");
            if (string.IsNullOrWhiteSpace(clean))
                clean = tok ?? "";

            LstHoleLines.Items.Add($"!! MISSING: {clean}");
        }

        // ============================================================
        // Hole parsing + carry-forward
        // ============================================================
        private void ResetCarryState()
        {
            _hasLastHoleCart = false;
            _lastHoleX = 0.0;
            _lastHoleY = 0.0;

            _hasLastHolePolar = false;
            _lastPolarDiam = 0.0;
            _lastPolarAngleDeg = 0.0;
        }

        private void ResetCarryStateFromLastHole()
        {
            if (_holePoints.Count == 0)
            {
                ResetCarryState();
                return;
            }

            var last = _holePoints[_holePoints.Count - 1];
            _lastHoleX = last.X;
            _lastHoleY = last.Y;
            _hasLastHoleCart = true;

            // Polar carry resets (we don't store diam/angle per hole)
            _hasLastHolePolar = false;
            _lastPolarDiam = 0.0;
            _lastPolarAngleDeg = 0.0;
        }

        private void TryAddHoleFromLine(int lineIndex)
        {
            if (GcodeLines == null || lineIndex < 0 || lineIndex >= GcodeLines.Count)
                return;

            // Prevent duplicates (same line index)
            for (int i = 0; i < _holePoints.Count; i++)
            {
                if (_holePoints[i].LineIndex == lineIndex)
                {
                    RefreshHighlighting();
                    return;
                }
            }

            string line = GcodeLines[lineIndex] ?? string.Empty;

            if (!TryBuildHoleXYFromLine(line, out double xCart, out double yCart, out string err))
            {
                MessageBox.Show(err, "Add Hole", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshHighlighting();
                return;
            }

            _holePoints.Add(new HolePoint { LineIndex = lineIndex, X = xCart, Y = yCart });

            if (LstHoleLines != null)
            {
                var inv = CultureInfo.InvariantCulture;
                string formatted = $"{xCart.ToString("0.###", inv)} {yCart.ToString("0.###", inv)}";
                LstHoleLines.Items.Add(formatted);
            }

            RefreshHighlighting();

            // Selection changed -> rebuild region range + anchored keys
            StoreDrillSelectionIntoSelectedSet(rebuildRegionRange: true);
        }

        private void AddHoleFromLine_NoUI(int lineIndex)
        {
            if (GcodeLines == null || lineIndex < 0 || lineIndex >= GcodeLines.Count)
                return;

            string line = GcodeLines[lineIndex] ?? string.Empty;

            if (!TryBuildHoleXYFromLine(line, out double xCart, out double yCart, out _))
                return;

            _holePoints.Add(new HolePoint { LineIndex = lineIndex, X = xCart, Y = yCart });

            if (LstHoleLines != null)
            {
                var inv = CultureInfo.InvariantCulture;
                string formatted = $"{xCart.ToString("0.###", inv)} {yCart.ToString("0.###", inv)}";
                LstHoleLines.Items.Add(formatted);
            }
        }

        private bool TryBuildHoleXYFromLine(string line, out double xCart, out double yCart, out string error)
        {
            xCart = 0;
            yCart = 0;
            error = "";

            if (RadCartesian?.IsChecked == true)
            {
                bool hasX = TryGetCoord(line, 'X', out double xVal);
                bool hasY = TryGetCoord(line, 'Y', out double yVal);

                if (!hasX && !hasY)
                {
                    error = "Selected line does not contain X or Y (Cartesian).";
                    return false;
                }

                if (!hasX)
                {
                    if (!_hasLastHoleCart)
                    {
                        error = "X is missing and there is no previous hole to copy from.";
                        return false;
                    }
                    xVal = _lastHoleX;
                }

                if (!hasY)
                {
                    if (!_hasLastHoleCart)
                    {
                        error = "Y is missing and there is no previous hole to copy from.";
                        return false;
                    }
                    yVal = _lastHoleY;
                }

                xCart = xVal;
                yCart = yVal;

                _lastHoleX = xCart;
                _lastHoleY = yCart;
                _hasLastHoleCart = true;
                return true;
            }

            // Polar: X = diameter, C = angle (deg)
            {
                bool hasDiam = TryGetCoord(line, 'X', out double diam);
                bool hasAngle = TryGetCoord(line, 'C', out double angleDeg);

                if (!hasDiam && !hasAngle)
                {
                    error = "Selected line does not contain X(diam) or C(angle) (Polar).";
                    return false;
                }

                if (!hasDiam)
                {
                    if (!_hasLastHolePolar)
                    {
                        error = "X(diam) is missing and there is no previous polar hole to copy from.";
                        return false;
                    }
                    diam = _lastPolarDiam;
                }

                if (!hasAngle)
                {
                    if (!_hasLastHolePolar)
                    {
                        error = "C(angle) is missing and there is no previous polar hole to copy from.";
                        return false;
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

                return true;
            }
        }

        // ============================================================
        // Coord parsing helpers
        // ============================================================
        private bool TryGetCoord(string line, char axis, out double value)
        {
            value = double.NaN;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            axis = char.ToUpperInvariant(axis);

            int idx = line.IndexOf(axis);
            if (idx < 0)
                return false;

            idx++;

            int end = idx;
            while (end < line.Length)
            {
                char c = line[end];
                if ((c >= '0' && c <= '9') || c == '.' || c == '-' || c == '+')
                {
                    end++;
                    continue;
                }
                break;
            }

            if (end <= idx)
                return false;

            string num = line.Substring(idx, end - idx);
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // ============================================================
        // Export + viewer (kept compatible)
        // ============================================================
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

        // BATCH EXPORT API (called by MainWindow ExportAll)
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

                ResolveDrillAndUpdateStatus(set, GcodeLines);
                if (set.Status != RegionResolveStatus.Ok)
                {
                    failReason = $"Status is {set.Status}.";
                    return false;
                }

                if (!TryBuildHoleGroupFromSet(set, GcodeLines, out DrillViewWindowV2.HoleGroup? grp, out string reason))
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

                string safe = MainWindow.SanitizeFileStem(set.Name);
                string txtPath = System.IO.Path.Combine(exportDir, $"{safe}_holes.txt");
                string stepPath = System.IO.Path.Combine(exportDir, $"{safe}_Holes_stp.stp");

                var rawTxtLines = new List<string>
                {
                    $"PARAMS {grp.HoleDia.ToString("0.###", inv)} {grp.ZHoleTop.ToString("0.###", inv)} {grp.PointAngle.ToString("0.###", inv)} {grp.ChamferLen.ToString("0.###", inv)} {grp.ZPlusExt.ToString("0.###", inv)} {grp.DrillZApex.ToString("0.###", inv)}"
                };

                foreach (var h in grp.Holes)
                    rawTxtLines.Add($"{h.X.ToString("0.###", inv)} {h.Y.ToString("0.###", inv)}");

                if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
                    File.WriteAllLines(txtPath, rawTxtLines);

                // Workaround values (kept)
                double chExtended = grp.ChamferLen + .1;
                double holeTopExtended = grp.ZHoleTop + .1;

                FreeCadScriptDrill.HoleShape = $@"
hole_dia    = {grp.HoleDia.ToString("0.###", inv)}
z_hole_top  = {holeTopExtended.ToString("0.###", inv)}
point_angle = {grp.PointAngle.ToString("0.###", inv)}
chamfer_len = {chExtended.ToString("0.###", inv)}
z_plus_ext  = {grp.ZPlusExt.ToString("0.###", inv)}
drill_z     = {grp.DrillZApex.ToString("0.###", inv)}
";

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

                Main.ExportAllCreatedStepFiles.Add(stepPath);
                return true;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                return false;
            }
        }

        // Single-export fallback (current selection state)
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

                if (double.IsNaN(_depthZ))
                {
                    failReason = "Drill depth (apex Z) is not set.";
                    return false;
                }

                if (_holePoints.Count == 0)
                {
                    failReason = "No holes selected.";
                    return false;
                }

                Directory.CreateDirectory(exportDir);

                string txtPath = System.IO.Path.Combine(exportDir, fileStem + "_holes.txt");
                string stepPath = System.IO.Path.Combine(exportDir, fileStem + "_Holes_stp.stp");

                var rawTxtLines = new List<string>
                {
                    $"PARAMS {holeDia.ToString("0.###", inv)} {zHoleTop.ToString("0.###", inv)} {pointAngle.ToString("0.###", inv)} {chamferLen.ToString("0.###", inv)} {zPlusExt.ToString("0.###", inv)} {_depthZ.ToString("0.###", inv)}"
                };

                foreach (var h in _holePoints)
                    rawTxtLines.Add($"{h.X.ToString("0.###", inv)} {h.Y.ToString("0.###", inv)}");

                if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
                    File.WriteAllLines(txtPath, rawTxtLines);

                double chExtended = chamferLen + .1;
                double holeTopExtended = zHoleTop + .1;

                FreeCadScriptDrill.HoleShape = $@"
hole_dia    = {holeDia.ToString("0.###", inv)}
z_hole_top  = {holeTopExtended.ToString("0.###", inv)}
point_angle = {pointAngle.ToString("0.###", inv)}
chamfer_len = {chExtended.ToString("0.###", inv)}
z_plus_ext  = {zPlusExt.ToString("0.###", inv)}
drill_z     = {_depthZ.ToString("0.###", inv)}
";

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
                foreach (var h in _holePoints)
                    sbPos.AppendLine($"    ({h.X.ToString("0.###", inv)}, {h.Y.ToString("0.###", inv)}),");
                sbPos.AppendLine("]");
                FreeCadScriptDrill.Positions = sbPos.ToString();

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

                if (!batchMode)
                    Main.ExportAllCreatedStepFiles.Add(stepPath);

                return true;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                return false;
            }
        }

        // Viewer helper: re-resolve set before show
        private void ResyncSelectedDrillSetBeforeViewer()
        {
            var set = GetSelectedDrillSetSafe();
            if (set == null)
                return;

            ApplyDrillSet(set);
        }

        private void BtnViewProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiUtilities.CloseAllToolWindows();
                ResyncSelectedDrillSetBeforeViewer();
                SyncGcodeLinesFromEditor();

                var set = GetSelectedDrillSetSafe();
                if (set == null)
                    return;

                if (!TryBuildHoleGroupFromSet(set, GcodeLines, out DrillViewWindowV2.HoleGroup? grp, out string why))
                    throw new Exception(why);

                var win = new DrillViewWindowV2(new List<DrillViewWindowV2.HoleGroup> { grp! });

                var owner = Window.GetWindow(this);
                if (owner != null)
                    win.Owner = owner;

                win.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Drill Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void BtnViewAllDrilling_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiUtilities.CloseAllToolWindows();
                SyncGcodeLinesFromEditor();

                var sets = Main.DrillSets;
                if (sets == null || sets.Count == 0)
                {
                    MessageBox.Show("No drill sets exist.", "Drill View All", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var groups = new List<DrillViewWindowV2.HoleGroup>();
                var skipped = new List<string>();

                foreach (var set in sets)
                {
                    if (set == null)
                        continue;

                    if (!set.ShowInViewAll)
                        continue;

                    ResolveDrillAndUpdateStatus(set, GcodeLines);
                    if (set.Status != RegionResolveStatus.Ok)
                    {
                        skipped.Add($"{set.Name}: {set.StatusText}");
                        continue;
                    }

                    if (TryBuildHoleGroupFromSet(set, GcodeLines, out DrillViewWindowV2.HoleGroup? grp, out string why))
                    {
                        if (grp != null)
                            groups.Add(grp);
                        else
                            skipped.Add($"{set.Name}: {why}");
                    }
                    else
                    {
                        skipped.Add($"{set.Name}: {why}");
                    }
                }

                if (groups.Count == 0)
                {
                    MessageBox.Show("No drill sets could be displayed.\n\n" + string.Join("\n", skipped), "Drill View All", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var win = new DrillViewWindowV2(groups);

                var owner = Window.GetWindow(this);
                if (owner != null)
                    win.Owner = owner;

                win.Show();

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
                MessageBox.Show(ex.Message, "Drill View All", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // Build export/view data from a stored set
        // ============================================================
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
                reason = "No region lines saved in this set.";
                return false;
            }

            if (!BuiltRegionSearches.FindMultiLine(allLines, set.RegionLines, out int rStart, out int rEnd, out int matchCount) || matchCount != 1)
            {
                reason = (matchCount > 1) ? "Region text matches multiple times (ambiguous)." : "Region text not found in current G-code.";
                return false;
            }

            // Scalars
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

            if (!TryParseInvariantDouble(GetSnapshotOrDefault(set, KEY_POINT_ANGLE, ""), out double pointAngle) || pointAngle <= 0.0 || pointAngle > 180.0)
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

            // Depth
            string depthTok = GetSnapshotOrDefault(set, KEY_DRILL_DEPTH_TEXT, "");
            if (string.IsNullOrWhiteSpace(depthTok))
            {
                reason = "Drill depth line is not set for this drill set.";
                return false;
            }

            int depthIdx = FindSingleInRangeUnique(allLines, depthTok, rStart, rEnd, out RegionResolveStatus depthStatus);
            if (depthStatus != RegionResolveStatus.Ok || depthIdx < 0 || depthIdx >= allLines.Count)
            {
                reason = "Drill depth line could not be uniquely resolved in the region.";
                return false;
            }

            if (!TryGetCoord(allLines[depthIdx], 'Z', out double drillZApex) || double.IsNaN(drillZApex))
            {
                reason = "Resolved drill depth line does not contain a valid Z value.";
                return false;
            }

            // Coord mode
            string mode = GetSnapshotOrDefault(set, KEY_COORD_MODE, "Cartesian");
            bool isPolar = mode.Equals("Polar", StringComparison.OrdinalIgnoreCase);

            // Holes (anchored tokens)
            var holeTokens = ReadHoleTokensFromSnapshot(set);
            if (holeTokens.Count == 0)
            {
                reason = "No hole lines saved in this set.";
                return false;
            }

            bool hasLastCart = false;
            double lastX = 0.0, lastY = 0.0;

            bool hasLastPolar = false;
            double lastDiam = 0.0, lastAngle = 0.0;

            var holes = new List<(double X, double Y, int LineIndex)>();

            for (int i = 0; i < holeTokens.Count; i++)
            {
                string tok = holeTokens[i];

                int idx = FindSingleInRangeUnique(allLines, tok, rStart, rEnd, out RegionResolveStatus hStatus);
                if (hStatus != RegionResolveStatus.Ok || idx < 0 || idx >= allLines.Count)
                {
                    reason = $"A hole line could not be uniquely resolved in the region (item #{i + 1}).";
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

            Main.TryGetTransformForRegion(
    set.Name,
    out double rotYDeg,
    out double rotZDeg,
    out double tx,
    out double ty,
    out double tz,
    out string matrixName);

            // Ignore RotY unless it is exactly 180 (within tolerance)
            static double Norm360(double deg)
            {
                if (!double.IsFinite(deg))
                    return 0.0;

                deg %= 360.0;
                if (deg < 0.0) deg += 360.0;
                return deg;
            }

            static bool IsRotY180(double deg)
            {
                double a = Norm360(deg);
                return Math.Abs(a - 180.0) < 1e-3;
            }

            double rotYForView = IsRotY180(rotYDeg) ? 180.0 : 0.0;

            group = new DrillViewWindowV2.HoleGroup
            {
                GroupName = string.IsNullOrWhiteSpace(set.Name) ? "(unnamed)" : set.Name.Trim(),
                HoleDia = holeDia,
                ZHoleTop = zHoleTop,
                PointAngle = pointAngle,
                ChamferLen = chamferLen,
                ZPlusExt = zPlusExt,
                DrillZApex = drillZApex,
                Holes = holes,

                // Matrix metadata for viewer display transform
                RegionName = set.Name ?? "",
                MatrixName = matrixName ?? "",
                RotZDeg = rotZDeg,

                // IMPORTANT: RotY ignored unless 180
                RotYDeg = rotYForView,

                Tx = tx,
                Ty = ty,
                Tz = tz
            };

            return true;


            return true;


            return true;
        }

        private bool TryParseInvariantDouble(string s, out double v)
        {
            var inv = CultureInfo.InvariantCulture;
            return double.TryParse(s ?? "", NumberStyles.Float, inv, out v);
        }
    }
}
