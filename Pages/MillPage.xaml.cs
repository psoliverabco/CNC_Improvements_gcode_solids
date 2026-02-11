using CNC_Improvements_gcode_solids.FreeCadIntegration;
using CNC_Improvements_gcode_solids.Properties;
using CNC_Improvements_gcode_solids.SetManagement;
using CNC_Improvements_gcode_solids.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CNC_Improvements_gcode_solids.SetManagement.Builders;


namespace CNC_Improvements_gcode_solids.Pages
{
    public partial class MillPage : Page, IGcodePage
    {

        public static readonly List<Brush> ColorList = new()
    {
        new SolidColorBrush(Color.FromRgb(0xE6, 0x19, 0x4B)), // red
        new SolidColorBrush(Color.FromRgb(0x3C, 0xB4, 0x4B)), // green
        new SolidColorBrush(Color.FromRgb(0x43, 0x63, 0xD8)), // blue
        new SolidColorBrush(Color.FromRgb(0xFF, 0xE1, 0x19)), // yellow
        new SolidColorBrush(Color.FromRgb(0xF5, 0x82, 0x31)), // orange
        new SolidColorBrush(Color.FromRgb(0x91, 0x1E, 0xB4)), // purple
        new SolidColorBrush(Color.FromRgb(0x46, 0xF0, 0xF0)), // cyan
        new SolidColorBrush(Color.FromRgb(0xF0, 0x32, 0xE6)), // magenta
        new SolidColorBrush(Color.FromRgb(0xBC, 0xF6, 0x0C)), // lime
        new SolidColorBrush(Color.FromRgb(0xFA, 0xBE, 0xBE)), // pink
        new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x80)), // teal
        new SolidColorBrush(Color.FromRgb(0xE6, 0xBE, 0xFF)), // lavender
        new SolidColorBrush(Color.FromRgb(0x9A, 0x63, 0x24)), // brown
        new SolidColorBrush(Color.FromRgb(0x80, 0x00, 0x00)), // maroon
        new SolidColorBrush(Color.FromRgb(0xAA, 0xFF, 0xC3)), // mint
        new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x00)), // olive
        new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0xB1)), // apricot
        new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x75)), // navy
        new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), // grey
    };


        private int startXIndex = -1;
        private int startYIndex = -1;
        private int endXIndex = -1;
        private int endYIndex = -1;
        private int selectedLineIndex = -1;
        private bool SelectOrderWrong = false;
        private int planeZIndex = -1;   // Z-plane line index

        // guards so ApplyMillSet doesn't cause recursive updates via TextChanged etc
        private bool _isApplyingMillSet = false;

        // Snapshot keys (stored in RegionSet.PageSnapshot.Values)
        private const string KEY_PLANEZ_TEXT = "PlaneZLineText";
        private const string KEY_STARTX_TEXT = "StartXLineText";
        private const string KEY_STARTY_TEXT = "StartYLineText";
        private const string KEY_ENDX_TEXT = "EndXLineText";
        private const string KEY_ENDY_TEXT = "EndYLineText";
        private const string KEY_TOOL_DIA = "TxtToolDia";
        private const string KEY_TOOL_LEN = "TxtToolLen";
        private const string KEY_FUSEALL = "Fuseall";
        private const string KEY_REMOVE_SPLITTER = "RemoveSplitter";
        private const string KEY_CLIPPER = "Clipper";
        private const string KEY_CLIPPER_ISLAND = "ClipperIsland";
        // Wire interpretation (new radios)
        private const string KEY_WIRE_GUIDED_TOOL = "GuidedTool";
        private const string KEY_WIRE_CLOSED_WIRE = "ClosedWire";
        private const string KEY_WIRE_CLOSED_INNER = "ClosedInner";
        private const string KEY_WIRE_CLOSED_OUTER = "ClosedOuter";








        // Modal state for motion mode
        private enum MotionMode { None, G1, G2, G3 }

        // Parsed geometry entities (XY plane, G17)
        private class GeoMove
        {
            public string Type = "";    // "LINE", "ARC_CW", "ARC_CCW"
            public double Xs, Ys;
            public double Xe, Ye;
            public double I, J;         // center offsets (if arc, G17)
            public double R;            // radius (if arc R-mode)
        }

        public MillPage()
        {
            InitializeComponent();
        }

        // -------------------------
        // Access to MainWindow model
        // -------------------------
        private MainWindow GetMain()
        {
            return Application.Current.MainWindow as MainWindow
                   ?? throw new InvalidOperationException("MainWindow not available.");
        }

        private List<string> GetGcodeLines()
        {
            return GetMain().GcodeLines;
        }

        private RichTextBox GetGcodeEditor()
        {
            return GetMain().GcodeEditor;
        }

        private RegionSet? GetSelectedMillSetSafe()
        {
            return GetMain().SelectedMillSet;
        }

        public void ApplyMillSet(RegionSet? set)
        {
            ApplyMillSetInternal(set, suppressEditorRender: false);
        }


        private int FindIndexByMarkerText(string markerText)
        {
            var lines = GetGcodeLines();

            // Uses shared normalizer + shared search that understands "#uid,n#..."
            return BuiltRegionSearches.FindSingleLine(
                allLines: lines,
                keyText: markerText ?? "",
                rangeStart: -1,
                rangeEnd: -1,
                preferLast: false);
        }

















        private void ApplyMillSetInternal(RegionSet? set, bool suppressEditorRender)
        {
            // Keep behavior safe: null set = do nothing destructive
            if (set == null)
            {
                if (!suppressEditorRender)
                {
                    RefreshButtonNames();
                    RefreshHighlighting();
                }
                return;
            }

            // Always match against latest edits before searching
            SyncGcodeLinesFromEditor();

            _isApplyingMillSet = true;
            try
            {
                // Ensure snapshot exists
                set.PageSnapshot ??= new UiStateSnapshot();

                // Restore tool params
                TxtToolDia.Text = GetSnapshotOrDefault(set, KEY_TOOL_DIA, "");
                TxtToolLen.Text = GetSnapshotOrDefault(set, KEY_TOOL_LEN, "");

                // Restore radios (DECOUPLED: treat each as independent)
                string snapFuse = GetSnapshotOrDefault(set, KEY_FUSEALL, "0");
                string snapClipper = GetSnapshotOrDefault(set, KEY_CLIPPER, "0");
                string snapRemove = GetSnapshotOrDefault(set, KEY_REMOVE_SPLITTER, "0");
                string snapClipperIsland = GetSnapshotOrDefault(set, KEY_CLIPPER_ISLAND, "0");

                if (Fuseall != null)
                    Fuseall.IsChecked = (snapFuse == "1");

                if (Clipper != null)
                    Clipper.IsChecked = (snapClipper == "1");

                if (RemoveSplitter != null)
                    RemoveSplitter.IsChecked = (snapRemove == "1");

                if (ClipperIsland != null)
                    ClipperIsland.IsChecked = (snapClipperIsland == "1");

                // Restore wire interpretation radios (GuidedTool / ClosedWire)
                string snapGuidedTool = GetSnapshotOrDefault(set, KEY_WIRE_GUIDED_TOOL, "1");
                string snapClosedWire = GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_WIRE, "0");
                string snapClosedInner = GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_INNER, "0");
                string snapClosedOuter = GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_OUTER, "1");

                // Enforce sane defaults if older projects have neither set
                bool wantGuided = (snapGuidedTool == "1");
                bool wantClosed = (snapClosedWire == "1");
                if (!wantGuided && !wantClosed)
                    wantGuided = true;

                if (GuidedTool != null) GuidedTool.IsChecked = wantGuided;
                if (ClosedWire != null) ClosedWire.IsChecked = wantClosed;
                if (ClosedInner != null) ClosedInner.IsChecked = (snapClosedInner == "1");
                if (ClosedOuter != null) ClosedOuter.IsChecked = (snapClosedOuter == "1");

                // Restore marker indices by stored marker line TEXT
                planeZIndex = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_PLANEZ_TEXT, ""));
                startXIndex = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_STARTX_TEXT, ""));
                startYIndex = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_STARTY_TEXT, ""));
                endXIndex = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_ENDX_TEXT, ""));
                endYIndex = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_ENDY_TEXT, ""));

                // If RegionLines is empty but markers are valid, build RegionLines silently now.
                // Use the shared builder + shared normalizer (no duplicated logic).
                if (set.RegionLines != null && set.RegionLines.Count == 0)
                {
                    var region = ExtractSelectedGcodeLines_Silent();
                    if (region.Count > 0 && !SelectOrderWrong)
                    {
                        int regionStartAbs = -1;
                        if (startXIndex >= 0 && startYIndex >= 0)
                            regionStartAbs = Math.Min(startXIndex, startYIndex);
                        else if (startXIndex >= 0)
                            regionStartAbs = startXIndex;
                        else
                            regionStartAbs = startYIndex;

                        int LocalOrMinus1(int absIdx)
                        {
                            if (absIdx < 0) return -1;
                            int local = absIdx - regionStartAbs;
                            if (local < 0 || local >= region.Count) return -1;
                            return local;
                        }

                        int planeLocal = LocalOrMinus1(planeZIndex);
                        int sxLocal = LocalOrMinus1(startXIndex);
                        int syLocal = LocalOrMinus1(startYIndex);
                        int exLocal = LocalOrMinus1(endXIndex);
                        int eyLocal = LocalOrMinus1(endYIndex);

                        BuildMillRegion.EditExisting(
                            set,
                            regionLines: region,
                            planeZIndex: planeLocal,
                            startXIndex: sxLocal,
                            startYIndex: syLocal,
                            endXIndex: exLocal,
                            endYIndex: eyLocal
                        );

                        // If plane Z is outside, preserve a usable unanchored key
                        if (planeLocal < 0)
                        {
                            var linesNow = GetGcodeLines();
                            if (planeZIndex >= 0 && planeZIndex < linesNow.Count)
                                set.PageSnapshot!.Values[KEY_PLANEZ_TEXT] =
                                    BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(linesNow[planeZIndex] ?? "");
                        }
                    }
                }

                // Resolve status + resolved range against current editor text
                ResolveAndUpdateMillStatus(set, GetGcodeLines());
            }
            finally
            {
                _isApplyingMillSet = false;
            }

            if (!suppressEditorRender)
            {
                RefreshButtonNames();
                RefreshHighlighting();
            }
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





        private static void ResolveAndUpdateMillStatus(RegionSet set, List<string> allLines)
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

            bool any = BuiltRegionSearches.FindMultiLine(
                allLines,
                set.RegionLines,
                out int start,
                out int end,
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
            set.ResolvedStartLine = start; // 0-based
            set.ResolvedEndLine = end;     // 0-based
        }



        // ============================================================
        // Snapshot support
        // ============================================================
        private void EnsureSelectedMillSnapshot(RegionSet set)
        {
            set.PageSnapshot ??= new UiStateSnapshot();
        }

        private void StoreMarkersIntoSelectedSet()
        {
            if (_isApplyingMillSet) return;

            var set = GetSelectedMillSetSafe();
            if (set == null)
                return;

            var lines = GetGcodeLines();

            // Raw marker lines (absolute indices)
            string? planeRaw = (planeZIndex >= 0 && planeZIndex < lines.Count) ? lines[planeZIndex] : null;
            string? sxRaw = (startXIndex >= 0 && startXIndex < lines.Count) ? lines[startXIndex] : null;
            string? syRaw = (startYIndex >= 0 && startYIndex < lines.Count) ? lines[startYIndex] : null;
            string? exRaw = (endXIndex >= 0 && endXIndex < lines.Count) ? lines[endXIndex] : null;
            string? eyRaw = (endYIndex >= 0 && endYIndex < lines.Count) ? lines[endYIndex] : null;

            var snapDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Always persist marker key TEXTs (even when region span is incomplete)
            if (!string.IsNullOrWhiteSpace(planeRaw))
                snapDefaults[KEY_PLANEZ_TEXT] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(planeRaw);
            if (!string.IsNullOrWhiteSpace(sxRaw))
                snapDefaults[KEY_STARTX_TEXT] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(sxRaw);
            if (!string.IsNullOrWhiteSpace(syRaw))
                snapDefaults[KEY_STARTY_TEXT] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(syRaw);
            if (!string.IsNullOrWhiteSpace(exRaw))
                snapDefaults[KEY_ENDX_TEXT] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(exRaw);
            if (!string.IsNullOrWhiteSpace(eyRaw))
                snapDefaults[KEY_ENDY_TEXT] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(eyRaw);

            // If we have a valid span (StartX/StartY + EndX/EndY), rebuild RegionLines now via builder.
            bool haveStart = (startXIndex >= 0 && startYIndex >= 0);
            bool haveEnd = (endXIndex >= 0 && endYIndex >= 0);

            if (haveStart && haveEnd)
            {
                int regionStartAbs = MinNonNeg(startXIndex, startYIndex, endXIndex, endYIndex, planeZIndex);
                int regionEndAbs = MaxNonNeg(startXIndex, startYIndex, endXIndex, endYIndex, planeZIndex);


                if (regionStartAbs >= 0 && regionEndAbs >= regionStartAbs && regionEndAbs < lines.Count)
                {
                    var region = lines.GetRange(regionStartAbs, regionEndAbs - regionStartAbs + 1);

                    int LocalOrMinus1(int absIdx)
                    {
                        if (absIdx < 0) return -1;
                        int local = absIdx - regionStartAbs;
                        if (local < 0 || local >= region.Count) return -1;
                        return local;
                    }

                    int planeLocal = LocalOrMinus1(planeZIndex);
                    int sxLocal = LocalOrMinus1(startXIndex);
                    int syLocal = LocalOrMinus1(startYIndex);
                    int exLocal = LocalOrMinus1(endXIndex);
                    int eyLocal = LocalOrMinus1(endYIndex);

                    // PlaneZ may be outside the region span. If outside, keep the stored key text
                    // from snapDefaults and do NOT pass a local PlaneZ index.
                    int? planeLocalOrNull = (planeLocal >= 0) ? planeLocal : (int?)null;

                    BuildMillRegion.EditExisting(
                        set,
                        regionLines: region,
                        planeZIndex: planeLocalOrNull,
                        startXIndex: (sxLocal >= 0) ? sxLocal : (int?)null,
                        startYIndex: (syLocal >= 0) ? syLocal : (int?)null,
                        endXIndex: (exLocal >= 0) ? exLocal : (int?)null,
                        endYIndex: (eyLocal >= 0) ? eyLocal : (int?)null,
                        snapshotDefaults: snapDefaults
                        
                    );

                    ResolveAndUpdateMillStatus(set, lines);
                    return;
                }
            }

            // No valid span yet: just persist keys (builder-only)
            BuildMillRegion.EditExisting(
                set,
                snapshotDefaults: snapDefaults
                
            );

            ResolveAndUpdateMillStatus(set, lines);
        }




        private void StoreMillParamsIntoSelectedSet()
        {
            if (_isApplyingMillSet) return;

            var set = GetSelectedMillSetSafe();
            if (set == null)
                return;

            // Route ALL writes through the builder
            BuildMillRegion.EditExisting(
                set,
                txtToolDia: TxtToolDia?.Text ?? "",
                txtToolLen: TxtToolLen?.Text ?? "",
                fuseAll: (Fuseall?.IsChecked == true) ? "1" : "0",
                clipper: (Clipper?.IsChecked == true) ? "1" : "0",
                removeSplitter: (RemoveSplitter?.IsChecked == true) ? "1" : "0",
                clipperIsland: (ClipperIsland?.IsChecked == true) ? "1" : "0",
                guidedTool: (GuidedTool?.IsChecked == true) ? "1" : "0",
                closedWire: (ClosedWire?.IsChecked == true) ? "1" : "0",
                closedInner: (ClosedInner?.IsChecked == true) ? "1" : "0",
                closedOuter: (ClosedOuter?.IsChecked == true) ? "1" : "0"
            );

            ResolveAndUpdateMillStatus(set, GetGcodeLines());
        }




        // -------------------------
        // IGcodePage
        // -------------------------
        public void OnGcodeModelLoaded()
        {
            startXIndex = -1;
            startYIndex = -1;
            endXIndex = -1;
            endYIndex = -1;
            selectedLineIndex = -1;
            SelectOrderWrong = false;
            planeZIndex = -1;

            RefreshButtonNames();
            RefreshHighlighting();
        }

        public void OnPageActivated()
        {
            RefreshButtonNames();
            RefreshHighlighting();

            // If a MillSet is currently selected, re-match markers now (survive edits)
            var set = GetSelectedMillSetSafe();
            if (set != null)
                ApplyMillSet(set);
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

                    int colonIndex = trimmed.IndexOf(':');
                    if (colonIndex >= 0 && colonIndex < 10)
                        trimmed = trimmed.Substring(colonIndex + 1).TrimStart();

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
            //ScrollToLine(targetIndex);

            // STORE: markers + params + region lines + resolve status
            StoreSelectionIntoSelectedSet();
        }

        private void StoreSelectionIntoSelectedSet()
        {
            if (_isApplyingMillSet) return;

            var set = GetSelectedMillSetSafe();
            if (set == null)
                return;

            EnsureSelectedMillSnapshot(set);

            // Store tool params (your existing behaviour)
            StoreMillParamsIntoSelectedSet();

            // Build raw region (absolute indices)
            var allLines = GetGcodeLines();

            // Must have a valid region span
            var regionRaw = ExtractSelectedGcodeLines();
            if (regionRaw == null || regionRaw.Count == 0 || SelectOrderWrong)
            {
                // Still store markers/params so user doesn’t lose selections
                StoreMarkersIntoSelectedSet();
                ResolveAndUpdateMillStatus(set, allLines);
                return;
            }

            // regionRaw now includes PlaneZ too, so regionStartAbs MUST match the expanded start
            int regionStartAbs = MinNonNeg(startXIndex, startYIndex, endXIndex, endYIndex, planeZIndex);

            if (regionStartAbs < 0)
            {
                StoreMarkersIntoSelectedSet();
                ResolveAndUpdateMillStatus(set, allLines);
                return;
            }


            // Convert absolute editor indices -> 0-based indices into regionRaw
            int LocalOrMinus1(int absIdx)
            {
                if (absIdx < 0) return -1;
                int local = absIdx - regionStartAbs;
                if (local < 0 || local >= regionRaw.Count) return -1;
                return local;
            }

            int planeLocal = LocalOrMinus1(planeZIndex);
            int sxLocal = LocalOrMinus1(startXIndex);
            int syLocal = LocalOrMinus1(startYIndex);
            int exLocal = LocalOrMinus1(endXIndex);
            int eyLocal = LocalOrMinus1(endYIndex);

            // Drive the canonical builder/edit path
            BuildMillRegion.EditExisting(
     set,
     regionLines: regionRaw,
     planeZIndex: planeLocal,
     startXIndex: sxLocal,
     startYIndex: syLocal,
     endXIndex: exLocal,
     endYIndex: eyLocal,
     txtToolDia: TxtToolDia?.Text ?? "",
     txtToolLen: TxtToolLen?.Text ?? "",
     fuseAll: (Fuseall?.IsChecked == true) ? "1" : "0",
     removeSplitter: (RemoveSplitter?.IsChecked == true) ? "1" : "0",
     clipper: (Clipper?.IsChecked == true) ? "1" : "0",
     clipperIsland: (ClipperIsland?.IsChecked == true) ? "1" : "0",
     guidedTool: (GuidedTool?.IsChecked == true) ? "1" : "0",
     closedWire: (ClosedWire?.IsChecked == true) ? "1" : "0",
     closedInner: (ClosedInner?.IsChecked == true) ? "1" : "0",
     closedOuter: (ClosedOuter?.IsChecked == true) ? "1" : "0"
 );


            // If plane Z was outside the region, keep your old behaviour: store unanchored normalized text.
            if (planeLocal < 0 && planeZIndex >= 0 && planeZIndex < allLines.Count)
            {
                set.PageSnapshot!.Values[KEY_PLANEZ_TEXT] =
                    BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(allLines[planeZIndex] ?? "");
            }

            // Resolve immediately against current editor
            ResolveAndUpdateMillStatus(set, allLines);
        }



        /// <summary>
        /// Same as ExtractSelectedGcodeLines, but NO MessageBoxes.
        /// Used during ApplyMillSet so selecting a set doesn't nag.
        /// </summary>
        private List<string> ExtractSelectedGcodeLines_Silent()
        {
            var lines = GetGcodeLines();

            SelectOrderWrong = false;

            if (startXIndex < 0 && startYIndex < 0)
                return new List<string>();

            if (endXIndex < 0 && endYIndex < 0)
                return new List<string>();

            int start = MinNonNeg(startXIndex, startYIndex);
            int end = MaxNonNeg(endXIndex, endYIndex);

            // Expand to include PlaneZ marker if set (before/inside/after)
            start = MinNonNeg(start, planeZIndex);
            end = MaxNonNeg(end, planeZIndex);

            if (start < 0 || end < 0)
                return new List<string>();

            if (end < start)
            {
                SelectOrderWrong = true;
                return new List<string>();
            }

            if (end >= lines.Count)
                return new List<string>();

            return lines.GetRange(start, end - start + 1);
        }

        // -------------------------
        // Button handlers
        // -------------------------
        private void BtnStartX_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref startXIndex);
        private void BtnStartY_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref startYIndex);
        private void BtnEndX_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref endXIndex);
        private void BtnEndY_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref endYIndex);
        private void BtnPlaneZ_Click(object sender, RoutedEventArgs e) => SelectLineFromCaret(ref planeZIndex);

        // TextChanged handlers referenced by XAML
        private void TxtToolDia_TextChanged(object sender, TextChangedEventArgs e) => StoreMillParamsIntoSelectedSet();
        private void TxtToolLen_TextChanged(object sender, TextChangedEventArgs e) => StoreMillParamsIntoSelectedSet();

        private void MillOptionRadio_Changed(object sender, RoutedEventArgs e) => StoreMillParamsIntoSelectedSet();



        private void RadioToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Make a RadioButton act like a toggle: click again => uncheck.
            if (sender is RadioButton rb && rb.IsChecked == true)
            {
                rb.IsChecked = false;
                e.Handled = true; // prevent WPF from forcing it back to checked
            }
        }




        // -------------------------
        // Scrolling / click handling
        // -------------------------
        private void ScrollToLine(int index)
        {
            var lines = GetGcodeLines();
            if (index < 0 || index >= lines.Count)
                return;

            var rtb = GetGcodeEditor();
            if (rtb == null)
                return;

            int currentLine = 0;
            foreach (Paragraph p in rtb.Document.Blocks)
            {
                if (currentLine == index)
                {
                    rtb.ScrollToHome();
                    rtb.ScrollToEnd();
                    p.BringIntoView();
                    break;
                }
                currentLine++;
            }
        }

        private void GcodeLine_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Paragraph p && p.Tag is int lineIndex)
            {
                SyncGcodeLinesFromEditor();
                selectedLineIndex = lineIndex;
                RefreshHighlighting();
            }
        }

        private void TxtGcode_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Not used
        }

        // -------------------------
        // Region validation
        // -------------------------
        private void ValidateSelectedRegion()
        {
            var lines = GetGcodeLines();
            if (lines.Count == 0) return;

            SyncGcodeLinesFromEditor();

            if (startXIndex < 0 && startYIndex < 0) return;
            if (endXIndex < 0 && endYIndex < 0) return;

            int start = -1;
            if (startXIndex >= 0 && startYIndex >= 0)
                start = (startXIndex < startYIndex) ? startXIndex : startYIndex;
            else if (startXIndex >= 0)
                start = startXIndex;
            else
                start = startYIndex;

            int end = -1;
            if (endXIndex >= 0 && endYIndex >= 0)
                end = (endXIndex > endYIndex) ? endXIndex : endYIndex;
            else if (endXIndex >= 0)
                end = endXIndex;
            else
                end = endYIndex;

            if (start < 0 || end < 0 || start > end)
                return;

            //for (int i = start - 1; i >= 0; i--)
            // {
            //if (TryGetLastMotionGCode(lines[i], out int g))
            //{
            //  if (g == 0)
            // {
            //  MessageBox.Show(
            //   $"ERROR: Selected region begins under modal G00 (rapid).\n" +
            //  $"Last motion before region is G00 at line {i + 1}.\n\n" +
            // $"Fix: ensure a G01/G02/G03 occurs before the region, or include it at the start of the region.");
            // return;
            //}
            // break;
            // }
            // }

            //for (int i = start; i <= end; i++)
            //{
            //if (TryGetLastMotionGCode(lines[i], out int g) && g == 0)
            // {
            // MessageBox.Show(
            //    $"ERROR: Rapid move (G00) found in selected region at line {i + 1}.\n" +
            //    "This region cannot be used for geometry.");
            // return;
            //}
            //}
        }

        // -------------------------
        // Button labels
        // -------------------------
        private void RefreshButtonNames()
        {
            BtnStartX.Content = FormatAxisButton(startXIndex, 'X', "Start X");
            BtnStartY.Content = FormatAxisButton(startYIndex, 'Y', "Start Y");
            BtnEndX.Content = FormatAxisButton(endXIndex, 'X', "End X");
            BtnEndY.Content = FormatAxisButton(endYIndex, 'Y', "End Y");

            if (BtnPlaneZ != null)
                BtnPlaneZ.Content = FormatPlaneZButton();
        }

        private string FormatAxisButton(int idx, char axis, string label)
        {
            var lines = GetGcodeLines();

            if (idx < 0 || idx >= lines.Count)
                return $"{label}: (none)";

            string line = lines[idx];

            if (TryGetCoord(line, axis, out double value))
                return $"{label}: {value.ToString("0.###", CultureInfo.InvariantCulture)}";

            string preview = line.Length > 20 ? line.Substring(0, 20) : line;
            return $"{label}: {preview}";
        }

        private string FormatPlaneZButton()
        {
            var lines = GetGcodeLines();

            if (planeZIndex < 0 || planeZIndex >= lines.Count)
                return "Z Plane: (none)";

            string line = lines[planeZIndex];

            if (TryGetCoord(line, 'Z', out double zVal))
                return $"Z Plane: {zVal.ToString("0.###", CultureInfo.InvariantCulture)}";

            string preview = line.Length > 20 ? line.Substring(0, 20) : line;
            return $"Z Plane: {preview}";
        }

        // ------------------------- 
        // Highlighting in shared editor
        // -------------------------
        // File: Pages/MillPage.xaml.cs (or wherever this Mill RefreshHighlighting lives)
        // Method: RefreshHighlighting()
        // Change: REMOVE unique-tag colouring entirely. Display the full line as normal text.
        //         Keep your robust arc detection logic using the original line (not tag-split).

        private void RefreshHighlighting()
        {
            // return if shift key is down
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
                return;

            var lines = GetGcodeLines();
            var rtb = GetGcodeEditor();
            if (rtb == null || lines == null)
                return;

            UiUtilities.ForceLinesUppercaseInPlace(lines);

            rtb.Document.Blocks.Clear();

            int regionStart = MinNonNeg(startXIndex, startYIndex, endXIndex, endYIndex, planeZIndex);
            int regionEnd = MaxNonNeg(startXIndex, startYIndex, endXIndex, endYIndex, planeZIndex);


            Debug.WriteLine("renum firedmill");

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i] ?? string.Empty;

                Brush regionBg = Brushes.Transparent;
                bool insideRegion = (regionStart >= 0 && regionEnd >= 0 && i >= regionStart && i <= regionEnd);
                if (insideRegion)
                    regionBg = Brushes.LightYellow;

                Brush fg = (i == selectedLineIndex) ? Brushes.Blue : Brushes.Black;

                // --- SAFE 4-segment split (FULL line text; NO tag splitting) ---
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
                Brush b2 = (i == startYIndex) ? Brushes.LightGreen : Brushes.Transparent;
                Brush b3 = (i == endXIndex) ? Brushes.LightSalmon : Brushes.Transparent;
                Brush b4 = (i == endYIndex) ? Brushes.Yellow : Brushes.Transparent;

                // FIX: robust arc detection (prevents G20/G21 etc falsely triggering "missing arcs")
                // Use FULL line text (tags included) exactly like before.
                if (insideRegion && TryGetLastMotionGCode(line ?? string.Empty, out int gArc) && (gArc == 2 || gArc == 3))
                {
                    bool hasI = TryGetCoord(line ?? string.Empty, 'I', out _);
                    bool hasJ = TryGetCoord(line ?? string.Empty, 'J', out _);
                    bool hasR = TryGetCoord(line ?? string.Empty, 'R', out _);

                    if (!hasI && !hasJ && !hasR)
                        regionBg = Brushes.LightCoral;
                }

                bool isPlaneZ = (i == planeZIndex);
                if (isPlaneZ)
                    regionBg = Brushes.LightPink;

                Paragraph p = new Paragraph { Margin = new Thickness(0) };
                p.Background = regionBg;

                p.Tag = i;
                p.MouseLeftButtonDown += GcodeLine_MouseLeftButtonDown;

                // line number
                UiUtilities.AddNumberedLinePrefix(p, i + 1, fg);

                // full line text (4 segments) — NO unique-tag colouring
                p.Inlines.Add(new Run(s1) { Background = b1, Foreground = fg });
                p.Inlines.Add(new Run(s2) { Background = b2, Foreground = fg });
                p.Inlines.Add(new Run(s3) { Background = b3, Foreground = fg });
                p.Inlines.Add(new Run(s4) { Background = b4, Foreground = fg });

                rtb.Document.Blocks.Add(p);
            }

            UiUtilities.RebuildAndStoreNumberedLineStartIndex(rtb);
        }





        // Unique tag styling: (u:xxxx) — light blue @ ~50% opacity
        private static readonly Brush UniqueTagBrush = UniqueTagColor.UniqueTagBrush;






        /// <summary>
        /// Finds the LAST motion G-code on a line.
        /// Treats G0/G00 as G1 (so rapids behave like feed moves for geometry).
        /// Returns true if found, and outputs gMotion = 1/2/3.
        /// </summary>
        private bool TryGetLastMotionGCode(string line, out int g)
        {
            g = -1;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            // 1) Strip line-number prefix + any parenthesis blocks.
            //    CRITICAL: this must remove tags like "(M:G0002)" so they cannot be parsed as motion.
            string s = line;

            // Remove optional "1234:" prefix (same rule you use elsewhere)
            // Keep it simple: if colon is early, strip.
            int colonIndex = s.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < 10)
                s = s.Substring(colonIndex + 1);

            // Remove ALL "(...)" blocks (repeat-safe)
            // This kills: (M:G0002) (u:a0001) comments, etc.
            s = Regex.Replace(s, @"\([^)]*\)", " ");

            s = (s ?? "").ToUpperInvariant();

            // 2) Find LAST valid motion token:
            //    Accept ONLY: G0/G00/G1/G01/G2/G02/G3/G03
            //    Reject: G0002, G1206, G17, G40, etc.
            //    (?!\d) ensures the token ends there (no extra digits)
            MatchCollection ms = Regex.Matches(s, @"G(?:0?[0-3])(?!\d)");
            if (ms == null || ms.Count == 0)
                return false;

            string tok = ms[ms.Count - 1].Value; // last motion token on line

            int motion;
            if (tok.Length == 2) // "G2"
                motion = tok[1] - '0';
            else                 // "G02"
                motion = tok[2] - '0';

            // 3) REQUIRED BEHAVIOR: treat G0/G00 as G1 (rapids behave like feed for geometry)
            if (motion == 0)
                motion = 1;

            g = motion; // 1/2/3 only
            return true;
        }





        // -------------------------
        // Coordinate helpers
        // -------------------------
        private (double startX, double startY) GetStartCoordinates()
        {
            var lines = GetGcodeLines();

            if (startXIndex < 0 || startXIndex >= lines.Count)
                throw new Exception("Invalid Start-X index.");

            string lx = lines[startXIndex];

            if (!TryGetCoord(lx, 'X', out double startX))
                throw new Exception($"Start-X line {startXIndex + 1} does not contain a valid X value.");

            if (startYIndex < 0 || startYIndex >= lines.Count)
                throw new Exception("Invalid Start-Y index.");

            string ly = lines[startYIndex];

            if (!TryGetCoord(ly, 'Y', out double startY))
                throw new Exception($"Start-Y line {startYIndex + 1} does not contain a valid Y value.");

            return (startX, startY);
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

        private double GetZPlaneValue()
        {
            var lines = GetGcodeLines();

            if (planeZIndex < 0 || planeZIndex >= lines.Count)
                return 0.0;

            string lz = lines[planeZIndex];

            if (TryGetCoord(lz, 'Z', out double zVal))
                return zVal;

            return 0.0;
        }

        // =========================
        // Geometry + export/viewer code
        // =========================

        private List<GeoMove> BuildGeometryFromGcode(List<string> regionLines, int regionStartIndex)
        {
            List<GeoMove> moves = new();

            var allLines = GetGcodeLines();

            (double startX, double startY) = GetStartCoordinates();
            double lastX = startX;
            double lastY = startY;

            MotionMode mode = MotionMode.None;
            for (int i = regionStartIndex - 1; i >= 0; i--)
            {
                // FIX: strip (M:....) etc BEFORE modal scan
                string pre = allLines[i] ?? "";
                pre = CNC_Improvements_gcode_solids.Utilities.GeneralNormalizers.StripLineNumberPrefixAndParenComments(pre);
                pre = pre.ToUpperInvariant().Trim();

                if (TryGetLastMotionGCode(pre, out int g))
                {
                    // KEEP EXISTING BEHAVIOR: treat G0 as G1 (no G0 mode introduced)
                    // (your original code: g==1 => G1, g==2 => G2, else => G3)
                    mode = (g == 1) ? MotionMode.G1
                         : (g == 2) ? MotionMode.G2
                         : MotionMode.G3;

                    break;
                }
            }

            int coordStartLine = Math.Max(startXIndex, startYIndex);

            for (int k = 0; k < regionLines.Count; k++)
            {
                string raw = regionLines[k] ?? "";

                // FIX: strip (M:....) etc BEFORE parsing this line
                string line = CNC_Improvements_gcode_solids.Utilities.GeneralNormalizers.StripLineNumberPrefixAndParenComments(raw);
                line = line.ToUpperInvariant().Trim();

                int physicalIndex = regionStartIndex + k;

                if (TryGetLastMotionGCode(line, out int gMotion))
                {
                    // KEEP EXISTING BEHAVIOR: if G0 or G1 => mode = G1
                    if (gMotion == 1 || gMotion == 0) mode = MotionMode.G1;
                    else if (gMotion == 2) mode = MotionMode.G2;
                    else if (gMotion == 3) mode = MotionMode.G3;
                }

                if (physicalIndex < coordStartLine)
                    continue;

                bool hasX = TryGetCoord(line, 'X', out double newX);
                bool hasY = TryGetCoord(line, 'Y', out double newY);

                if (!hasX) newX = lastX;
                if (!hasY) newY = lastY;

                if (mode == MotionMode.None)
                {
                    lastX = newX;
                    lastY = newY;
                    continue;
                }

                if (!hasX && !hasY)
                {
                    lastX = newX;
                    lastY = newY;
                    continue;
                }

                if (mode == MotionMode.G1)
                {
                    if (lastX == newX && lastY == newY)
                    {
                        lastX = newX;
                        lastY = newY;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = "LINE",
                        Xs = lastX,
                        Ys = lastY,
                        Xe = newX,
                        Ye = newY
                    });
                }
                else if (mode == MotionMode.G2 || mode == MotionMode.G3)
                {
                    double I = 0, J = 0, R = 0;

                    bool hasI = TryGetCoord(line, 'I', out I);
                    bool hasJ = TryGetCoord(line, 'J', out J);
                    bool hasR = TryGetCoord(line, 'R', out R);

                    bool useIJ = hasI || hasJ;

                    if (!useIJ && !hasR)
                        throw new Exception($"ERROR: Arc missing I/J or R at line {physicalIndex + 1}.");

                    // RULE: if I or J exists on the line, ignore R completely
                    if (useIJ)
                        R = 0.0;

                    if (lastX == newX && lastY == newY)
                    {
                        lastX = newX;
                        lastY = newY;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = (mode == MotionMode.G2) ? "ARC_CW" : "ARC_CCW",
                        Xs = lastX,
                        Ys = lastY,
                        Xe = newX,
                        Ye = newY,
                        I = I,
                        J = J,
                        R = R
                    });
                }

                lastX = newX;
                lastY = newY;
            }

            return moves;
        }




        private void GetArc3Points2D(
    GeoMove m,
    out double xs, out double ys,
    out double xm, out double ym,
    out double xe, out double ye)
        {
            xs = m.Xs;
            ys = m.Ys;
            xe = m.Xe;
            ye = m.Ye;

            double cx = 0.0;
            double cy = 0.0;
            double aStart = 0.0;
            double dAlpha = 0.0;

            bool useR = Math.Abs(m.R) > 1e-9;

            if (!useR)
            {
                cx = m.Xs + m.I;
                cy = m.Ys + m.J;

                double sx = m.Xs - cx;
                double sy = m.Ys - cy;
                double ex = m.Xe - cx;
                double ey = m.Ye - cy;

                double a1 = Math.Atan2(sy, sx);
                double a2 = Math.Atan2(ey, ex);
                double da = a2 - a1;

                if (m.Type == "ARC_CW")
                {
                    while (da >= 0.0) da -= 2.0 * Math.PI;   // CW => negative
                }
                else
                {
                    while (da <= 0.0) da += 2.0 * Math.PI;   // CCW => positive
                }

                aStart = a1;
                dAlpha = da;
            }
            else
            {
                double r = Math.Abs(m.R);

                double dx = m.Xe - m.Xs;
                double dy = m.Ye - m.Ys;
                double d = Math.Sqrt(dx * dx + dy * dy);

                if (d < 1e-9)
                    throw new Exception("R-arc with zero-length chord.");

                if (d > 2.0 * r + 1e-6)
                    throw new Exception("R too small for given arc endpoints.");

                double mx0 = (m.Xs + m.Xe) * 0.5;
                double my0 = (m.Ys + m.Ye) * 0.5;

                double px = -dy / d;
                double py = dx / d;

                double h = Math.Sqrt(Math.Max(r * r - (d * d * 0.25), 0.0));

                double cx1 = mx0 + h * px;
                double cy1 = my0 + h * py;
                double cx2 = mx0 - h * px;
                double cy2 = my0 - h * py;

                // candidate 1
                double s1x = m.Xs - cx1;
                double s1y = m.Ys - cy1;
                double e1x = m.Xe - cx1;
                double e1y = m.Ye - cy1;

                double a1_1 = Math.Atan2(s1y, s1x);
                double a2_1 = Math.Atan2(e1y, e1x);
                double da1 = a2_1 - a1_1;

                // FIX: correct sign for CW/CCW (same logic as IJ mode)
                if (m.Type == "ARC_CW")
                {
                    while (da1 >= 0.0) da1 -= 2.0 * Math.PI;   // CW => negative
                }
                else
                {
                    while (da1 <= 0.0) da1 += 2.0 * Math.PI;   // CCW => positive
                }

                // candidate 2
                double s2x = m.Xs - cx2;
                double s2y = m.Ys - cy2;
                double e2x = m.Xe - cx2;
                double e2y = m.Ye - cy2;

                double a1_2 = Math.Atan2(s2y, s2x);
                double a2_2 = Math.Atan2(e2y, e2x);
                double da2 = a2_2 - a1_2;

                // FIX: correct sign for CW/CCW (same logic as IJ mode)
                if (m.Type == "ARC_CW")
                {
                    while (da2 >= 0.0) da2 -= 2.0 * Math.PI;   // CW => negative
                }
                else
                {
                    while (da2 <= 0.0) da2 += 2.0 * Math.PI;   // CCW => positive
                }

                bool wantLong = (m.R < 0.0);

                bool ok1 = wantLong ? (Math.Abs(da1) > Math.PI) : (Math.Abs(da1) <= Math.PI);
                bool ok2 = wantLong ? (Math.Abs(da2) > Math.PI) : (Math.Abs(da2) <= Math.PI);

                if (ok1 && !ok2)
                {
                    cx = cx1;
                    cy = cy1;
                    aStart = a1_1;
                    dAlpha = da1;
                }
                else if (!ok1 && ok2)
                {
                    cx = cx2;
                    cy = cy2;
                    aStart = a1_2;
                    dAlpha = da2;
                }
                else
                {
                    // fallback: choose smaller sweep magnitude
                    if (Math.Abs(da1) <= Math.Abs(da2))
                    {
                        cx = cx1;
                        cy = cy1;
                        aStart = a1_1;
                        dAlpha = da1;
                    }
                    else
                    {
                        cx = cx2;
                        cy = cy2;
                        aStart = a1_2;
                        dAlpha = da2;
                    }
                }
            }

            double sxFinal = xs - cx;
            double syFinal = ys - cy;

            double radiusFinal = Math.Sqrt(sxFinal * sxFinal + syFinal * syFinal);
            double aMid = aStart + 0.5 * dAlpha;

            xm = cx + radiusFinal * Math.Cos(aMid);
            ym = cy + radiusFinal * Math.Sin(aMid);
        }



        private List<string> BuildShapeText(List<GeoMove> moves)
        {
            if (moves == null || moves.Count == 0)
                throw new Exception("No feed moves found in selection.");

            var inv = CultureInfo.InvariantCulture;

            static string F(double v)
            {
                if (!double.IsFinite(v))
                    throw new Exception("Non-finite number in geometry output.");
                return v.ToString("0.###############", CultureInfo.InvariantCulture);
            }

            List<string> outLines = new();

           

            foreach (var m in moves)
            {
                if (m.Type == "LINE")
                {
                    outLines.Add($"LINE {F(m.Xs)} {F(m.Ys)}   {F(m.Xe)} {F(m.Ye)}");
                }
                else if (m.Type == "ARC_CW" || m.Type == "ARC_CCW")
                {
                    // ALWAYS emit 3-point ARC3_* for the python parser (never emit "R ...")
                    GetArc3Points2D(
                        m,
                        out double xs, out double ys,
                        out double xm, out double ym,
                        out double xe, out double ye);

                    string tag = (m.Type == "ARC_CW") ? "ARC3_CW" : "ARC3_CCW";
                    outLines.Add($"{tag} {F(xs)} {F(ys)}   {F(xm)} {F(ym)}   {F(xe)} {F(ye)}");
                }
                else
                {
                    throw new Exception($"Unknown move type '{m.Type}' in BuildShapeText.");
                }
            }

            return outLines;
        }













        public List<string> ExtractSelectedGcodeLines()
        {
            var lines = GetGcodeLines();

            SelectOrderWrong = false;

            if (startXIndex < 0 && startYIndex < 0)
                return new List<string>();

            if (endXIndex < 0 && endYIndex < 0)
                return new List<string>();

            int start = MinNonNeg(startXIndex, startYIndex);
            int end = MaxNonNeg(endXIndex, endYIndex);

            // Expand to include PlaneZ marker if set (before/inside/after)
            start = MinNonNeg(start, planeZIndex);
            end = MaxNonNeg(end, planeZIndex);

            

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





        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            // Single selected-set export (FUSE or CLIPPER) — one readable flow.
            // - Same STEP filename regardless of fuse/clipper.
            // - Fuse uses:  TDIA + TLENGTH + ZPLANE + segs
            // - Clipper uses: TLENGTH + ZPLANE + (viewer clipper body)
            // - Both use transform flags.
            // - Clipper path uses FreeCadScriptMillClipper / FreeCadRunnerMillClipper.

            FreeCadRunSuffix.ResetMill();
            FreeCadRunSuffix.ResetMillClipper();
            try
            {
                UiUtilities.CloseAllToolWindows();
                SyncGcodeLinesFromEditor();

                var main = GetMain();
                var allLines = GetGcodeLines();

                if (allLines == null || allLines.Count == 0)
                    throw new Exception("No G-code loaded.");

                var set = GetSelectedMillSetSafe();
                if (set == null)
                    throw new Exception("Select a MILL region first.");

                string exportDir = main.GetExportDirectory();
                if (string.IsNullOrWhiteSpace(exportDir))
                    throw new Exception("Export directory is not valid.");

                Directory.CreateDirectory(exportDir);

                // -----------------------------
                // 1) Gather UI params (common)
                // -----------------------------
                if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double toolDia))
                {
                    if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out toolDia))
                        throw new Exception("Invalid Tool Dia.");
                }
                if (!double.IsFinite(toolDia) || toolDia <= 0.0)
                    throw new Exception("Tool diameter must be > 0.");

                double toolLen = 20.0;
                if (TxtToolLen != null && !string.IsNullOrWhiteSpace(TxtToolLen.Text))
                {
                    if (!double.TryParse(TxtToolLen.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out toolLen))
                        double.TryParse(TxtToolLen.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out toolLen);
                }

                double zPlane = GetZPlaneValue();

                bool optFuseAll = (Fuseall?.IsChecked == true);
                bool optRemoveSplitter = (RemoveSplitter?.IsChecked == true);
                bool optClipper = (Clipper?.IsChecked == true);
                bool optClipperIsland = (ClipperIsland?.IsChecked == true);

                bool isClipperPath = optClipper || optClipperIsland;
                bool includeIslands = optClipperIsland;

                // Per-set wire interpretation (persisted in RegionSet snapshot)
                bool isClosedWire = (GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_WIRE, "0") == "1");
                bool cwInner = (GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_INNER, "0") == "1");
                bool cwOuter = (GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_OUTER, "1") == "1");

                // Closed CL Wire: behave as if "Create Clipper Solid" is selected.
                // (Ignore GuidedTool/Fuse path selection for this set.)
                if (isClosedWire)
                {
                    isClipperPath = true;

                    // includeIslands flag is irrelevant for ClosedWire (MillViewWindow decides output based on cwInner/cwOuter),
                    // but keep it true to ensure island parsing is enabled if present.
                    includeIslands = true;
                }





                // -----------------------------
                // 2) Build transform text (common)
                // -----------------------------
                var inv = CultureInfo.InvariantCulture;

                main.TryGetTransformForRegion(
                    set.Name,
                    out double rotYDeg,
                    out double rotZDeg,
                    out double tx,
                    out double ty,
                    out double tz,
                    out string matrixName);

                string transText = $@"
#{matrixName}
TRANSFORM_ROTZ = {rotZDeg.ToString("0.###", inv)}
TRANSFORM_ROTY  = {rotYDeg.ToString("0.###", inv)}
TRANSFORM_TX = {tx.ToString("0.###", inv)}
TRANSFORM_TY = {ty.ToString("0.###", inv)}
TRANSFORM_TZ  = {tz.ToString("0.###", inv)}
";

                // -----------------------------
                // 3) Resolve region by markers + build moves (common)
                // -----------------------------
                if (!TryGetSetRegionRangeByMarkers(set, allLines,
                        out int regionStartIndex, out int regionEndIndex,
                        out int startXIdx, out int startYIdx))
                {
                    throw new Exception("Region markers could not be resolved (StartX/StartY/EndX/EndY).");
                }

                if (regionStartIndex < 0 || regionEndIndex < regionStartIndex || regionEndIndex >= allLines.Count)
                    throw new Exception("Resolved marker range is invalid for current editor text.");

                if (!TryGetCoord(allLines[startXIdx], 'X', out double startX))
                    throw new Exception("Could not read Start X from marker line.");

                if (!TryGetCoord(allLines[startYIdx], 'Y', out double startY))
                    throw new Exception("Could not read Start Y from marker line.");

                int coordStartLine = Math.Max(startXIdx, startYIdx);

                var regionLines = allLines.GetRange(regionStartIndex, regionEndIndex - regionStartIndex + 1);
                var moves = BuildGeometryFromGcode_ForSetViewAll(regionLines, regionStartIndex, allLines, startX, startY, coordStartLine);

                if (moves == null || moves.Count == 0)
                    throw new Exception("No toolpath geometry produced.");

                // -----------------------------
                // STEP filename: SAME for fuse/clipper
                // -----------------------------
                string safe = MainWindow.SanitizeFileStem(set.Name);
                string stepPath = Path.Combine(exportDir, $"{safe}_Mill_stp.stp");

                // =====================================================================================
                // 4) Branch: FUSE path  (FreeCadScriptMill / FreeCadRunnerMill)
                // =====================================================================================
                if (!isClipperPath)
                {
                    // Build seg text for python (TDIA + TLENGTH + ZPLANE + segs)
                    var shapeLines = BuildShapeText(moves);

                    var sb = new StringBuilder();
                    sb.AppendLine($"TDIA {toolDia.ToString(inv)}");
                    sb.AppendLine($"TLENGTH {toolLen.ToString(inv)}");
                    sb.AppendLine($"ZPLANE  {zPlane.ToString(inv)}");
                    for (int i = 0; i < shapeLines.Count; i++)
                        sb.AppendLine(shapeLines[i]);

                    // Assign script inputs
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.TransPY = transText;
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.MillShape = sb.ToString();

                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.Fuseall = optFuseAll ? "True" : "False";
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.RemoveSplitter = optRemoveSplitter ? "True" : "False";

                    // Run FreeCAD
                    string scriptPath = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMill.SaveScript(stepPath);
                    _ = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMill.RunFreeCad(scriptPath, exportDir);

                    if (!main.IsExportAllRunning)
                        MessageBox.Show("Export complete.", "Mill Export", MessageBoxButton.OK, MessageBoxImage.Information);

                    return;
                }

                // =====================================================================================
                // 5) Branch: CLIPPER path (MillViewWindow -> FreeCadScriptMillClipper / RunnerMillClipper)
                // =====================================================================================

                // Build viewer segs list (the viewer already knows how to clip + emit loops)
                var segs = new List<CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg>();
                int segIndex = 0;

                for (int i = 0; i < moves.Count; i++)
                {
                    var m = moves[i];

                    if (m.Type == "LINE")
                    {
                        segs.Add(new CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg
                        {
                            Index = segIndex++,
                            Type = "LINE",
                            X1 = m.Xs,
                            Y1 = m.Ys,
                            X2 = m.Xe,
                            Y2 = m.Ye,
                            ToolDia = toolDia,
                            RegionName = set.Name,
                            MatrixName = matrixName,
                            RotZDeg = rotZDeg,
                            RotYDeg = rotYDeg,
                            Tx = tx,
                            Ty = ty,
                            Tz = tz,
                            RegionColor = Utilities.UiUtilities.HexBrush(Settings.Default.ProfileColor),

                            // NEW: per-set wire mode flags for viewer/export
                            IsClosedWire = isClosedWire,
                            ClosedWireInner = cwInner,
                            ClosedWireOuter = cwOuter
                        });
                    }
                    else if (m.Type == "ARC_CW" || m.Type == "ARC_CCW")
                    {
                        GetArc3Points2D(
                            m,
                            out double xs, out double ys,
                            out double xm, out double ym,
                            out double xe, out double ye);

                        segs.Add(new CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg
                        {
                            Index = segIndex++,
                            Type = (m.Type == "ARC_CW") ? "ARC3_CW" : "ARC3_CCW",
                            X1 = xs,
                            Y1 = ys,
                            Xm = xm,
                            Ym = ym,
                            X2 = xe,
                            Y2 = ye,
                            ToolDia = toolDia,
                            RegionName = set.Name,
                            MatrixName = matrixName,
                            RotZDeg = rotZDeg,
                            RotYDeg = rotYDeg,
                            Tx = tx,
                            Ty = ty,
                            Tz = tz,
                            RegionColor = Utilities.UiUtilities.HexBrush(Settings.Default.ProfileColor),

                            // NEW: per-set wire mode flags for viewer/export
                            IsClosedWire = isClosedWire,
                            ClosedWireInner = cwInner,
                            ClosedWireOuter = cwOuter
                        });
                    }
                }

                // Create viewer and ask it for the python-friendly clipper body text.
                // IMPORTANT: use the return value directly (do NOT re-fetch it via reflection).
                var vw = new CNC_Improvements_gcode_solids.Utilities.MillViewWindow(toolDia, toolLen, zPlane, segs);

                // If your MillViewWindow ctor already stores segs, this is harmless.
                // If it doesn't, this is required.
                vw.LoadSegmentsForClipper(segs);

                // This should return the body text (loops/lines/arcs) that FreeCadScriptMillClipper expects.
                string clipperBody = vw.BuildClipperExportText(includeIslands) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(clipperBody))
                    throw new Exception("Clipper export failed: BuildClipperExportText() returned empty text.");

                // Build final CLIPPER_TEXT for python: TLENGTH + ZPLANE + body (NO TDIA)
                var sbClip = new StringBuilder();
                sbClip.AppendLine($"TLENGTH {toolLen.ToString(inv)}");
                sbClip.AppendLine($"ZPLANE  {zPlane.ToString(inv)}");
                sbClip.AppendLine(clipperBody.TrimEnd());

                // Assign script inputs
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMillClipper.TransPY = transText;
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMillClipper.MillShapeClipper = sbClip.ToString();

                // If these are public static properties, set via reflection safely:
                var clipperType = typeof(CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMillClipper);
                var pMergeAll = clipperType.GetProperty("MergeAll", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (pMergeAll != null && pMergeAll.CanWrite && pMergeAll.PropertyType == typeof(string))
                    pMergeAll.SetValue(null, "True"); // default: merge loops (adjust if your script expects otherwise)

                var pRemoveSplit = clipperType.GetProperty("MergeRemoveSplitterAtEnd", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (pRemoveSplit != null && pRemoveSplit.CanWrite && pRemoveSplit.PropertyType == typeof(string))
                    pRemoveSplit.SetValue(null, optRemoveSplitter ? "True" : "False");

                // Run FreeCAD (clipper runner)
                string scriptPathClip = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMillClipper.SaveScript(stepPath);
                _ = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMillClipper.RunFreeCad(scriptPathClip, exportDir);

                if (!main.IsExportAllRunning)
                    MessageBox.Show("Export complete.", "Mill Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Mill Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }



















        // ============================================================
        // BATCH EXPORT API (called by MainWindow ExportAll)
        // ============================================================
        public bool ExportSetBatch(RegionSet set, string exportDir, out string failReason)
        {
            return ExportMillSetCore(set, exportDir, batchMode: true, out failReason);
        }

        private bool ExportMillSetCore(RegionSet set, string exportDir, bool batchMode, out string failReason)
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

                // Apply without re-rendering the shared editor (don’t trash the user’s view during batch)
                ApplyMillSetInternal(set, suppressEditorRender: true);

                // Ensure we are using latest text edits
                SyncGcodeLinesFromEditor();

                // tool dia/len from UI (restored from snapshot by ApplyMillSetInternal)
                if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double toolDia))
                {
                    if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out toolDia))
                    {
                        failReason = "Invalid Tool Dia.";
                        return false;
                    }
                }
                if (!double.IsFinite(toolDia) || toolDia <= 0.0)
                {
                    failReason = "Tool diameter must be > 0.";
                    return false;
                }

                double toolLen = 20.0;
                if (TxtToolLen != null && !string.IsNullOrWhiteSpace(TxtToolLen.Text))
                {
                    if (!double.TryParse(TxtToolLen.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out toolLen))
                        double.TryParse(TxtToolLen.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out toolLen);
                }

                double zPlane = GetZPlaneValue();

                // IMPORTANT: these radios are per-set (restored by ApplyMillSetInternal)
                bool optFuseAll = (Fuseall?.IsChecked == true);
                bool optRemoveSplitter = (RemoveSplitter?.IsChecked == true);
                bool optClipper = (Clipper?.IsChecked == true);
                bool optClipperIsland = (ClipperIsland?.IsChecked == true);

                bool isClipperPath = optClipper || optClipperIsland;
                bool includeIslands = optClipperIsland;

                // Per-set wire interpretation (persisted in RegionSet snapshot)
                bool isClosedWire = (GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_WIRE, "0") == "1");
                bool cwInner = (GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_INNER, "0") == "1");
                bool cwOuter = (GetSnapshotOrDefault(set, KEY_WIRE_CLOSED_OUTER, "1") == "1");

                // Closed CL Wire: behave as if "Create Clipper Solid" is selected for this set.
                if (isClosedWire)
                {
                    isClipperPath = true;
                    includeIslands = true;
                }





                // Fetch transform for THIS region/set name
                main.TryGetTransformForRegion(
                    set.Name,
                    out double rotYDeg,
                    out double rotZDeg,
                    out double tx,
                    out double ty,
                    out double tz,
                    out string matrixName);

                string transText = $@"
#{matrixName}
TRANSFORM_ROTZ = {rotZDeg.ToString("0.###", inv)}
TRANSFORM_ROTY  = {rotYDeg.ToString("0.###", inv)}
TRANSFORM_TX = {tx.ToString("0.###", inv)}
TRANSFORM_TY = {ty.ToString("0.###", inv)}
TRANSFORM_TZ  = {tz.ToString("0.###", inv)}
";

                var allLines = GetGcodeLines();
                if (allLines == null || allLines.Count == 0)
                {
                    failReason = "No G-code loaded.";
                    return false;
                }

                // Marker-based range (same as View All)
                if (!TryGetSetRegionRangeByMarkers(set, allLines,
                        out int regionStartIndex, out int regionEndIndex,
                        out int startXIdx, out int startYIdx))
                {
                    failReason = "Region markers could not be resolved (StartX/StartY/EndX/EndY).";
                    return false;
                }

                if (regionStartIndex < 0 || regionEndIndex < regionStartIndex || regionEndIndex >= allLines.Count)
                {
                    failReason = "Resolved marker range is invalid for current editor text.";
                    return false;
                }

                if (!TryGetCoord(allLines[startXIdx], 'X', out double startX))
                {
                    failReason = "Could not read Start X from marker line.";
                    return false;
                }

                if (!TryGetCoord(allLines[startYIdx], 'Y', out double startY))
                {
                    failReason = "Could not read Start Y from marker line.";
                    return false;
                }

                int coordStartLine = Math.Max(startXIdx, startYIdx);

                var region = allLines.GetRange(regionStartIndex, regionEndIndex - regionStartIndex + 1);

                // Build geometry (supports I/J and R arcs)
                var moves = BuildGeometryFromGcode_ForSetViewAll(region, regionStartIndex, allLines, startX, startY, coordStartLine);
                if (moves == null || moves.Count == 0)
                {
                    failReason = "No toolpath geometry produced.";
                    return false;
                }

                Directory.CreateDirectory(exportDir);

                // STEP filename: SAME for fuse/clipper (match single-export)
                string safe = MainWindow.SanitizeFileStem(set.Name);
                string stepPath = Path.Combine(exportDir, $"{safe}_Mill_stp.stp");

                // ============================================================
                // FUSE PATH (existing behaviour)
                // ============================================================
                if (!isClipperPath)
                {
                    var shapeLines = BuildShapeText(moves);

                    var shapeText = new StringBuilder();
                    shapeText.AppendLine($"TDIA {toolDia.ToString(inv)}");
                    shapeText.AppendLine($"TLENGTH {toolLen.ToString(inv)}");
                    shapeText.AppendLine($"ZPLANE  {zPlane.ToString(inv)}");
                    foreach (var s in shapeLines)
                        shapeText.AppendLine(s);

                    // Optional debug write
                    if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
                    {
                        string txtPath = Path.Combine(exportDir, $"{safe}_mill.txt");
                        File.WriteAllText(txtPath, shapeText.ToString());
                    }

                    // Assign script inputs
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.TransPY = transText;
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.MillShape = shapeText.ToString();

                    // merge options (python wants True/False)
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.Fuseall = optFuseAll ? "True" : "False";
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.RemoveSplitter = optRemoveSplitter ? "True" : "False";

                    // Run FreeCAD
                    string scriptPath = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMill.SaveScript(stepPath);
                    try
                    {
                        _ = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMill.RunFreeCad(scriptPath, exportDir);
                    }
                    catch (Exception fcEx)
                    {
                        failReason = $"FreeCAD failed: {fcEx.Message}";
                        return false;
                    }

                    // EXPORT-ALL TRACKING
                    var mainwin = Application.Current?.MainWindow as MainWindow;
                    if (mainwin != null)
                        mainwin.ExportAllCreatedStepFiles.Add(stepPath);

                    return true;
                }

                // ============================================================
                // CLIPPER PATH (new: match single-export behaviour)
                // ============================================================

                // Build viewer segs list (viewer clips + emits loops)
                var segs = new List<CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg>();
                int segIndex = 0;

                for (int i = 0; i < moves.Count; i++)
                {
                    var m = moves[i];

                    if (m.Type == "LINE")
                    {
                        segs.Add(new CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg
                        {
                            Index = segIndex++,
                            Type = "LINE",
                            X1 = m.Xs,
                            Y1 = m.Ys,
                            X2 = m.Xe,
                            Y2 = m.Ye,
                            ToolDia = toolDia,
                            RegionName = set.Name,
                            MatrixName = matrixName,
                            RotZDeg = rotZDeg,
                            RotYDeg = rotYDeg,
                            Tx = tx,
                            Ty = ty,
                            Tz = tz,
                            RegionColor = Utilities.UiUtilities.HexBrush(Settings.Default.ProfileColor),

                            // IMPORTANT: carry Closed CL Wire mode into the viewer/export
                            IsClosedWire = isClosedWire,
                            ClosedWireInner = cwInner,
                            ClosedWireOuter = cwOuter
                        });
                    
                    }
                    else if (m.Type == "ARC_CW" || m.Type == "ARC_CCW")
                    {
                        GetArc3Points2D(
                            m,
                            out double xs, out double ys,
                            out double xm, out double ym,
                            out double xe, out double ye);

                        segs.Add(new CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg
                        {
                            Index = segIndex++,
                            Type = (m.Type == "ARC_CW") ? "ARC3_CW" : "ARC3_CCW",
                            X1 = xs,
                            Y1 = ys,
                            Xm = xm,
                            Ym = ym,
                            X2 = xe,
                            Y2 = ye,
                            ToolDia = toolDia,
                            RegionName = set.Name,
                            MatrixName = matrixName,
                            RotZDeg = rotZDeg,
                            RotYDeg = rotYDeg,
                            Tx = tx,
                            Ty = ty,
                            Tz = tz,
                            RegionColor = Utilities.UiUtilities.HexBrush(Settings.Default.ProfileColor),

                            // IMPORTANT: carry Closed CL Wire mode into the viewer/export
                            IsClosedWire = isClosedWire,
                            ClosedWireInner = cwInner,
                            ClosedWireOuter = cwOuter
                        });

                    }
                }

                // Use the viewer as the single source of truth for clipper export text
                var vw = new CNC_Improvements_gcode_solids.Utilities.MillViewWindow(toolDia, toolLen, zPlane, segs);

                // If your viewer needs an explicit load call, keep it (harmless if it just assigns)
                vw.LoadSegmentsForClipper(segs);

                // This should populate the internal clipper export body text
                string clipperBody = vw.BuildClipperExportText(includeIslands) ?? string.Empty;


                if (string.IsNullOrWhiteSpace(clipperBody))
                {
                    failReason = "Clipper export failed: viewer returned empty clipper shape text.";
                    return false;
                }

                // Final CLIPPER_TEXT for python: TLENGTH + ZPLANE + body (NO TDIA)
                var clipText = new StringBuilder();
                clipText.AppendLine($"TLENGTH {toolLen.ToString(inv)}");
                clipText.AppendLine($"ZPLANE  {zPlane.ToString(inv)}");
                clipText.AppendLine(clipperBody.TrimEnd());

                // Optional debug write
                if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
                {
                    string txtPath = Path.Combine(exportDir, $"{safe}_mill_clipper.txt");
                    File.WriteAllText(txtPath, clipText.ToString());
                }

                // Assign script inputs (clipper script)
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMillClipper.TransPY = transText;
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMillClipper.MillShapeClipper = clipText.ToString();

                // If these exist in your clipper script, keep them (same intent as single-export)
                // If they DON'T exist, delete these two lines.

                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMillClipper.CutRemoveSplitterAtEnd = optRemoveSplitter ? "True" : "False";

                // Run FreeCAD (clipper runner)
                string scriptPathClip = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMillClipper.SaveScript(stepPath);
                try
                {
                    _ = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMillClipper.RunFreeCad(scriptPathClip, exportDir);
                }
                catch (Exception fcEx)
                {
                    failReason = $"FreeCAD failed: {fcEx.Message}";
                    return false;
                }

                // EXPORT-ALL TRACKING
                var mw = Application.Current?.MainWindow as MainWindow;
                if (mw != null)
                    mw.ExportAllCreatedStepFiles.Add(stepPath);

                return true;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                return false;
            }
        }





        // ============================================================
        // Legacy single-export fallback (selection-based)
        // ============================================================
        private bool ExportCurrentSelectionCore(string exportDir, string fileStem, bool batchMode, out string failReason)
        {
            failReason = "";

            try
            {
                var main = GetMain();
                var inv = CultureInfo.InvariantCulture;
                // Fetch transform for THIS region/set name (same helper as Mill)
                main.TryGetTransformForRegion(
                    fileStem,
                    out double rotYDeg,
                    out double rotZDeg,
                    out double tx,
                    out double ty,
                    out double tz,
                    out string matrixName);

                FreeCadScriptMill.TransPY = $@"
#{matrixName}
TRANSFORM_ROTZ = {rotZDeg.ToString("0.###", inv)}
TRANSFORM_ROTY  = {rotYDeg.ToString("0.###", inv)}
TRANSFORM_TX = {tx.ToString("0.###", inv)}
TRANSFORM_TY = {ty.ToString("0.###", inv)}
TRANSFORM_TZ  = {tz.ToString("0.###", inv)}
";








                SyncGcodeLinesFromEditor();

                if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double toolDia))
                {
                    if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out toolDia))
                    {
                        failReason = "Enter a valid numeric tool diameter.";
                        return false;
                    }
                }

                if (toolDia <= 0.0)
                {
                    failReason = "Tool diameter must be greater than zero.";
                    return false;
                }

                double toolLen = 20.0;
                double zPlane = GetZPlaneValue();

                var region = ExtractSelectedGcodeLines();
                if (region.Count == 0)
                {
                    failReason = "No valid G-code region selected.";
                    return false;
                }

                int regionStartIndex = -1;
                if (startXIndex >= 0 && startYIndex >= 0)
                    regionStartIndex = (startXIndex < startYIndex) ? startXIndex : startYIndex;
                else if (startXIndex >= 0)
                    regionStartIndex = startXIndex;
                else
                    regionStartIndex = startYIndex;

                if (regionStartIndex < 0)
                {
                    failReason = "No valid region start selected.";
                    return false;
                }

                var moves = BuildGeometryFromGcode(region, regionStartIndex);
                var shapeLines = BuildShapeText(moves);

                var shapeText = new StringBuilder();
                shapeText.AppendLine($"TDIA {toolDia.ToString(CultureInfo.InvariantCulture)}");
                shapeText.AppendLine($"TLENGTH {toolLen.ToString(CultureInfo.InvariantCulture)}");
                shapeText.AppendLine($"ZPLANE  {zPlane.ToString(CultureInfo.InvariantCulture)}");
                foreach (var s in shapeLines)
                    shapeText.AppendLine(s);

                Directory.CreateDirectory(exportDir);

                string txtPath = Path.Combine(exportDir, $"{fileStem}_mill.txt");
                File.WriteAllText(txtPath, shapeText.ToString());

                string stepPath = Path.Combine(exportDir, $"{fileStem}_Mill_stp.stp");

                // --- NEW: drive FreeCAD merge options from UI radios (python wants True/False) ---
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.Fuseall = (Fuseall?.IsChecked == true) ? "True" : "False";
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.RemoveSplitter = (RemoveSplitter?.IsChecked == true) ? "True" : "False";

                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptMill.MillShape = shapeText.ToString();

                string scriptPath = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMill.SaveScript(stepPath);
                _ = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerMill.RunFreeCad(scriptPath, exportDir);

                return true;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                return false;
            }
        }













        //=================================================================


        private void BtnViewAllMilling_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // MillViewWindow.BtnClipperD.visability=invisable;

                UiUtilities.CloseAllToolWindows();
                SyncGcodeLinesFromEditor();

                var main = GetMain();
                var allLines = GetGcodeLines();

                if (main.MillSets == null || main.MillSets.Count == 0)
                    throw new Exception("No MILL sets exist. Add at least one MILL region first.");

                // Optional UI tool dia (used ONLY as fallback if a set has no saved tool dia)
                double uiToolDia = double.NaN;
                if (TxtToolDia != null && !string.IsNullOrWhiteSpace(TxtToolDia.Text))
                {
                    if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out uiToolDia))
                        double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out uiToolDia);

                    if (!double.IsFinite(uiToolDia) || uiToolDia <= 0.0)
                        uiToolDia = double.NaN;
                }

                // VIEW ALL must not depend on RegionLines/Resolve status.
                var segsAll = new List<CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg>();
                int segIndex = 0;

                int colorIndex = 0;

                var sbLog = new System.Text.StringBuilder();
                var inv = CultureInfo.InvariantCulture;

                sbLog.AppendLine("=== VIEW ALL MILLING : DEBUG ===");
                sbLog.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sbLog.AppendLine($"MillSets: {main.MillSets.Count}");
                sbLog.AppendLine();

                int built = 0;
                int skipped = 0;

                // We need SOME tool dia to pass to viewer ctor (only used as fallback if a seg has no ToolDia)
                double ctorFallbackToolDia = double.IsFinite(uiToolDia) ? uiToolDia : double.NaN;

                for (int setIdx = 0; setIdx < main.MillSets.Count; setIdx++)
                {
                    var set = main.MillSets[setIdx];
                    if (set == null)
                    {
                        skipped++;
                        continue;
                    }

                    // NEW: only include sets that are enabled for "View All"
                    if (!set.ShowInViewAll)
                    {
                        skipped++;
                        continue;
                    }

                    // ---------- Wire mode flags (from set snapshot) ----------
                    bool snapGuided = (GetSnapshotOrDefault(set, "GuidedTool", "1") == "1");
                    bool snapClosedWire = (GetSnapshotOrDefault(set, "ClosedWire", "0") == "1");
                    bool snapInner = (GetSnapshotOrDefault(set, "ClosedInner", "0") == "1");
                    bool snapOuter = (GetSnapshotOrDefault(set, "ClosedOuter", "1") == "1");

                    bool isClosedWire = snapClosedWire; // ClosedWire wins if dirty state
                    bool cwInner = false;
                    bool cwOuter = false;

                    if (isClosedWire)
                    {
                        cwInner = snapInner;
                        cwOuter = snapOuter;

                        if (!cwInner && !cwOuter) cwOuter = true;           // consistent default
                        if (cwInner && cwOuter) { cwInner = false; cwOuter = true; } // prefer outer
                    }

                    // one color per SET (region)
                    Brush setColor = Brushes.Gray;
                    if (ColorList != null && ColorList.Count > 0)
                    {
                        if (colorIndex >= ColorList.Count) colorIndex = 0;   // wrap
                        setColor = ColorList[colorIndex];
                    }

                    sbLog.AppendLine($"--- SET {setIdx + 1}/{main.MillSets.Count}: {set.Name} ---");

                    sbLog.AppendLine($"WireMode: {(isClosedWire ? "ClosedWire" : "GuidedTool")}");
                    if (isClosedWire)
                        sbLog.AppendLine($"ClosedWire: {(cwInner ? "Pocket/Inner" : "Outer")}");

                    // Per-set tool dia (THIS is the fix)
                    bool hasSetTool = TryGetToolDiaForSet(set, out double setToolDia);
                    if (!hasSetTool)
                    {
                        setToolDia = uiToolDia; // fallback (may be NaN)
                        sbLog.AppendLine($"ToolDia(set): <missing/invalid>  FALLBACK(UI): {(double.IsFinite(setToolDia) ? setToolDia.ToString("0.###", inv) : "NaN")}");
                    }
                    else
                    {
                        sbLog.AppendLine($"ToolDia(set): {setToolDia.ToString("0.###", inv)}");
                    }

                    if (!double.IsFinite(ctorFallbackToolDia) && double.IsFinite(setToolDia) && setToolDia > 0.0)
                        ctorFallbackToolDia = setToolDia;

                    // fetch transform for THIS region/set name
                    main.TryGetTransformForRegion(
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

                    if (!TryGetSetRegionRangeByMarkers(set, allLines,
                            out int regionStartIndex, out int regionEndIndex,
                            out int startXIdx, out int startYIdx))
                    {
                        sbLog.AppendLine("SKIP: no valid saved markers after load.");
                        sbLog.AppendLine();
                        skipped++;
                        continue;
                    }

                    sbLog.AppendLine($"Region: L{regionStartIndex}..L{regionEndIndex}");
                    sbLog.AppendLine($"StartX marker line: L{startXIdx}");
                    sbLog.AppendLine($"StartY marker line: L{startYIdx}");

                    var region = allLines.GetRange(regionStartIndex, regionEndIndex - regionStartIndex + 1);

                    if (!TryGetCoord(allLines[startXIdx], 'X', out double startX))
                    {
                        sbLog.AppendLine("SKIP: could not read StartX from marker line.");
                        sbLog.AppendLine();
                        skipped++;
                        continue;
                    }

                    if (!TryGetCoord(allLines[startYIdx], 'Y', out double startY))
                    {
                        sbLog.AppendLine("SKIP: could not read StartY from marker line.");
                        sbLog.AppendLine();
                        skipped++;
                        continue;
                    }

                    sbLog.AppendLine($"StartXY: X={startX.ToString("0.###", inv)}  Y={startY.ToString("0.###", inv)}");

                    int coordStartLine = Math.Max(startXIdx, startYIdx);
                    sbLog.AppendLine($"CoordStartLine: L{coordStartLine}");

                    var moves = BuildGeometryFromGcode_ForSetViewAll(region, regionStartIndex, allLines, startX, startY, coordStartLine);

                    sbLog.AppendLine($"Moves: {moves.Count}");

                    int beforeSegs = segsAll.Count;

                    for (int i = 0; i < moves.Count; i++)
                    {
                        var m = moves[i];

                        if (m.Type == "LINE")
                        {
                            segsAll.Add(new CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg
                            {
                                Index = segIndex++,
                                Type = "LINE",
                                X1 = m.Xs,
                                Y1 = m.Ys,
                                X2 = m.Xe,
                                Y2 = m.Ye,

                                ToolDia = setToolDia,

                                RegionName = set.Name,
                                MatrixName = matrixName,
                                RotZDeg = rotZDeg,
                                RotYDeg = rotYDeg,
                                Tx = tx,
                                Ty = ty,
                                Tz = tz,

                                RegionColor = setColor,

                                // NEW: per-set wire mode flags for viewer
                                IsClosedWire = isClosedWire,
                                ClosedWireInner = cwInner,
                                ClosedWireOuter = cwOuter
                            });
                        }
                        else if (m.Type == "ARC_CW" || m.Type == "ARC_CCW")
                        {
                            GetArc3Points2D(
                                m,
                                out double xs, out double ys,
                                out double xm, out double ym,
                                out double xe, out double ye);

                            segsAll.Add(new CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg
                            {
                                Index = segIndex++,
                                Type = (m.Type == "ARC_CW") ? "ARC3_CW" : "ARC3_CCW",
                                X1 = xs,
                                Y1 = ys,
                                Xm = xm,
                                Ym = ym,
                                X2 = xe,
                                Y2 = ye,

                                ToolDia = setToolDia,

                                RegionName = set.Name,
                                MatrixName = matrixName,
                                RotZDeg = rotZDeg,
                                RotYDeg = rotYDeg,
                                Tx = tx,
                                Ty = ty,
                                Tz = tz,

                                RegionColor = setColor,

                                // NEW: per-set wire mode flags for viewer
                                IsClosedWire = isClosedWire,
                                ClosedWireInner = cwInner,
                                ClosedWireOuter = cwOuter
                            });
                        }
                    }

                    int added = segsAll.Count - beforeSegs;
                    sbLog.AppendLine($"Segments added: {added}");

                    sbLog.AppendLine("SEGMENTS:");
                    for (int k = beforeSegs; k < segsAll.Count; k++)
                    {
                        var s = segsAll[k];
                        string td = (double.IsFinite(s.ToolDia) && s.ToolDia > 0.0) ? s.ToolDia.ToString("0.###", inv) : "NaN";
                        if (s.Type == "LINE")
                        {
                            sbLog.AppendLine($"  [TDIA={td}] LINE {s.X1.ToString("0.###", inv)} {s.Y1.ToString("0.###", inv)}   {s.X2.ToString("0.###", inv)} {s.Y2.ToString("0.###", inv)}");
                        }
                        else
                        {
                            sbLog.AppendLine($"  [TDIA={td}] {s.Type} {s.X1.ToString("0.###", inv)} {s.Y1.ToString("0.###", inv)}   {s.Xm.ToString("0.###", inv)} {s.Ym.ToString("0.###", inv)}   {s.X2.ToString("0.###", inv)} {s.Y2.ToString("0.###", inv)}");
                        }
                    }

                    sbLog.AppendLine();
                    built++;

                    // NEXT set gets next color
                    colorIndex++;
                    if (ColorList != null && ColorList.Count > 0 && colorIndex >= ColorList.Count)
                        colorIndex = 0;
                }

                if (segsAll.Count == 0)
                    throw new Exception("No MILL sets could be displayed.\n\nAfter load, a set must have saved StartX/StartY/EndX/EndY marker lines that still match the editor text.");

                // Viewer ctor fallback: if still unknown, pass NaN (viewer will use per-seg tool dia where available)
                double toolDiaForCtor = double.IsFinite(ctorFallbackToolDia) ? ctorFallbackToolDia : double.NaN;

                double toolLen = 20.0;
                double zPlane = GetZPlaneValue();

                sbLog.AppendLine("=== SUMMARY ===");
                sbLog.AppendLine($"Sets built: {built}");
                sbLog.AppendLine($"Sets skipped: {skipped}");
                sbLog.AppendLine($"Total segments: {segsAll.Count}");
                sbLog.AppendLine();
                sbLog.AppendLine($"ToolDia(ctor fallback): {(double.IsFinite(toolDiaForCtor) ? toolDiaForCtor.ToString("0.###", inv) : "NaN")}");
                sbLog.AppendLine($"ToolLen: {toolLen.ToString("0.###", inv)}");
                sbLog.AppendLine($"ZPlane: {zPlane.ToString("0.###", inv)}");

                if (Settings.Default.LogWindowShow)
                {
                    var ownerLog = Window.GetWindow(this);
                    var logWindow = new LogWindow("VIEW ALL MILLING : DEBUG", sbLog.ToString());
                    if (ownerLog != null)
                        logWindow.Owner = ownerLog;
                    logWindow.Show();
                }

                var owner = Window.GetWindow(this);
                var vw = new CNC_Improvements_gcode_solids.Utilities.MillViewWindow(toolDiaForCtor, toolLen, zPlane, segsAll);
                if (owner != null)
                    vw.Owner = owner;
                vw.RemUnusableButtons(true);
                // Your requirement: show NaN for these fields (display only).
                vw.SetDisplayParams(double.NaN, double.NaN, double.NaN);

                vw.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "View All Milling Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }









        private bool TryGetSetRegionRangeByMarkers(
            RegionSet set,
            List<string> allLines,
            out int regionStartIndex,
            out int regionEndIndex,
            out int startXIdx,
            out int startYIdx)
        {
            regionStartIndex = -1;
            regionEndIndex = -1;
            startXIdx = -1;
            startYIdx = -1;

            if (set == null || allLines == null || allLines.Count == 0)
                return false;

            // Resolve start/end markers (required for region span)
            int sx = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_STARTX_TEXT, ""));
            int sy = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_STARTY_TEXT, ""));
            int ex = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_ENDX_TEXT, ""));
            int ey = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_ENDY_TEXT, ""));

            if (sx < 0 || sy < 0 || ex < 0 || ey < 0)
                return false;

            // --------------------
            // GEOMETRY span (unchanged): Start/End XY only
            // --------------------
            int geomStart = Math.Min(sx, sy);
            int geomEnd = Math.Max(ex, ey);

            if (geomEnd < geomStart) return false;
            if (geomStart < 0 || geomEnd < 0) return false;
            if (geomStart >= allLines.Count || geomEnd >= allLines.Count) return false;

            regionStartIndex = geomStart;
            regionEndIndex = geomEnd;
            startXIdx = sx;
            startYIdx = sy;

            // --------------------
            // STORAGE span (NEW): expand to include PlaneZ if it exists
            // --------------------
            int pz = FindIndexByMarkerText(GetSnapshotOrDefault(set, KEY_PLANEZ_TEXT, ""));

            int storeStart = MinNonNeg(geomStart, pz);
            int storeEnd = MaxNonNeg(geomEnd, pz);

            if (storeStart < 0 || storeEnd < storeStart || storeEnd >= allLines.Count)
                return false;

            var regionForStore = allLines.GetRange(storeStart, storeEnd - storeStart + 1);

            // locals must be based on storeStart
            int LocalOrMinus1(int absIdx)
            {
                if (absIdx < 0) return -1;
                int local = absIdx - storeStart;
                if (local < 0 || local >= regionForStore.Count) return -1;
                return local;
            }

            int sxLocal = LocalOrMinus1(sx);
            int syLocal = LocalOrMinus1(sy);
            int exLocal = LocalOrMinus1(ex);
            int eyLocal = LocalOrMinus1(ey);
            int pzLocal = LocalOrMinus1(pz);

            // If for some reason pz couldn't be mapped, preserve key text (fallback)
            Dictionary<string, string>? snapDefaults = null;
            if (pz >= 0 && pz < allLines.Count && pzLocal < 0)
            {
                snapDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                snapDefaults[KEY_PLANEZ_TEXT] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(allLines[pz] ?? "");
            }

            // Rebuild the set using the SAME canonical builder path,
            // but with RegionLines expanded to include PlaneZ.
            BuildMillRegion.EditExisting(
                set,
                regionLines: regionForStore,
                planeZIndex: (pzLocal >= 0) ? pzLocal : (int?)null,
                startXIndex: (sxLocal >= 0) ? sxLocal : (int?)null,
                startYIndex: (syLocal >= 0) ? syLocal : (int?)null,
                endXIndex: (exLocal >= 0) ? exLocal : (int?)null,
                endYIndex: (eyLocal >= 0) ? eyLocal : (int?)null,
                snapshotDefaults: snapDefaults
            );

            ResolveAndUpdateMillStatus(set, allLines);
            return true;

        }


        private List<GeoMove> BuildGeometryFromGcode_ForSetViewAll(
    List<string> regionLines,
    int regionStartIndex,
    List<string> allLines,
    double startX,
    double startY,
    int coordStartLine)
        {
            List<GeoMove> moves = new();

            double lastX = startX;
            double lastY = startY;

            MotionMode mode = MotionMode.None;

            // modal motion before region start
            for (int i = regionStartIndex - 1; i >= 0; i--)
            {
                if (TryGetLastMotionGCode(allLines[i], out int g))
                {
                    //if (g == 0)
                    //throw new Exception($"ERROR: Region begins under modal G00 (rapid). Last motion before region is G00 at line {i + 1}.");

                    mode = (g == 1) ? MotionMode.G1
                         : (g == 2) ? MotionMode.G2
                         : MotionMode.G3;
                    break;
                }
            }

            for (int k = 0; k < regionLines.Count; k++)
            {
                string raw = regionLines[k];
                string line = raw.ToUpperInvariant().Trim();

                int physicalIndex = regionStartIndex + k;

                //if (TryGetLastMotionGCode(line, out int gHere) && gHere == 0)
                //throw new Exception($"ERROR: G00 rapid move found inside region at line {physicalIndex + 1}.");

                if (TryGetLastMotionGCode(line, out int gMotion))
                {
                    if (gMotion == 1) mode = MotionMode.G1;
                    else if (gMotion == 2) mode = MotionMode.G2;
                    else if (gMotion == 3) mode = MotionMode.G3;
                }

                // ignore lines before coords are established for THIS set
                if (physicalIndex < coordStartLine)
                    continue;

                bool hasX = TryGetCoord(line, 'X', out double newX);
                bool hasY = TryGetCoord(line, 'Y', out double newY);

                if (!hasX) newX = lastX;
                if (!hasY) newY = lastY;

                if (mode == MotionMode.None)
                {
                    lastX = newX;
                    lastY = newY;
                    continue;
                }

                if (!hasX && !hasY)
                {
                    lastX = newX;
                    lastY = newY;
                    continue;
                }

                if (mode == MotionMode.G1)
                {
                    if (lastX == newX && lastY == newY)
                    {
                        lastX = newX;
                        lastY = newY;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = "LINE",
                        Xs = lastX,
                        Ys = lastY,
                        Xe = newX,
                        Ye = newY
                    });
                }
                else if (mode == MotionMode.G2 || mode == MotionMode.G3)
                {
                    double I = 0, J = 0, R = 0;

                    bool hasI = TryGetCoord(line, 'I', out I);
                    bool hasJ = TryGetCoord(line, 'J', out J);
                    bool hasR = TryGetCoord(line, 'R', out R);

                    bool useIJ = hasI || hasJ;

                    if (!useIJ && !hasR)
                        throw new Exception($"ERROR: Arc missing I/J or R at line {physicalIndex + 1}.");

                    // RULE: if I or J exists on the line, ignore R completely
                    if (useIJ)
                        R = 0.0;

                    if (lastX == newX && lastY == newY)
                    {
                        lastX = newX;
                        lastY = newY;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = (mode == MotionMode.G2) ? "ARC_CW" : "ARC_CCW",
                        Xs = lastX,
                        Ys = lastY,
                        Xe = newX,
                        Ye = newY,
                        I = I,
                        J = J,
                        R = R
                    });
                }

                lastX = newX;
                lastY = newY;
            }

            return moves;
        }










        private void BtnViewMilling_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiUtilities.CloseAllToolWindows();
                SyncGcodeLinesFromEditor();

                var main = GetMain();
                var allLines = GetGcodeLines();

                if (main.MillSets == null || main.MillSets.Count == 0)
                    throw new Exception("No MILL sets exist. Add at least one MILL region first.");

                var set = main.SelectedMillSet;
                if (set == null)
                    throw new Exception("Select a MILL region first.");

                // ---------- Wire mode flags (from set snapshot) ----------
                // Defaults:
                //  - GuidedTool ON for old projects
                //  - ClosedWire OFF
                //  - ClosedWire default is OUTER if neither inner/outer set
                bool snapGuided = (GetSnapshotOrDefault(set, "GuidedTool", "1") == "1");
                bool snapClosedWire = (GetSnapshotOrDefault(set, "ClosedWire", "0") == "1");
                bool snapInner = (GetSnapshotOrDefault(set, "ClosedInner", "0") == "1");
                bool snapOuter = (GetSnapshotOrDefault(set, "ClosedOuter", "1") == "1");

                // ClosedWire wins if both are accidentally on
                bool isClosedWire = snapClosedWire;
                bool cwInner = false;
                bool cwOuter = false;

                if (isClosedWire)
                {
                    cwInner = snapInner;
                    cwOuter = snapOuter;

                    if (!cwInner && !cwOuter) cwOuter = true;      // consistent default
                    if (cwInner && cwOuter) { cwInner = false; cwOuter = true; } // prefer outer
                }

                // --- Build geometry for THIS set ---
                if (!TryGetSetRegionRangeByMarkers(set, allLines,
                        out int regionStartIndex, out int regionEndIndex,
                        out int startXIdx, out int startYIdx))
                    throw new Exception("Region markers could not be resolved. Apply region first.");

                var region = allLines.GetRange(regionStartIndex, regionEndIndex - regionStartIndex + 1);

                if (!TryGetCoord(allLines[startXIdx], 'X', out double startX))
                    throw new Exception("Could not read Start X marker.");
                if (!TryGetCoord(allLines[startYIdx], 'Y', out double startY))
                    throw new Exception("Could not read Start Y marker.");

                int coordStartLine = Math.Max(startXIdx, startYIdx);

                var moves = BuildGeometryFromGcode_ForSetViewAll(region, regionStartIndex, allLines, startX, startY, coordStartLine);

                if (moves.Count == 0)
                    throw new Exception("No toolpath geometry produced.");

                if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double toolDia))
                {
                    if (!double.TryParse(TxtToolDia.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out toolDia))
                        throw new Exception("Enter a valid numeric Tool Dia value.");
                }
                if (toolDia <= 0.0)
                    throw new Exception("Tool Dia must be greater than zero.");

                double toolLen = 20.0;
                if (TxtToolLen != null && !string.IsNullOrWhiteSpace(TxtToolLen.Text))
                {
                    if (!double.TryParse(TxtToolLen.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out toolLen))
                        double.TryParse(TxtToolLen.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out toolLen);
                }

                double zPlane = GetZPlaneValue();

                // --- SAME as View All: include transform + per-seg ToolDia ---
                main.TryGetTransformForRegion(
                    set.Name,
                    out double rotYDeg,
                    out double rotZDeg,
                    out double tx,
                    out double ty,
                    out double tz,
                    out string matrixName);

                // --- Build display segments (THIS is what the viewer receives) ---
                var segs = new List<CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg>();
                int segIndex = 0;

                foreach (var m in moves)
                {
                    if (m.Type == "LINE")
                    {
                        segs.Add(new CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg
                        {
                            Index = segIndex++,
                            Type = "LINE",
                            X1 = m.Xs,
                            Y1 = m.Ys,
                            X2 = m.Xe,
                            Y2 = m.Ye,

                            ToolDia = toolDia,
                            RegionName = set.Name,
                            MatrixName = matrixName,
                            RotZDeg = rotZDeg,
                            RotYDeg = rotYDeg,
                            Tx = tx,
                            Ty = ty,
                            Tz = tz,
                            RegionColor = Utilities.UiUtilities.HexBrush(Settings.Default.ProfileColor),

                            // NEW: per-set wire mode flags for viewer
                            IsClosedWire = isClosedWire,
                            ClosedWireInner = cwInner,
                            ClosedWireOuter = cwOuter
                        });
                    }
                    else if (m.Type == "ARC_CW" || m.Type == "ARC_CCW")
                    {
                        GetArc3Points2D(m,
                            out double xs, out double ys,
                            out double xm, out double ym,
                            out double xe, out double ye);

                        segs.Add(new CNC_Improvements_gcode_solids.Utilities.MillViewWindow.PathSeg
                        {
                            Index = segIndex++,
                            Type = (m.Type == "ARC_CW") ? "ARC3_CW" : "ARC3_CCW",
                            X1 = xs,
                            Y1 = ys,
                            Xm = xm,
                            Ym = ym,
                            X2 = xe,
                            Y2 = ye,

                            ToolDia = toolDia,
                            RegionName = set.Name,
                            MatrixName = matrixName,
                            RotZDeg = rotZDeg,
                            RotYDeg = rotYDeg,
                            Tx = tx,
                            Ty = ty,
                            Tz = tz,
                            RegionColor = Utilities.UiUtilities.HexBrush(Settings.Default.ProfileColor),

                            // NEW: per-set wire mode flags for viewer
                            IsClosedWire = isClosedWire,
                            ClosedWireInner = cwInner,
                            ClosedWireOuter = cwOuter
                        });
                    }
                }

                if (segs.Count == 0)
                    throw new Exception("Nothing to render in viewer.");

                // --- Log: show EXACT viewer inputs (same style as View All) ---
                var inv = CultureInfo.InvariantCulture;
                var sbLog = new System.Text.StringBuilder();

                sbLog.AppendLine("=== MILL VIEW : ACTUAL INPUT ===");
                sbLog.AppendLine($"Region: {set.Name}");
                sbLog.AppendLine($"Tool Dia (viewer ctor): {toolDia.ToString("0.###", inv)}");
                sbLog.AppendLine($"Tool Len (viewer ctor): {toolLen.ToString("0.###", inv)}");
                sbLog.AppendLine($"Z Plane  (viewer ctor): {zPlane.ToString("0.###", inv)}");
                sbLog.AppendLine($"@TRANS Matrix: {matrixName}");
                sbLog.AppendLine($"@TRANS RotZ(CW+): {rotZDeg.ToString("0.###", inv)}");
                sbLog.AppendLine($"@TRANS RotY: {rotYDeg.ToString("0.###", inv)}");
                sbLog.AppendLine($"@TRANS Tx={tx.ToString("0.###", inv)}  Ty={ty.ToString("0.###", inv)}  Tz={tz.ToString("0.###", inv)}");
                sbLog.AppendLine($"WireMode: {(isClosedWire ? "ClosedWire" : "GuidedTool")}");
                if (isClosedWire)
                    sbLog.AppendLine($"ClosedWire: {(cwInner ? "Pocket/Inner" : "Outer")}");
                sbLog.AppendLine($"Segments (viewer list): {segs.Count}");
                sbLog.AppendLine();

                sbLog.AppendLine("SEGMENTS:");
                for (int i = 0; i < segs.Count; i++)
                {
                    var s = segs[i];
                    string td = (double.IsFinite(s.ToolDia) && s.ToolDia > 0.0) ? s.ToolDia.ToString("0.###", inv) : "NaN";

                    if (s.Type == "LINE")
                    {
                        sbLog.AppendLine(
                            $"  [TDIA={td}] LINE {s.X1.ToString("0.###", inv)} {s.Y1.ToString("0.###", inv)}   " +
                            $"{s.X2.ToString("0.###", inv)} {s.Y2.ToString("0.###", inv)}");
                    }
                    else
                    {
                        sbLog.AppendLine(
                            $"  [TDIA={td}] {s.Type} {s.X1.ToString("0.###", inv)} {s.Y1.ToString("0.###", inv)}   " +
                            $"{s.Xm.ToString("0.###", inv)} {s.Ym.ToString("0.###", inv)}   " +
                            $"{s.X2.ToString("0.###", inv)} {s.Y2.ToString("0.###", inv)}");
                    }
                }

                string logText = sbLog.ToString();

                if (Settings.Default.LogWindowShow)
                {
                    var owner = Window.GetWindow(this);
                    var logWin = new CNC_Improvements_gcode_solids.Utilities.LogWindow("Mill View : Actual Viewer Input", logText);
                    if (owner != null) logWin.Owner = owner;
                    logWin.Show();
                }

                // --- Show the actual viewer ---
                var owner2 = Window.GetWindow(this);
                var vw = new CNC_Improvements_gcode_solids.Utilities.MillViewWindow(toolDia, toolLen, zPlane, segs);
                if (owner2 != null)
                    vw.Owner = owner2;

                vw.SetDisplayParams(toolDia, toolLen, zPlane);
                vw.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "View Milling Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }









        // ============================================================
        // Optional: marker-based resolved range (kept, but now correct)
        //  - 0-based
        //  - null when unset
        // ============================================================
        private void UpdateResolvedRangeInSelectedSet()
        {
            if (_isApplyingMillSet) return;

            var set = GetSelectedMillSetSafe();
            if (set == null)
                return;

            int start = MinNonNeg(startXIndex, startYIndex);
            int end = MaxNonNeg(endXIndex, endYIndex);

            if (start < 0 || end < 0)
            {
                set.ResolvedStartLine = null;
                set.ResolvedEndLine = null;
                return;
            }

            set.ResolvedStartLine = start; // 0-based
            set.ResolvedEndLine = end;     // 0-based
        }

        // Min/Max across MANY indices, ignoring negatives (-1 = "unset")
        private static int MinNonNeg(params int[] values)
        {
            int min = int.MaxValue;
            bool any = false;

            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    int v = values[i];
                    if (v >= 0)
                    {
                        any = true;
                        if (v < min) min = v;
                    }
                }
            }

            return any ? min : -1;
        }

        private static int MaxNonNeg(params int[] values)
        {
            int max = int.MinValue;
            bool any = false;

            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    int v = values[i];
                    if (v >= 0)
                    {
                        any = true;
                        if (v > max) max = v;
                    }
                }
            }

            return any ? max : -1;
        }



        private bool TryGetToolDiaForSet(RegionSet set, out double toolDia)
        {
            toolDia = double.NaN;

            if (set == null)
                return false;

            string s = GetSnapshotOrDefault(set, KEY_TOOL_DIA, "");
            if (string.IsNullOrWhiteSpace(s))
                return false;

            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out toolDia))
            {
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out toolDia))
                    return false;
            }

            return double.IsFinite(toolDia) && toolDia > 0.0;
        }



    }
}
