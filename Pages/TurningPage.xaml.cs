using CNC_Improvements_gcode_solids.FreeCadIntegration;
using CNC_Improvements_gcode_solids.Properties;
using CNC_Improvements_gcode_solids.SetManagement;
using CNC_Improvements_gcode_solids.TurningHelpers;
using CNC_Improvements_gcode_solids.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Pages
{
    public partial class TurningPage : Page, IGcodePage
    {

        private int startXIndex = -1;
        private int startZIndex = -1;
        private int endXIndex = -1;
        private int endZIndex = -1;
        private int selectedLineIndex = -1;
        private bool SelectOrderWrong = false;

        // guards so ApplyTurnSet doesn't cause recursive updates via TextChanged etc
        private bool _isApplyingTurnSet = false;

        // Modal state for motion mode
        private enum MotionMode { None, G1, G2, G3 }

        // Parsed geometry entities
        private class GeoMove
        {
            public string Type = "";    // "LINE", "ARC_CW", "ARC_CCW"
            public double Xs, Zs;
            public double Xe, Ze;
            public double I, K;    // center offsets (if arc)
            public double R;       // radius (if arc R-mode)
        }



        private class ToolComp
        {
            public string Usage = "OFF";    // "LEFT", "OFF", "RIGHT"
            public double Rad = 1.0;        // default nose radius
            public int Quadrant = 3;        // 1..9
        }

        private ToolComp _toolComp = new ToolComp();

        // Snapshot keys (non-control-name keys)
        private const string KEY_TOOL_USAGE = "__ToolUsage";
        private const string KEY_QUADRANT = "__Quadrant";
        private const string KEY_STARTX = "__StartXLine";
        private const string KEY_STARTZ = "__StartZLine";
        private const string KEY_ENDX = "__EndXLine";
        private const string KEY_ENDZ = "__EndZLine";

        public TurningPage()
        {
            // Prevent InitializeComponent() default XAML values from writing into the store
            // via TextChanged handlers during construction.
            _isApplyingTurnSet = true;
            InitializeComponent();
            _isApplyingTurnSet = false;
        }

        // -------------------------
        // Access to MainWindow model
        // -------------------------
        private MainWindow GetMain()
        {
            return Application.Current.MainWindow as MainWindow
                   ?? throw new InvalidOperationException("MainWindow not available.");
        }

        private List<string> GetGcodeLines() => GetMain().GcodeLines;

        private RichTextBox GetGcodeEditor() => GetMain().GcodeEditor;

        // ============================================================
        // REGION RESOLVE (ONLY via TextSearching)
        // ============================================================
        private static void ResolveAndUpdateStatus(RegionSet set, List<string> allLines)
        {
            if (set == null)
                return;

            if (set.RegionLines == null || set.RegionLines.Count == 0)
            {
                set.Status = RegionResolveStatus.Unset;
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            bool any = CNC_Improvements_gcode_solids.SetManagement.Builders.BuiltRegionSearches.FindMultiLine(
                allLines,
                set.RegionLines,
                out int s,
                out int e,
                out int matchCount);

            if (!any || matchCount == 0)
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

            set.Status = RegionResolveStatus.Ok;
            set.ResolvedStartLine = s;
            set.ResolvedEndLine = e;
        }

        // ============================================================
        // UNIVERSAL: Apply Turn Set (called from MainWindow)
        // ============================================================
        public void ApplyTurnSet(RegionSet? set)
        {
            // null set = do nothing destructive
            if (set == null)
            {
                RefreshButtonNames();
                RefreshHighlighting();
                return;
            }

            // Always match against latest edits before searching
            SyncGcodeLinesFromEditor();
            var lines = GetGcodeLines();

            _isApplyingTurnSet = true;
            try
            {
                // Apply generic snapshot (best-effort)
                set.PageSnapshot?.ApplyTo(this);

                // HARD RULE: known Turning controls must be set from STORE explicitly
                TxtZExt.Text = GetSnap(set, "TxtZExt", TxtZExt?.Text ?? "");
                NRad.Text = GetSnap(set, "NRad", NRad?.Text ?? "");

                // Apply tool usage/quadrant (stored as keys, not button controls)
                string usage = GetSnap(set, KEY_TOOL_USAGE, "OFF").ToUpperInvariant();
                int quadrant = ParseIntOrDefault(GetSnap(set, KEY_QUADRANT, "3"), 3);

                _toolComp.Usage = usage;
                _toolComp.Quadrant = quadrant;

                UpdateToolUsageButtonVisuals(_toolComp.Usage);
                UpdateQuadrantButtonVisuals(_toolComp.Quadrant);

                // Resolve region lines against current editor (RegionLines are stored normalized)
                ResolveAndUpdateStatus(set, lines);

                // Rematch marker indices (stored normalized, including unique tag)
                string sxText = GetSnap(set, KEY_STARTX, "");
                string szText = GetSnap(set, KEY_STARTZ, "");
                string exText = GetSnap(set, KEY_ENDX, "");
                string ezText = GetSnap(set, KEY_ENDZ, "");

                int rangeStart = set.ResolvedStartLine ?? -1;
                int rangeEnd = set.ResolvedEndLine ?? -1;

                // Start markers: first match in range
                startXIndex = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, sxText, rangeStart, rangeEnd, preferLast: false);
                startZIndex = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, szText, rangeStart, rangeEnd, preferLast: false);

                // End markers: last match in range (fixes "same line" when start/end text identical)
                endXIndex = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, exText, rangeStart, rangeEnd, preferLast: true);
                endZIndex = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, ezText, rangeStart, rangeEnd, preferLast: true);

                // If region isn't resolved, fall back to global search for markers (best-effort)
                if (rangeStart < 0 || rangeEnd < 0)
                {
                    startXIndex = (startXIndex >= 0) ? startXIndex : SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, sxText, -1, -1, preferLast: false);
                    startZIndex = (startZIndex >= 0) ? startZIndex : SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, szText, -1, -1, preferLast: false);

                    endXIndex = (endXIndex >= 0) ? endXIndex : SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, exText, -1, -1, preferLast: true);
                    endZIndex = (endZIndex >= 0) ? endZIndex : SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, ezText, -1, -1, preferLast: true);
                }
            }
            finally
            {
                _isApplyingTurnSet = false;
            }

            RefreshButtonNames();
            RefreshHighlighting();
        }

        private static int ParseIntOrDefault(string s, int def)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
            return def;
        }

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

        private void UpdateToolUsageButtonVisuals(string usageUpper)
        {
            var baseBrush = UiUtilities.HexBrush("#FFE65CD9");

            G41.Background = baseBrush;
            G40.Background = baseBrush;
            G42.Background = baseBrush;

            if (string.Equals(usageUpper, "LEFT", StringComparison.OrdinalIgnoreCase))
                G41.Background = Brushes.Yellow;
            else if (string.Equals(usageUpper, "RIGHT", StringComparison.OrdinalIgnoreCase))
                G42.Background = Brushes.Yellow;
            else
                G40.Background = Brushes.Yellow;
        }

        private void UpdateQuadrantButtonVisuals(int quadrant)
        {
            var baseBrush = UiUtilities.HexBrush("#FF19CE24");

            Quad1.Background = baseBrush;
            Quad2.Background = baseBrush;
            Quad3.Background = baseBrush;
            Quad4.Background = baseBrush;
            Quad5.Background = baseBrush;
            Quad6.Background = baseBrush;
            Quad7.Background = baseBrush;
            Quad8.Background = baseBrush;
            Quad9.Background = baseBrush;

            switch (quadrant)
            {
                case 1: Quad1.Background = Brushes.Yellow; break;
                case 2: Quad2.Background = Brushes.Yellow; break;
                case 3: Quad3.Background = Brushes.Yellow; break;
                case 4: Quad4.Background = Brushes.Yellow; break;
                case 5: Quad5.Background = Brushes.Yellow; break;
                case 6: Quad6.Background = Brushes.Yellow; break;
                case 7: Quad7.Background = Brushes.Yellow; break;
                case 8: Quad8.Background = Brushes.Yellow; break;
                case 9: Quad9.Background = Brushes.Yellow; break;
            }
        }

        // ============================================================
        // UNIVERSAL: Push current UI + marker picks into selected TurnSet
        // ============================================================
        private RegionSet? GetSelectedTurnSetSafe() => GetMain().SelectedTurnSet;

        // ============================================================
        // NEW: Better store of region text (rebuild RegionLines) BEFORE
        // view/export/switch — but ONLY if region resolves OK.
        // If region cannot resolve, we do nothing (user must fix markers).
        // ============================================================
        private bool TryStoreSelectedTurnSetRegionFromEditor(out string reason)
        {
            reason = "";

            if (_isApplyingTurnSet)
                return true;

            // Always pull latest editor text first
            SyncGcodeLinesFromEditor();

            var set = GetSelectedTurnSetSafe();
            if (set == null)
                return true;

            // Must have marker picks and valid order
            if ((startXIndex < 0 && startZIndex < 0) || (endXIndex < 0 && endZIndex < 0) || SelectOrderWrong)
            {
                reason = "Region markers are not set (or selection order is invalid).";
                ResolveAndUpdateStatus(set, GetGcodeLines());
                return false;
            }

            // IMPORTANT:
            // On View/Export clicks we MUST rebuild RegionLines from the current marker indices,
            // even if the previous stored RegionLines no longer resolve (user edited inside region).
            StoreSelectionIntoSelectedSet();

            // Update resolve status after rebuild
            ResolveAndUpdateStatus(set, GetGcodeLines());
            return true;
        }

        private bool TryRebuildTurnSetRegionFromStoredMarkers(RegionSet set, List<string> allLines, out string reason)
        {
            reason = "";

            if (set == null)
            {
                reason = "Set is null.";
                return false;
            }

            if (allLines == null || allLines.Count == 0)
            {
                reason = "No G-code lines.";
                return false;
            }

            // Marker texts are stored normalized in snapshot keys
            string sxText = GetSnap(set, KEY_STARTX, "");
            string szText = GetSnap(set, KEY_STARTZ, "");
            string exText = GetSnap(set, KEY_ENDX, "");
            string ezText = GetSnap(set, KEY_ENDZ, "");

            if (string.IsNullOrWhiteSpace(sxText) || string.IsNullOrWhiteSpace(szText) ||
                string.IsNullOrWhiteSpace(exText) || string.IsNullOrWhiteSpace(ezText))
            {
                reason = "Marker texts missing in snapshot (Start/End X/Z).";
                return false;
            }

            // Find markers globally (best-effort)
            int sx = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(allLines, sxText, -1, -1, preferLast: false);
            int sz = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(allLines, szText, -1, -1, preferLast: false);
            int ex = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(allLines, exText, -1, -1, preferLast: true);
            int ez = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(allLines, ezText, -1, -1, preferLast: true);

            if (sx < 0 || sz < 0 || ex < 0 || ez < 0)
            {
                reason = $"Marker not found. sx={sx}, sz={sz}, ex={ex}, ez={ez}";
                return false;
            }

            int startAbs = Math.Min(sx, sz);
            int endAbs = Math.Max(ex, ez);

            if (endAbs < startAbs)
            {
                reason = "Marker order invalid (end above start).";
                return false;
            }

            if (startAbs < 0 || endAbs >= allLines.Count)
            {
                reason = "Marker indices out of range.";
                return false;
            }

            // Extract raw region from current editor model
            var regionRaw = allLines.GetRange(startAbs, endAbs - startAbs + 1);

            // Convert absolute marker indices -> local indices inside regionRaw
            int localSX = sx - startAbs;
            int localSZ = sz - startAbs;
            int localEX = ex - startAbs;
            int localEZ = ez - startAbs;

            // Pull turning params from snapshot (or defaults)
            string usage = GetSnap(set, KEY_TOOL_USAGE, "OFF");
            string quad = GetSnap(set, KEY_QUADRANT, "3");
            string zExt = GetSnap(set, "TxtZExt", TxtZExt?.Text ?? "");
            string nRad = GetSnap(set, "NRad", NRad?.Text ?? "");

            // Build via canonical builder
            CNC_Improvements_gcode_solids.SetManagement.Builders.BuildTurnRegion.EditExisting(
    rs: set,
    regionLines: regionRaw,
    startXIndex: localSX,
    startZIndex: localSZ,
    endXIndex: localEX,
    endZIndex: localEZ,
    toolUsage: usage,
    quadrant: quad,
    txtZExt: zExt,
    nRad: nRad,
    snapshotDefaults: null
);

            // Resolve after rebuild
            ResolveAndUpdateStatus(set, allLines);
            return true;
        }

        private void StoreTurnParamsIntoSelectedSet()
        {
            if (_isApplyingTurnSet) return;

            var set = GetSelectedTurnSetSafe();
            if (set == null)
                return;

            // ALL WRITES go through the canonical editor
            CNC_Improvements_gcode_solids.SetManagement.Builders.BuildTurnRegion.EditExisting(
                rs: set,
                regionLines: null,              // leave RegionLines unchanged
                startXIndex: null,
                startZIndex: null,
                endXIndex: null,
                endZIndex: null,
                toolUsage: _toolComp.Usage ?? "OFF",
                quadrant: _toolComp.Quadrant.ToString(CultureInfo.InvariantCulture),
                txtZExt: TxtZExt?.Text ?? "",
                nRad: NRad?.Text ?? "",
                snapshotDefaults: null
            );

            ResolveAndUpdateStatus(set, GetGcodeLines());
        }

        // -------------------------
        // IGcodePage
        // -------------------------
        public void OnGcodeModelLoaded()
        {
            startXIndex = -1;
            startZIndex = -1;
            endXIndex = -1;
            endZIndex = -1;
            selectedLineIndex = -1;
            SelectOrderWrong = false;

            RefreshButtonNames();
            RefreshHighlighting();
        }

        public void OnPageActivated()
        {
            RefreshButtonNames();
            RefreshHighlighting();

            var set = GetSelectedTurnSetSafe();
            if (set != null)
                ApplyTurnSet(set);
        }

        // -------------------------
        // Sync view -> model
        // -------------------------
        private void SyncGcodeLinesFromEditor()
        {
            var lines = GetGcodeLines();
            lines.Clear();

            var rtb = GetGcodeEditor();
            if (rtb == null)
            {
                MessageBox.Show("Internal error: G-code editor not found in MainWindow.");
                return;
            }

            TextRange tr = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);

            string allText = tr.Text.Replace("\r\n", "\n");

            using (StringReader reader = new StringReader(allText))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line;

                    // Strip the visible editor prefix "1234:" if present (editor adds it)
                    int colonIndex = trimmed.IndexOf(':');
                    if (colonIndex >= 0 && colonIndex < 10)
                    {
                        trimmed = trimmed.Substring(colonIndex + 1).TrimStart();
                    }

                    lines.Add(trimmed);
                }
            }
        }

        // -------------------------
        // Selection from clicked line
        // -------------------------
        private void SelectLineFromCaret(ref int targetIndex)
        {
            SyncGcodeLinesFromEditor();

            var lines = GetGcodeLines();
            if (lines == null || lines.Count == 0)
            {
                MessageBox.Show("No G-code loaded in the editor.");
                return;
            }

            if (selectedLineIndex < 0 || selectedLineIndex >= lines.Count)
            {
                MessageBox.Show("Click a G-code line in the editor first.");
                return;
            }

            targetIndex = selectedLineIndex;

            RefreshButtonNames();
            ValidateSelectedRegion();
            RefreshHighlighting();

            // UNIVERSAL: store to selected set (markers + region lines + params)
            StoreSelectionIntoSelectedSet();
        }

        // ============================================================
        // Store current picked markers + region text into SelectedTurnSet
        // (called after marker clicks)
        // ============================================================
        private void StoreSelectionIntoSelectedSet()
        {
            if (_isApplyingTurnSet) return;

            var set = GetSelectedTurnSetSafe();
            if (set == null)
                return;

            var lines = GetGcodeLines();

            // region range from current picked indices
            var regionRaw = ExtractSelectedGcodeLines();
            if (regionRaw == null || regionRaw.Count == 0 || SelectOrderWrong)
            {
                ResolveAndUpdateStatus(set, lines);
                return;
            }

            // absolute region start index (same rules as ExtractSelectedGcodeLines)
            int absStart = -1;
            if (startXIndex >= 0 && startZIndex >= 0)
                absStart = (startXIndex < startZIndex) ? startXIndex : startZIndex;
            else if (startXIndex >= 0)
                absStart = startXIndex;
            else
                absStart = startZIndex;

            if (absStart < 0)
            {
                ResolveAndUpdateStatus(set, lines);
                return;
            }

            // convert absolute marker indices -> local indices in regionRaw
            int localSX = (startXIndex >= 0) ? (startXIndex - absStart) : -1;
            int localSZ = (startZIndex >= 0) ? (startZIndex - absStart) : -1;
            int localEX = (endXIndex >= 0) ? (endXIndex - absStart) : -1;
            int localEZ = (endZIndex >= 0) ? (endZIndex - absStart) : -1;

            // ALL WRITES go through the canonical editor (preserves UID if possible)
            CNC_Improvements_gcode_solids.SetManagement.Builders.BuildTurnRegion.EditExisting(
                rs: set,
                regionLines: regionRaw,
                startXIndex: localSX,
                startZIndex: localSZ,
                endXIndex: localEX,
                endZIndex: localEZ,
                toolUsage: _toolComp.Usage ?? "OFF",
                quadrant: _toolComp.Quadrant.ToString(CultureInfo.InvariantCulture),
                txtZExt: TxtZExt?.Text ?? "",
                nRad: NRad?.Text ?? "",
                snapshotDefaults: null
            );

            // Resolve now (anchored RegionLines match raw editor lines via searches)
            ResolveAndUpdateStatus(set, lines);
        }

        // -------------------------
        // Button handlers
        // -------------------------
        private void BtnStartX_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref startXIndex);
        private void BtnStartZ_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref startZIndex);
        private void BtnEndX_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref endXIndex);
        private void BtnEndZ_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref endZIndex);

        private void TxtZExt_TextChanged(object sender, TextChangedEventArgs e) => StoreTurnParamsIntoSelectedSet();
        private void NRad_TextChanged(object sender, TextChangedEventArgs e) => StoreTurnParamsIntoSelectedSet();

        // -------------------------
        // Scrolling / click handling
        // -------------------------
        private void GcodeLine_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Paragraph p && p.Tag is int lineIndex)
            {
                SyncGcodeLinesFromEditor();
                selectedLineIndex = lineIndex;
                RefreshHighlighting();
            }
        }

        // -------------------------
        // Region validation
        // -------------------------
        private void ValidateSelectedRegion()
        {
            var lines = GetGcodeLines();
            if (lines.Count == 0) return;

            SyncGcodeLinesFromEditor();

            if (startXIndex < 0 && startZIndex < 0) return;
            if (endXIndex < 0 && endZIndex < 0) return;

            int start = -1;
            if (startXIndex >= 0 && startZIndex >= 0)
                start = (startXIndex < startZIndex) ? startXIndex : startZIndex;
            else if (startXIndex >= 0)
                start = startXIndex;
            else
                start = startZIndex;

            int end = -1;
            if (endXIndex >= 0 && endZIndex >= 0)
                end = (endXIndex > endZIndex) ? endXIndex : endZIndex;
            else if (endXIndex >= 0)
                end = endXIndex;
            else
                end = endZIndex;

            if (start < 0 || end < 0 || start > end)
                return;

            for (int i = start; i <= end; i++)
            {
                string line = lines[i].ToUpperInvariant();
            }
        }

        // -------------------------
        // Button labels
        // -------------------------
        private void RefreshButtonNames()
        {
            BtnStartX.Content = FormatAxisButton(startXIndex, 'X', "Start X");
            BtnStartZ.Content = FormatAxisButton(startZIndex, 'Z', "Start Z");
            BtnEndX.Content = FormatAxisButton(endXIndex, 'X', "End X");
            BtnEndZ.Content = FormatAxisButton(endZIndex, 'Z', "End Z");
        }

        private string FormatAxisButton(int idx, char axis, string label)
        {
            var lines = GetGcodeLines();

            if (idx < 0 || idx >= lines.Count)
                return $"{label}: (none)";

            string line = lines[idx];

            if (TryGetCoord(line, axis, out double value))
            {
                return $"{label}: {value.ToString("0.###", CultureInfo.InvariantCulture)}";
            }

            string preview = line.Length > 20 ? line.Substring(0, 20) : line;
            return $"{label}: {preview}";
        }

        // -------------------------
        // Highlighting in shared editor
        // -------------------------
        // File: Utilities/TurnEditWindow.xaml.cs (or wherever this Turn RefreshHighlighting lives)
        // Method: RefreshHighlighting()
        // Change: REMOVE unique-tag coloring entirely. Display the full line as normal text.
        //         (Keeps your 4-segment colouring + error highlighting logic, but no special tag brush.)

        private void RefreshHighlighting()
        {
            // return if shift key is down
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
                return;

            var __swRefresh = System.Diagnostics.Stopwatch.StartNew();

            var lines = GetGcodeLines();
            var rtb = GetGcodeEditor();
            if (rtb == null || lines == null)
                return;

            rtb.Document.Blocks.Clear();

            int regionStart = -1;
            if (startXIndex >= 0 && startZIndex >= 0)
                regionStart = (startXIndex < startZIndex) ? startXIndex : startZIndex;
            else if (startXIndex >= 0)
                regionStart = startXIndex;
            else if (startZIndex >= 0)
                regionStart = startZIndex;

            int regionEnd = -1;
            if (endXIndex >= 0 && endZIndex >= 0)
                regionEnd = (endXIndex > endZIndex) ? endXIndex : endZIndex;
            else if (endXIndex >= 0)
                regionEnd = endXIndex;
            else if (endZIndex >= 0)
                regionEnd = endZIndex;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i] ?? string.Empty;

                Brush regionBg = Brushes.Transparent;
                bool insideRegion = (regionStart >= 0 && regionEnd >= 0 && i >= regionStart && i <= regionEnd);
                if (insideRegion)
                    regionBg = Brushes.LightYellow;

                Brush fg = (i == selectedLineIndex) ? Brushes.Blue : Brushes.Black;

                // ---- NO tag splitting for display anymore ----
                // We still need "code only" for validation, so we strip comments directly from the full line.
                string codeOnly = line;
                int paren = codeOnly.IndexOf('(');
                if (paren >= 0)
                    codeOnly = codeOnly.Substring(0, paren);

                string upperCode = (codeOnly ?? string.Empty).ToUpperInvariant();

                // --- SAFE 4-segment split (FULL line text) ---
                string s1 = string.Empty;
                string s2 = string.Empty;
                string s3 = string.Empty;
                string s4 = string.Empty;

                if (!string.IsNullOrEmpty(line))
                {
                    int total = line.Length;
                    int seg = total / 4;
                    if (seg == 0) seg = 1;

                    int idx = 0;

                    int len1 = Math.Min(seg, total - idx);
                    s1 = line.Substring(idx, len1);
                    idx += len1;

                    int len2 = Math.Min(seg, total - idx);
                    s2 = line.Substring(idx, len2);
                    idx += len2;

                    int len3 = Math.Min(seg, total - idx);
                    s3 = line.Substring(idx, len3);
                    idx += len3;

                    if (idx < total)
                        s4 = line.Substring(idx);
                }

                Brush b1 = (i == startXIndex) ? Brushes.LightBlue : Brushes.Transparent;
                Brush b2 = (i == startZIndex) ? Brushes.LightGreen : Brushes.Transparent;
                Brush b3 = (i == endXIndex) ? Brushes.LightSalmon : Brushes.Transparent;
                Brush b4 = (i == endZIndex) ? Brushes.Yellow : Brushes.Transparent;

                // ------------------------------------------------------------
                // ERROR HIGHLIGHTING (TURN):
                // Use upperCode (comments stripped) so "(T:G0000)" does NOT trip.
                // ------------------------------------------------------------
                if (insideRegion && (upperCode.Contains("G0 ") || upperCode.Contains("G00")))
                    regionBg = Brushes.OrangeRed;

                if (insideRegion && (upperCode.Contains("G2") || upperCode.Contains("G3")))
                {
                    bool hasI = upperCode.Contains("I");
                    bool hasK = upperCode.Contains("K");
                    bool hasR = upperCode.Contains("R");
                    if (!hasI && !hasK && !hasR)
                        regionBg = Brushes.LightCoral;
                }

                Paragraph p = new Paragraph { Margin = new Thickness(0) };
                p.Background = regionBg;

                p.Tag = i;
                p.MouseLeftButtonDown += GcodeLine_MouseLeftButtonDown;

                // Line number
                UiUtilities.AddNumberedLinePrefix(p, i + 1, fg);

                // Full text (4 segments) — NO special unique-tag colouring
                p.Inlines.Add(new Run(s1) { Background = b1, Foreground = fg });
                p.Inlines.Add(new Run(s2) { Background = b2, Foreground = fg });
                p.Inlines.Add(new Run(s3) { Background = b3, Foreground = fg });
                p.Inlines.Add(new Run(s4) { Background = b4, Foreground = fg });

                rtb.Document.Blocks.Add(p);
            }

            UiUtilities.RebuildAndStoreNumberedLineStartIndex(rtb);

            __swRefresh.Stop();
            System.Diagnostics.Debug.WriteLine(
               $"RefreshHighlighting(Turn) took {__swRefresh.Elapsed.TotalMilliseconds:0.###} ms " +
               $"(lines={lines.Count}, sel={selectedLineIndex}, sx={startXIndex}, sz={startZIndex}, ex={endXIndex}, ez={endZIndex})");
        }

        // Unique tag styling: (u:xxxx) — light blue @ ~50% opacity
        private static readonly Brush UniqueTagBrush = UniqueTagColor.UniqueTagBrush;

        private static bool TrySplitUniqueTag(string line, out string mainText, out string tagText)
        {

            mainText = line ?? string.Empty;
            tagText = string.Empty;

            if (string.IsNullOrEmpty(mainText))
                return false;

            // Support BOTH "(u:...)" and "(t:...)" suffix tags (any case)
            int idxU = mainText.LastIndexOf("(u:", StringComparison.OrdinalIgnoreCase);
            int idxT = mainText.LastIndexOf("(t:", StringComparison.OrdinalIgnoreCase);

            int idx = Math.Max(idxU, idxT);
            if (idx < 0)
                return false;

            // Must have a closing ')'
            int close = mainText.IndexOf(')', idx);
            if (close < 0)
                return false;

            // Keep ALL text from the tag start to the end
            tagText = mainText.Substring(idx);
            mainText = mainText.Substring(0, idx);
            return true;
        }

        // -------------------------
        // Coordinate helpers
        // -------------------------
        private (double startX, double startZ) GetStartCoordinates()
        {
            var lines = GetGcodeLines();

            if (startXIndex < 0 || startXIndex >= lines.Count)
                throw new Exception("Invalid Start-X index.");

            string lx = lines[startXIndex];

            if (!TryGetCoord(lx, 'X', out double startX))
                throw new Exception($"Start-X line {startXIndex + 1} does not contain a valid X value.");

            if (startZIndex < 0 || startZIndex >= lines.Count)
                throw new Exception("Invalid Start-Z index.");

            string lz = lines[startZIndex];

            if (!TryGetCoord(lz, 'Z', out double startZ))
                throw new Exception($"Start-Z line {startZIndex + 1} does not contain a valid Z value.");

            return (startX, startZ);
        }

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

        // -------------------------
        // Geometry build from region
        // -------------------------
        private List<GeoMove> BuildGeometryFromGcode(List<string> regionLines)
        {
            List<GeoMove> moves = new();

            var allLines = GetGcodeLines();

            (double startX, double startZ) = GetStartCoordinates();
            double lastX = startX;
            double lastZ = startZ;

            MotionMode mode = MotionMode.None;

            bool firstLineProcessed = false;

            foreach (string raw in regionLines)
            {
                string line = raw.ToUpperInvariant().Trim();

                if (line.Contains("G1") || line.Contains("G01") || line.Contains("G0") || line.Contains("G00")) mode = MotionMode.G1;
                if (line.Contains("G2") || line.Contains("G02")) mode = MotionMode.G2;
                if (line.Contains("G3") || line.Contains("G03")) mode = MotionMode.G3;

                if (!firstLineProcessed)
                {
                    firstLineProcessed = true;

                    int regionStart = Math.Max(startXIndex, startZIndex);
                    int physicalIndex = allLines.IndexOf(raw);

                    if (physicalIndex != regionStart)
                    {
                        continue;
                    }
                }

                bool hasX = TryGetCoord(line, 'X', out double newX);
                bool hasZ = TryGetCoord(line, 'Z', out double newZ);

                if (!hasX) newX = lastX;
                if (!hasZ) newZ = lastZ;

                if (mode == MotionMode.None)
                {
                    lastX = newX;
                    lastZ = newZ;
                    continue;
                }

                if (!hasX && !hasZ)
                {
                    lastX = newX;
                    lastZ = newZ;
                    continue;
                }

                if (mode == MotionMode.G1)
                {
                    if (lastX == newX && lastZ == newZ)
                    {
                        lastX = newX;
                        lastZ = newZ;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = "LINE",
                        Xs = lastX,
                        Zs = lastZ,
                        Xe = newX,
                        Ze = newZ
                    });
                }
                else if (mode == MotionMode.G2 || mode == MotionMode.G3)
                {
                    double I = 0, K = 0, R = 0;

                    bool hasI = TryGetCoord(line, 'I', out I);
                    bool hasK = TryGetCoord(line, 'K', out K);
                    bool hasR = TryGetCoord(line, 'R', out R);

                    if (!hasI && !hasK && !hasR)
                        throw new Exception("ERROR: Arc missing I/K or R.");

                    if (lastX == newX && lastZ == newZ)
                    {
                        lastX = newX;
                        lastZ = newZ;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = (mode == MotionMode.G2) ? "ARC_CW" : "ARC_CCW",
                        Xs = lastX,
                        Zs = lastZ,
                        Xe = newX,
                        Ze = newZ,
                        I = I,
                        K = K,
                        R = R
                    });
                }

                lastX = newX;
                lastZ = newZ;
            }

            return moves;
        }

        private void GetArc3Points(
            GeoMove m,
            out double xsR, out double zs,
            out double xmR, out double zm,
            out double xeR, out double ze,
            out double cxR, out double cz)
        {
            xsR = m.Xs / 2.0;
            xeR = m.Xe / 2.0;
            zs = m.Zs;
            ze = m.Ze;

            double cxRLocal = 0.0;
            double czLocal = 0.0;

            double aStart = 0.0;
            double dAlpha = 0.0;

            bool useR = Math.Abs(m.R) > 1e-9;

            if (!useR)
            {
                cxRLocal = xsR + m.I;
                czLocal = m.Zs + m.K;

                double sx = xsR - cxRLocal;
                double szLoc = m.Zs - czLocal;
                double ex = xeR - cxRLocal;
                double ezLoc = m.Ze - czLocal;

                double a1 = Math.Atan2(szLoc, sx);
                double a2 = Math.Atan2(ezLoc, ex);
                double da = a2 - a1;

                if (m.Type == "ARC_CW")
                {
                    while (da <= 0.0) da += 2.0 * Math.PI;
                }
                else
                {
                    while (da >= 0.0) da -= 2.0 * Math.PI;
                }

                aStart = a1;
                dAlpha = da;
            }
            else
            {
                double r = Math.Abs(m.R);

                double dx = xeR - xsR;
                double dz = m.Ze - m.Zs;
                double d = Math.Sqrt(dx * dx + dz * dz);

                if (d < 1e-9)
                    throw new Exception("R-arc with zero-length chord.");

                if (d > 2.0 * r + 1e-6)
                    throw new Exception("R too small for given arc endpoints.");

                double mx = (xsR + xeR) * 0.5;
                double mz = (m.Zs + m.Ze) * 0.5;

                double px = -dz / d;
                double pz = dx / d;

                double h = Math.Sqrt(Math.Max(r * r - (d * d * 0.25), 0.0));

                double cx1 = mx + h * px;
                double cz1 = mz + h * pz;
                double cx2 = mx - h * px;
                double cz2 = mz - h * pz;

                double s1x = xsR - cx1;
                double s1z = m.Zs - cz1;
                double e1x = xeR - cx1;
                double e1z = m.Ze - cz1;

                double a1_1 = Math.Atan2(s1z, s1x);
                double a2_1 = Math.Atan2(e1z, e1x);
                double da1 = a2_1 - a1_1;

                if (m.Type == "ARC_CW")
                {
                    while (da1 <= 0.0) da1 += 2.0 * Math.PI;
                }
                else
                {
                    while (da1 >= 0.0) da1 -= 2.0 * Math.PI;
                }

                double s2x = xsR - cx2;
                double s2z = m.Zs - cz2;
                double e2x = xeR - cx2;
                double e2z = m.Ze - cz2;

                double a1_2 = Math.Atan2(s2z, s2x);
                double a2_2 = Math.Atan2(e2z, e2x);
                double da2 = a2_2 - a1_2;

                if (m.Type == "ARC_CW")
                {
                    while (da2 <= 0.0) da2 += 2.0 * Math.PI;
                }
                else
                {
                    while (da2 >= 0.0) da2 -= 2.0 * Math.PI;
                }

                bool wantLong = (m.R < 0.0);

                bool ok1 = wantLong ? (Math.Abs(da1) > Math.PI) : (Math.Abs(da1) <= Math.PI);
                bool ok2 = wantLong ? (Math.Abs(da2) > Math.PI) : (Math.Abs(da2) <= Math.PI);

                if (ok1 && !ok2)
                {
                    cxRLocal = cx1;
                    czLocal = cz1;
                    aStart = a1_1;
                    dAlpha = da1;
                }
                else if (!ok1 && ok2)
                {
                    cxRLocal = cx2;
                    czLocal = cz2;
                    aStart = a1_2;
                    dAlpha = da2;
                }
                else
                {
                    if (Math.Abs(da1) <= Math.Abs(da2))
                    {
                        cxRLocal = cx1;
                        czLocal = cz1;
                        aStart = a1_1;
                        dAlpha = da1;
                    }
                    else
                    {
                        cxRLocal = cx2;
                        czLocal = cz2;
                        aStart = a1_2;
                        dAlpha = da2;
                    }
                }
            }

            double sxFinal = xsR - cxRLocal;
            double szFinal = zs - czLocal;

            double radiusFinal = Math.Sqrt(sxFinal * sxFinal + szFinal * szFinal);
            double aMid = aStart + 0.5 * dAlpha;

            xmR = cxRLocal + radiusFinal * Math.Cos(aMid);
            zm = czLocal + radiusFinal * Math.Sin(aMid);

            cxR = cxRLocal;
            cz = czLocal;
        }
        private List<string> BuildProfileTextFromMoves(List<GeoMove> moves)
        {
            if (moves == null || moves.Count == 0)
                throw new Exception("No feed moves found in selection.");

            List<string> profile = new List<string>();

            foreach (var m in moves)
            {
                if (m.Type == "LINE")
                {
                    double xsR = m.Xs / 2.0;
                    double xeR = m.Xe / 2.0;

                    profile.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "LINE {0} {1}   {2} {3}",
                        xsR, m.Zs,
                        xeR, m.Ze));
                }
                else if (m.Type == "ARC_CW" || m.Type == "ARC_CCW")
                {
                    GetArc3Points(
                        m,
                        out double xsR, out double zs,
                        out double xmR, out double zm,
                        out double xeR, out double ze,
                        out double cxR, out double cz);

                    double vSx = cxR - xsR;
                    double vSz = cz - zs;
                    double vEx = cxR - xeR;
                    double vEz = cz - ze;

                    string tag = (m.Type == "ARC_CW") ? "ARC3_CW" : "ARC3_CCW";

                    profile.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} {1} {2}   {3} {4}   {5} {6}   {7} {8}   {9} {10}   {11} {12}",
                        tag,
                        xsR, zs,
                        xmR, zm,
                        xeR, ze,
                        cxR, cz,
                        vSx, vSz,
                        vEx, vEz));
                }
                else
                {
                    throw new Exception($"Unknown move type '{m.Type}' in BuildProfileTextFromMoves.");
                }
            }

            return profile;
        }

        // -------------------------
        // Region extraction
        // -------------------------
        public List<string> ExtractSelectedGcodeLines()
        {
            var lines = GetGcodeLines();

            SelectOrderWrong = false;

            if (startXIndex < 0 && startZIndex < 0)
                return new List<string>();

            if (endXIndex < 0 && endZIndex < 0)
                return new List<string>();

            int start = -1;
            if (startXIndex >= 0 && startZIndex >= 0)
                start = (startXIndex < startZIndex) ? startXIndex : startZIndex;
            else if (startXIndex >= 0)
                start = startXIndex;
            else
                start = startZIndex;

            int end = -1;
            if (endXIndex >= 0 && endZIndex >= 0)
                end = (endXIndex > endZIndex) ? endXIndex : endZIndex;
            else if (endXIndex >= 0)
                end = endXIndex;
            else
                end = endZIndex;

            if (start < 0 || end < 0)
                return new List<string>();

            if (end < start)
            {
                SelectOrderWrong = true;

                MessageBox.Show(
                    "The end of the selected region is above the start.\n\n" +
                    "Please select the start position first, then the end position.",
                    "Invalid Selection Order",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return new List<string>();
            }

            if (end >= lines.Count)
                throw new Exception("End index exceeds G-code line count.");

            return lines.GetRange(start, end - start + 1);
        }
        // ...
        // NOTE: You did NOT paste beyond BtnTurnEditor_Click end in your final snippet,
        // so I am preserving exactly what you provided.
        // ...
        // Your existing BuildClosedShapeFromSelection / Export / ViewAll / etc
        // remains as-is EXCEPT for marker search call sites below.

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            FreeCadRunSuffix.ResetTurn();

            try
            {
                UiUtilities.CloseAllToolWindows();

                TryStoreSelectedTurnSetRegionFromEditor(out _);

                var main = GetMain();
                if (main == null)
                    throw new Exception("MainWindow not available.");

                string exportDir = main.CurrentProjectDirectory ?? "";
                if (string.IsNullOrWhiteSpace(exportDir) || !Directory.Exists(exportDir))
                    throw new Exception(
                        "Project directory is not set. Save or load a project first so an export folder exists.");

                var setForName = GetSelectedTurnSetSafe();

                var inv = CultureInfo.InvariantCulture;
                main.TryGetTransformForRegion(
                    setForName.Name,
                    out double rotYDeg,
                    out double rotZDeg,
                    out double tx,
                    out double ty,
                    out double tz,
                    out string matrixName);

                FreeCadScript.TransPY = $@"
#{matrixName}
TRANSFORM_ROTZ = {rotZDeg.ToString("0.###", inv)}
TRANSFORM_ROTY  = {rotYDeg.ToString("0.###", inv)}
TRANSFORM_TX = {tx.ToString("0.###", inv)}
TRANSFORM_TY = {ty.ToString("0.###", inv)}
TRANSFORM_TZ  = {tz.ToString("0.###", inv)}
";

                string safeBaseName;
                if (setForName != null && !string.IsNullOrWhiteSpace(setForName.Name))
                    safeBaseName = CNC_Improvements_gcode_solids.MainWindow.SanitizeFileStem(setForName.Name);
                else
                    safeBaseName = "TurnExport";

                bool ok = ExportTurnSelectionCore(
                    exportDir: exportDir,
                    exportBaseName: safeBaseName,
                    suppressSuccessMessage: main.IsExportAllRunning,
                    out string failReason);

                if (!ok)
                    MessageBox.Show(
                        failReason,
                        "Turning Export",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "Turning Export",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public bool ExportSetBatch(CNC_Improvements_gcode_solids.SetManagement.RegionSet set, string exportDir, out string failReason)
        {
            failReason = "";

            try
            {
                if (set == null)
                {
                    failReason = "Set was null.";
                    return false;
                }

                var main = GetMain();
                var inv = CultureInfo.InvariantCulture;
                main.TryGetTransformForRegion(
                    set.Name,
                    out double rotYDeg,
                    out double rotZDeg,
                    out double tx,
                    out double ty,
                    out double tz,
                    out string matrixName);

                FreeCadScript.TransPY = $@"
#{matrixName}
TRANSFORM_ROTZ = {rotZDeg.ToString("0.###", inv)}
TRANSFORM_ROTY  = {rotYDeg.ToString("0.###", inv)}
TRANSFORM_TX = {tx.ToString("0.###", inv)}
TRANSFORM_TY = {ty.ToString("0.###", inv)}
TRANSFORM_TZ  = {tz.ToString("0.###", inv)}
";

                if (string.IsNullOrWhiteSpace(exportDir) || !System.IO.Directory.Exists(exportDir))
                {
                    failReason = "Export directory is not valid (project directory not set).";
                    return false;
                }

                var miApply = this.GetType().GetMethod(
                    "ApplyTurnSet",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                if (miApply == null)
                {
                    failReason = "TurningPage is missing ApplyTurnSet(set). Cannot batch export turning sets.";
                    return false;
                }

                miApply.Invoke(this, new object?[] { set });

                string safeBaseName = CNC_Improvements_gcode_solids.MainWindow.SanitizeFileStem(set.Name);

                if (string.IsNullOrWhiteSpace(safeBaseName))
                    safeBaseName = "TurnExport";

                bool ok = ExportTurnSelectionCore(
                    exportDir: exportDir,
                    exportBaseName: safeBaseName,
                    suppressSuccessMessage: true,
                    out failReason);

                return ok;
            }
            catch (Exception ex)
            {
                if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                {
                    failReason = tie.InnerException.Message;
                    return false;
                }

                failReason = ex.Message;
                return false;
            }
        }

        // (ExportTurnSelectionCore / MakeSafeFileName / G41/G40/G42 / QuadButton_Click / Viewers etc are unchanged)

        // ============================================================
        // View-All builder: UPDATE marker searches to TextSearching
        // ============================================================
        private (List<string> exportClosedShape, string exportLabel) BuildExportClosedShapeForTurnSet(
            RegionSet set,
            List<string> allLines)
        {
            if (set == null)
                throw new ArgumentNullException(nameof(set));

            ResolveAndUpdateStatus(set, allLines);

            if (set.Status != RegionResolveStatus.Ok || !set.ResolvedStartLine.HasValue || !set.ResolvedEndLine.HasValue)
                throw new Exception("Set region is not resolved (Missing/Ambiguous/Unset).");

            int start = set.ResolvedStartLine.Value;
            int end = set.ResolvedEndLine.Value;

            var regionLines = new List<string>();
            for (int i = start; i <= end; i++)
                regionLines.Add(allLines[i]);

            string sxText = GetSnap(set, KEY_STARTX, "");
            string szText = GetSnap(set, KEY_STARTZ, "");
            string exText = GetSnap(set, KEY_ENDX, "");
            string ezText = GetSnap(set, KEY_ENDZ, "");

            int sx = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(allLines, sxText, start, end, preferLast: false);
            int sz = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(allLines, szText, start, end, preferLast: false);
            int ex = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(allLines, exText, start, end, preferLast: true);
            int ez = SetManagement.Builders.BuiltRegionSearches.FindSingleLine(allLines, ezText, start, end, preferLast: true);

            if (sx < 0) throw new Exception("StartX marker line not found in resolved region.");
            if (sz < 0) throw new Exception("StartZ marker line not found in resolved region.");
            if (ex < 0) throw new Exception("EndX marker line not found in resolved region.");
            if (ez < 0) throw new Exception("EndZ marker line not found in resolved region.");

            // NOTE: your existing BuildGeometryFromGcode_AbsoluteRange depends on methods you pasted later.
            // Leaving it as-is, only marker find was changed.

            var moves = BuildGeometryFromGcode_AbsoluteRange(regionLines, allLines, sx, sz);

            var profileOpen = BuildProfileTextFromMoves(moves);

            string zExtText = GetSnap(set, "TxtZExt", "-100");
            if (!double.TryParse(zExtText, NumberStyles.Float, CultureInfo.InvariantCulture, out double zUser))
                throw new Exception("Invalid ZExtText in set.");

            var closingOriginal = TurningProfileComposer.BuildClosingLinesForOpenProfile(profileOpen, zUser);

            List<string> exportProfileOpen = profileOpen;
            List<string> exportClosing3 = closingOriginal;
            string exportLabel = "ORIGINAL (G40/OFF)";

            string usage = GetSnap(set, KEY_TOOL_USAGE, "OFF").Trim();
            if (!string.Equals(usage, "OFF", StringComparison.OrdinalIgnoreCase))
            {
                var guideBuilder = new OffsetGuideBuilder(profileOpen, usage);
                var (_, cornerGuide) = guideBuilder.BuildGuide();

                string nradText = GetSnap(set, "NRad", "0").Trim();
                int quadrant = ParseIntOrDefault(GetSnap(set, KEY_QUADRANT, "3"), 3);

                var offsetter = new TurningOffsetter(profileOpen, cornerGuide, usage, nradText, quadrant);
                var (_, offsetProfileShape) = offsetter.BuildOffsetProfile();

                if (offsetProfileShape == null || offsetProfileShape.Count == 0)
                    throw new Exception("Offset profile generation produced no segments.");

                var offsetClosing3 = TurningProfileComposer.BuildClosingLinesForOpenProfile(offsetProfileShape, zUser);

                exportProfileOpen = offsetProfileShape;
                exportClosing3 = offsetClosing3;
                exportLabel = $"OFFSET ({usage})";
            }

            var exportClosed = TurningProfileComposer.ComposeClosedShape(exportProfileOpen, exportClosing3);
            return (exportClosed, exportLabel);
        }
        private void BtnTurnEditor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiUtilities.CloseAllToolWindows();

                var owner = Window.GetWindow(this);

                var win = new CNC_Improvements_gcode_solids.Utilities.TurnEditWindow();
                if (owner != null)
                    win.Owner = owner;

                win.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // -------------------------
        // (BuildClosedShapeFromSelection, ExportTurnSelectionCore, ViewProfile, ViewAll, etc.)
        // You pasted those earlier in this message, but not re-pasted after my rewrite point.
        // Keep them unchanged except where they called FindIndexByMarkerText... (now replaced above).
        // -------------------------

        // The following methods were in your paste after BuildExportClosedShapeForTurnSet:
        // BuildGeometryFromGcode_AbsoluteRange, GetStartCoordinatesFromMarkerIndices, etc.
        // KEEP THEM EXACTLY AS YOU HAVE THEM in your project file.
        // (No search/store logic inside them was changed.)

        private List<GeoMove> BuildGeometryFromGcode_AbsoluteRange(
            List<string> regionLines,
            List<string> allLines,
            int startXIdx,
            int startZIdx)
        {
            List<GeoMove> moves = new();

            if (regionLines == null || regionLines.Count == 0)
                return moves;

            (double startX, double startZ) = GetStartCoordinatesFromMarkerIndices(allLines, startXIdx, startZIdx);
            double lastX = startX;
            double lastZ = startZ;

            MotionMode mode = MotionMode.None;

            bool firstLineProcessed = false;
            int trueRegionStart = Math.Max(startXIdx, startZIdx);
            int absStart = Math.Min(startXIdx, startZIdx);

            for (int local = 0; local < regionLines.Count; local++)
            {
                int absIndex = absStart + local;

                string raw = regionLines[local];
                string line = (raw ?? "").ToUpperInvariant().Trim();

                if (line.Contains("G0 ") || line.Contains("G00"))
                    throw new Exception($"ERROR: G00 rapid move found inside region at line {absIndex + 1}.");

                if (line.Contains("G1") || line.Contains("G01")) mode = MotionMode.G1;
                if (line.Contains("G2") || line.Contains("G02")) mode = MotionMode.G2;
                if (line.Contains("G3") || line.Contains("G03")) mode = MotionMode.G3;

                if (!firstLineProcessed)
                {
                    firstLineProcessed = true;

                    if (absIndex != trueRegionStart)
                        continue;
                }

                bool hasX = TryGetCoord(line, 'X', out double newX);
                bool hasZ = TryGetCoord(line, 'Z', out double newZ);

                if (!hasX) newX = lastX;
                if (!hasZ) newZ = lastZ;

                if (mode == MotionMode.None)
                {
                    lastX = newX;
                    lastZ = newZ;
                    continue;
                }

                if (!hasX && !hasZ)
                {
                    lastX = newX;
                    lastZ = newZ;
                    continue;
                }

                if (mode == MotionMode.G1)
                {
                    if (lastX == newX && lastZ == newZ)
                    {
                        lastX = newX;
                        lastZ = newZ;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = "LINE",
                        Xs = lastX,
                        Zs = lastZ,
                        Xe = newX,
                        Ze = newZ
                    });
                }
                else if (mode == MotionMode.G2 || mode == MotionMode.G3)
                {
                    double I = 0, K = 0, R = 0;

                    bool hasI = TryGetCoord(line, 'I', out I);
                    bool hasK = TryGetCoord(line, 'K', out K);
                    bool hasR = TryGetCoord(line, 'R', out R);

                    if (!hasI && !hasK && !hasR)
                        throw new Exception($"ERROR: Arc missing I/K or R at line {absIndex + 1}.");

                    if (lastX == newX && lastZ == newZ)
                    {
                        lastX = newX;
                        lastZ = newZ;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = (mode == MotionMode.G2) ? "ARC_CW" : "ARC_CCW",
                        Xs = lastX,
                        Zs = lastZ,
                        Xe = newX,
                        Ze = newZ,
                        I = I,
                        K = K,
                        R = R
                    });
                }

                lastX = newX;
                lastZ = newZ;
            }

            return moves;
        }

        private (double startX, double startZ) GetStartCoordinatesFromMarkerIndices(
            List<string> allLines,
            int startXIdx,
            int startZIdx)
        {
            if (allLines == null || allLines.Count == 0)
                throw new Exception("No G-code lines loaded.");

            if (startXIdx < 0 || startXIdx >= allLines.Count)
                throw new Exception("Invalid Start-X marker index.");

            if (startZIdx < 0 || startZIdx >= allLines.Count)
                throw new Exception("Invalid Start-Z marker index.");

            string lx = allLines[startXIdx];
            string lz = allLines[startZIdx];

            if (!TryGetCoord(lx, 'X', out double startX))
                throw new Exception($"Start-X marker line {startXIdx + 1} does not contain a valid X value.");

            if (!TryGetCoord(lz, 'Z', out double startZ))
                throw new Exception($"Start-Z marker line {startZIdx + 1} does not contain a valid Z value.");

            return (startX, startZ);
        }

        // (Older ad-hoc matching helpers were removed to keep Turn aligned with Mill/Drill.)

        // ============================================================
        // Core exporter used by BOTH single and batch.
        // - No LogWindow ever
        // - FreeCAD failure -> returns false (TXT may still exist)
        // ============================================================
        private bool ExportTurnSelectionCore(
    string exportDir,
    string exportBaseName,
    bool suppressSuccessMessage,
    out string failReason)
        {
            failReason = "";

            try
            {
                // Build geometry from CURRENT selection (same behavior as before)
                var (closedShapeOriginal, region, moves, profileShape, closingShapeOriginal) = BuildClosedShapeFromSelection();

                if (!double.TryParse(TxtZExt.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zUser))
                    return Fail("Invalid Z-extension value for closing lines.", out failReason);

                List<string> exportProfileOpen = profileShape;
                List<string> exportClosing3 = closingShapeOriginal;
                string exportLabel = "ORIGINAL (G40/OFF)";

                // Apply tool compensation if enabled (same logic as before)
                if (!string.Equals(_toolComp.Usage, "OFF", StringComparison.OrdinalIgnoreCase))
                {
                    var guideBuilder = new OffsetGuideBuilder(profileShape, _toolComp.Usage);
                    var (_, cornerGuide) = guideBuilder.BuildGuide();

                    var offsetter = new TurningOffsetter(profileShape, cornerGuide, _toolComp.Usage, NRad.Text, _toolComp.Quadrant);
                    var (offsetOps, offsetProfileShape) = offsetter.BuildOffsetProfile();

                    if (offsetProfileShape == null || offsetProfileShape.Count == 0)
                        return Fail("Offset profile generation produced no segments; cannot export.", out failReason);

                    var offsetClosing3 = TurningProfileComposer.BuildClosingLinesForOpenProfile(offsetProfileShape, zUser);

                    exportProfileOpen = offsetProfileShape;
                    exportClosing3 = offsetClosing3;
                    exportLabel = $"OFFSET ({_toolComp.Usage})";
                }

                var exportClosedShape = TurningProfileComposer.ComposeClosedShape(exportProfileOpen, exportClosing3);

                // ------------------------------------------------------------
                // Paths: project directory + selected set name (already agreed)
                // ------------------------------------------------------------
                if (string.IsNullOrWhiteSpace(exportBaseName))
                    exportBaseName = "TurnExport";

                exportBaseName = MakeSafeFileName(exportBaseName);
                if (string.IsNullOrWhiteSpace(exportBaseName))
                    exportBaseName = "TurnExport";

                string txtPath = Path.Combine(exportDir, exportBaseName + ".txt");
                string stepPath = Path.Combine(exportDir, exportBaseName + "_Turn_stp.stp");

                if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
                { File.WriteAllLines(txtPath, exportClosedShape); }
                // Overwrite allowed always

                // Set FreeCAD paths
                FreeCadScript.ProfilePth = txtPath;
                FreeCadScript.StepPth = stepPath;

                String Cr = @" 
                    ";
                FreeCadScript.Profile = Cr;
                for (int i2 = 0; i2 < exportClosedShape.Count; i2++)
                {
                    FreeCadScript.Profile = FreeCadScript.Profile + exportClosedShape[i2];
                    FreeCadScript.Profile = FreeCadScript.Profile + Cr;
                }

                // Run FreeCAD (batch-safe: failure returns false, no MessageBox here)
                try
                {
                    _ = FreeCadRunner.RunFreeCad();
                }
                catch (Exception fcEx)
                {
                    return Fail($"FreeCAD STEP export failed: {fcEx.Message}", out failReason);
                }

                // ------------------------------------------------------------


                // EXPORT-ALL REGISTRATION (authoritative list)
                // Only register AFTER successful FreeCAD export.
                // De-dupe defensively (case-insensitive).
                // ------------------------------------------------------------
                var main = GetMain();
                if (main != null && main.ExportAllCreatedStepFiles != null)
                {
                    bool already = false;
                    for (int i = 0; i < main.ExportAllCreatedStepFiles.Count; i++)
                    {
                        var p = main.ExportAllCreatedStepFiles[i];
                        if (string.Equals(p, stepPath, StringComparison.OrdinalIgnoreCase))
                        {
                            already = true;
                            break;
                        }
                    }

                    if (!already)
                        main.ExportAllCreatedStepFiles.Add(stepPath);
                }

                // Success message ONLY for single export (ExportAll suppresses it)
                bool isExportAllRunning = (main != null && main.IsExportAllRunning);

                if (!suppressSuccessMessage && !isExportAllRunning)
                {
                    MessageBox.Show(
                        $"Export complete.",
                        "Turning Export",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return true;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message, out failReason);
            }

            static bool Fail(string msg, out string reason)
            {
                reason = msg;
                return false;
            }
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";

            string s = name.Trim();

            // Replace invalid filename characters with underscore
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            // Avoid names that are just dots/spaces
            s = s.Trim().Trim('.');

            // Keep it reasonable
            if (s.Length > 120)
                s = s.Substring(0, 120).Trim();

            return s;
        }

        private void G41_click(object sender, RoutedEventArgs e)
        {
            _toolComp.Usage = "LEFT";
            UpdateToolUsageButtonVisuals(_toolComp.Usage);
            StoreTurnParamsIntoSelectedSet();
        }

        private void G40_click(object sender, RoutedEventArgs e)
        {
            _toolComp.Usage = "OFF";
            UpdateToolUsageButtonVisuals(_toolComp.Usage);
            StoreTurnParamsIntoSelectedSet();
        }

        private void G42_click(object sender, RoutedEventArgs e)
        {
            _toolComp.Usage = "RIGHT";
            UpdateToolUsageButtonVisuals(_toolComp.Usage);
            StoreTurnParamsIntoSelectedSet();
        }

        private void QuadButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn)
                return;

            int quadrant = btn.Name switch
            {
                "Quad1" => 1,
                "Quad2" => 2,
                "Quad3" => 3,
                "Quad4" => 4,
                "Quad5" => 5,
                "Quad6" => 6,
                "Quad7" => 7,
                "Quad8" => 8,
                "Quad9" => 9,
                _ => 0
            };

            if (quadrant == 0)
                return;

            _toolComp.Quadrant = quadrant;
            UpdateQuadrantButtonVisuals(_toolComp.Quadrant);
            StoreTurnParamsIntoSelectedSet();
        }

        // ============================================================
        // NEW: Always re-sync selected TURN set before any viewer run.
        // Fixes: inserting/deleting lines above a saved turn region.
        // ============================================================
        private void ResyncSelectedTurnSetBeforeViewer()
        {
            // You already have this in Turning if it matches Mill/Drill patterns:
            SyncGcodeLinesFromEditor();

            var set = GetSelectedTurnSetSafe(); // or Main.SelectedTurnSet
            if (set == null)
                return;

            // Use whichever you already have:
            // ApplyTurnSetInternal(set, suppressEditorRender: true);
            ApplyTurnSet(set);
        }

        private (
    List<string> closedShape,
    List<string> region,
    List<GeoMove> moves,
    List<string> profileShape,
    List<string> closingShape
) BuildClosedShapeFromSelection()
        {
            SyncGcodeLinesFromEditor();

            var region = ExtractSelectedGcodeLines();
            if (region.Count == 0)
                throw new Exception("No valid G-code region selected.");

            var moves = BuildGeometryFromGcode(region);

            var profileShape = BuildProfileTextFromMoves(moves);

            // ------------------------------------------------------------
            // If the profile is already closed, DO NOT add closing lines.
            // closedShape = profileShape, closingShape = empty.
            // ------------------------------------------------------------
            static bool TryGetStartPoint(List<string> prof, out double xs, out double zs)
            {
                xs = zs = 0;
                if (prof == null || prof.Count == 0) return false;

                string s = (prof[0] ?? "").Trim();
                if (s.Length == 0) return false;

                string[] t = Regex.Split(s, @"\s+");
                if (t.Length < 5) return false;

                if (string.Equals(t[0], "LINE", StringComparison.OrdinalIgnoreCase))
                {
                    return double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out xs)
                        && double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out zs);
                }

                if (t[0].StartsWith("ARC3_", StringComparison.OrdinalIgnoreCase))
                {
                    // ARC3_* xs zs xm zm xe ze ...
                    if (t.Length < 7) return false;
                    return double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out xs)
                        && double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out zs);
                }

                return false;
            }

            static bool TryGetEndPoint(List<string> prof, out double xe, out double ze)
            {
                xe = ze = 0;
                if (prof == null || prof.Count == 0) return false;

                string s = (prof[prof.Count - 1] ?? "").Trim();
                if (s.Length == 0) return false;

                string[] t = Regex.Split(s, @"\s+");
                if (t.Length < 5) return false;

                if (string.Equals(t[0], "LINE", StringComparison.OrdinalIgnoreCase))
                {
                    return double.TryParse(t[3], NumberStyles.Float, CultureInfo.InvariantCulture, out xe)
                        && double.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out ze);
                }

                if (t[0].StartsWith("ARC3_", StringComparison.OrdinalIgnoreCase))
                {
                    // ARC3_* xs zs xm zm xe ze ...
                    if (t.Length < 7) return false;
                    return double.TryParse(t[5], NumberStyles.Float, CultureInfo.InvariantCulture, out xe)
                        && double.TryParse(t[6], NumberStyles.Float, CultureInfo.InvariantCulture, out ze);
                }

                return false;
            }

            static bool IsClosed(List<string> prof)
            {
                const double tol = 1e-5;
                if (!TryGetStartPoint(prof, out double xs, out double zs)) return false;
                if (!TryGetEndPoint(prof, out double xe, out double ze)) return false;

                double dx = xe - xs;
                double dz = ze - zs;
                return (dx * dx + dz * dz) <= (tol * tol);
            }

            if (IsClosed(profileShape))
            {
                // Already closed: ignore closing curves completely.
                return (new List<string>(profileShape), region, moves, profileShape, new List<string>());
            }

            // ------------------------------------------------------------
            // Existing behavior (open profile -> add closing lines)
            // ------------------------------------------------------------
            if (!double.TryParse(TxtZExt.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zUser))
                throw new Exception("Invalid Z-extension value for closing lines.");

            var closingShape = TurningProfileComposer.BuildClosingLinesForOpenProfile(profileShape, zUser);

            var closedShape = TurningProfileComposer.ComposeClosedShape(profileShape, closingShape);

            return (closedShape, region, moves, profileShape, closingShape);
        }

        private void BtnViewProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiUtilities.CloseAllToolWindows();
                // Better store: update RegionLines from current editor, only if resolved OK
                TryStoreSelectedTurnSetRegionFromEditor(out _);

                ResyncSelectedTurnSetBeforeViewer();
                var (closedShape, region, moves, profileShape, closingShape) = BuildClosedShapeFromSelection();

                if (closedShape.Count < 4)
                    throw new Exception("Closed shape did not contain enough segments.");

                if (!double.TryParse(TxtZExt.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zUser))
                    throw new Exception("Invalid Z-extension value for closing lines.");

                var guideBuilder = new OffsetGuideBuilder(profileShape, _toolComp.Usage);
                var (segmentPairs, cornerGuide) = guideBuilder.BuildGuide();

                List<string> offsetProfileShape = new List<string>();
                List<string> offsetClosingShape = new List<string>();
                List<string> offsetOps = new List<string>();

                if (!string.Equals(_toolComp.Usage, "OFF", StringComparison.OrdinalIgnoreCase))
                {
                    var offsetter = new TurningOffsetter(profileShape, cornerGuide, _toolComp.Usage, NRad.Text, _toolComp.Quadrant);
                    (offsetOps, offsetProfileShape) = offsetter.BuildOffsetProfile();

                    if (offsetProfileShape.Count > 0)
                        offsetClosingShape = TurningProfileComposer.BuildClosingLinesForOpenProfile(offsetProfileShape, zUser);
                }
                else
                {
                    offsetOps.Add("Tool usage OFF: offset not generated.");
                }

                var sbScript = new System.Text.StringBuilder();

                string profWidth = Settings.Default.ProfileWidth.ToString("0.###", CultureInfo.InvariantCulture);
                string closeWidth = Settings.Default.ClosingWidth.ToString("0.###", CultureInfo.InvariantCulture);

                sbScript.AppendLine($"({Settings.Default.ProfileColor},{profWidth})");
                foreach (var line in profileShape)
                    sbScript.AppendLine(line);

                sbScript.AppendLine($"({Settings.Default.ClosingColor},{closeWidth})");
                foreach (var line in closingShape)
                    sbScript.AppendLine(line);

                if (offsetProfileShape.Count > 0)
                {
                    sbScript.AppendLine($"({Settings.Default.OffsetColor},{profWidth})");
                    foreach (var line in offsetProfileShape)
                        sbScript.AppendLine(line);

                    sbScript.AppendLine($"({Settings.Default.ClosingColor},{closeWidth})");
                    foreach (var line in offsetClosingShape)
                        sbScript.AppendLine(line);
                }

                string scriptText = sbScript.ToString();

                // -------------------------------
                // LOG WINDOW (toggle)
                // -------------------------------
                if (Settings.Default.LogWindowShow)
                {
                    var sbLog = new System.Text.StringBuilder();

                    sbLog.AppendLine("=== PROFILE DISPLAY SCRIPT ===");
                    sbLog.AppendLine(scriptText);
                    sbLog.AppendLine();

                    sbLog.AppendLine("=== Segment Pairs ===");
                    foreach (var c in segmentPairs)
                        sbLog.AppendLine(c);
                    sbLog.AppendLine();

                    sbLog.AppendLine("=== Corner Guide ===");
                    foreach (var c in cornerGuide)
                        sbLog.AppendLine(c);
                    sbLog.AppendLine();

                    sbLog.AppendLine("=== OFFSET OPS ===");
                    foreach (var op in offsetOps)
                        sbLog.AppendLine(op);

                    string logText = sbLog.ToString();

                    var ownerW = Window.GetWindow(this);
                    var logWindow = new CNC_Improvements_gcode_solids.Utilities.LogWindow("Profile Script + Corner Guide + Offset", logText);
                    if (ownerW != null)
                        logWindow.Owner = ownerW;
                    logWindow.Show();
                }

                // -------------------------------
                // VIEWER
                // -------------------------------
                var owner = Window.GetWindow(this);

                var viewer = new ProfileViewWindow();
                if (owner != null)
                    viewer.Owner = owner;

                string offsetType = _toolComp.Usage;

                double noseRad = double.NaN;
                if (!string.Equals(_toolComp.Usage, "OFF", StringComparison.OrdinalIgnoreCase))
                {
                    if (!double.TryParse(NRad.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out noseRad))
                    {
                        if (!double.TryParse(NRad.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out noseRad))
                            noseRad = double.NaN;
                    }
                }

                int quadrant = string.Equals(_toolComp.Usage, "OFF", StringComparison.OrdinalIgnoreCase)
                    ? -1
                    : _toolComp.Quadrant;

                viewer.SetDiagnostics(offsetType, noseRad, quadrant);

                // IMPORTANT: single-view stays “raw” (no transform metadata)
                viewer.LoadProfileScript(scriptText);
                viewer.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Profile Script Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===============================
        // VIEW ALL TURN SETS (silent build)
        // ===============================
        private void BtnViewAllProfiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiUtilities.CloseAllToolWindows();

                SyncGcodeLinesFromEditor();

                var main = GetMain();
                var allLines = GetGcodeLines();

                if (main.TurnSets == null || main.TurnSets.Count == 0)
                    throw new Exception("No TURN sets exist. Add at least one TURN region first.");

                // Build script + collect invalid region warnings
                string scriptText = BuildAllTurnSetsDisplayScript(main.TurnSets, allLines, out List<string> invalidRegions);

                // If any invalid regions: tell user clearly (but still show viewer if at least one region drew)
                if (invalidRegions != null && invalidRegions.Count > 0)
                {
                    string msg = string.Join("\n", invalidRegions);
                    MessageBox.Show(
                        msg,
                        "View All: Invalid Regions",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                // If script is empty/whitespace, nothing valid was built (BuildAll returns errors-only text if you keep that behavior)
                if (string.IsNullOrWhiteSpace(scriptText))
                    throw new Exception("No sets could be built.");

                // LOG WINDOW (toggle) – show script text (includes @TRANSFORM lines)
                if (Settings.Default.LogWindowShow)
                {
                    var ownerW = Window.GetWindow(this);
                    var logWindow = new CNC_Improvements_gcode_solids.Utilities.LogWindow("VIEW ALL: Turning Profile Script", scriptText);
                    if (ownerW != null)
                        logWindow.Owner = ownerW;
                    logWindow.Show();
                }

                var owner = Window.GetWindow(this);

                var viewer = new ProfileViewWindow();
                if (owner != null)
                    viewer.Owner = owner;

                viewer.SetDiagnostics("ALL", double.NaN, -1);
                viewer.LoadProfileScript(scriptText);
                viewer.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "VIEW ALL Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BuildAllTurnSetsDisplayScript(
    ObservableCollection<RegionSet> turnSets,
    List<string> allLines,
    out List<string> invalidRegions)
        {
            invalidRegions = new List<string>();

            string profWidth = Settings.Default.ProfileWidth.ToString("0.###", CultureInfo.InvariantCulture);
            string closeWidth = Settings.Default.ClosingWidth.ToString("0.###", CultureInfo.InvariantCulture);

            string styleProfile = $"({Settings.Default.ProfileColor},{profWidth})";
            string styleOffset = $"({Settings.Default.OffsetColor},{profWidth})";
            string styleClosing = $"({Settings.Default.ClosingColor},{closeWidth})";

            var sb = new System.Text.StringBuilder();

            var errors = new List<string>();
            int drawn = 0;

            var main = GetMain();
            var inv = CultureInfo.InvariantCulture;

            for (int i = 0; i < turnSets.Count; i++)
            {
                var set = turnSets[i];
                if (set == null)
                    continue;

                // Only include sets enabled for "View All"
                if (!set.ShowInViewAll)
                    continue;

                try
                {
                    // Rebuild from stored markers (so View All tracks current editor)
                    if (!TryRebuildTurnSetRegionFromStoredMarkers(set, allLines, out string rebuildReason))
                        throw new Exception(rebuildReason);

                    var (exportClosedShape, exportLabel) = BuildExportClosedShapeForTurnSet(set, allLines);

                    if (exportClosedShape == null || exportClosedShape.Count == 0)
                        throw new Exception("Produced no geometry lines.");

                    if (exportClosedShape.Count < 4)
                        throw new Exception("Closed shape too short to split (expected >= 4 lines).");

                    var closing3 = new List<string>
            {
                exportClosedShape[0],
                exportClosedShape[exportClosedShape.Count - 2],
                exportClosedShape[exportClosedShape.Count - 1]
            };

                    var profileOpen = exportClosedShape.GetRange(1, exportClosedShape.Count - 3);

                    string usage = GetSnap(set, KEY_TOOL_USAGE, "OFF");
                    bool isOffset = !string.Equals(usage, "OFF", StringComparison.OrdinalIgnoreCase);

                    // Per-set transform metadata
                    double rotY = 0.0;
                    double rotZ = 0.0;
                    double tx = 0.0;
                    double ty = 0.0;
                    double tz = 0.0;
                    string matrixName = "No Transformation";

                    if (main != null)
                        main.TryGetTransformForRegion(set.Name ?? "", out rotY, out rotZ, out tx, out ty, out tz, out matrixName);

                    string rotYStr = rotY.ToString("0.###", inv);
                    string tzStr = tz.ToString("0.###", inv);
                    string safeMatrixName = (matrixName ?? "").Replace("\"", "'");

                    sb.AppendLine($"; ===== TURN SET: {set.Name} =====");
                    sb.AppendLine($"; Mode: {exportLabel}");
                    sb.AppendLine($"; ToolUsage: {usage}");
                    sb.AppendLine($"@TRANSFORM MATRIX \"{safeMatrixName}\" ROTY {rotYStr} TZ {tzStr}");

                    sb.AppendLine(isOffset ? styleOffset : styleProfile);
                    foreach (var line in profileOpen)
                        sb.AppendLine(line);

                    sb.AppendLine(styleClosing);
                    foreach (var line in closing3)
                        sb.AppendLine(line);

                    sb.AppendLine();
                    drawn++;
                }
                catch (Exception oneEx)
                {
                    string nm = set?.Name ?? "(null)";
                    // USER-FACING message (exact phrasing you asked for)
                    invalidRegions.Add($"Region \"{nm}\" is invalid...remove from view all or correct.");

                    // Keep detailed reason for log/script
                    errors.Add($"{nm}: {oneEx.Message}");
                }
            }

            // If nothing could be drawn, return a readable error-only script
            if (drawn == 0)
            {
                sb.AppendLine("; ===== ERRORS (no sets built) =====");
                if (errors.Count == 0)
                    sb.AppendLine("; Unknown reason.");
                else
                {
                    foreach (var e in errors)
                        sb.AppendLine($"; {e}");
                }

                return sb.ToString();
            }

            // If some sets drew but some failed, append errors at bottom
            if (errors.Count > 0)
            {
                sb.AppendLine("; ===== ERRORS (some sets skipped) =====");
                foreach (var e in errors)
                    sb.AppendLine($"; {e}");
            }

            return sb.ToString();
        }

    }
}
