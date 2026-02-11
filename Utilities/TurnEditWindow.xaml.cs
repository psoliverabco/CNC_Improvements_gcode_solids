// File: Utilities/TurnEditWindow.xaml.cs  edits and outputs a region
using CNC_Improvements_gcode_solids.Properties;
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNC_Improvements_gcode_solids.Utilities.TurnEditHelpers;
using System.Windows.Threading;
using CNC_Improvements_gcode_solids.SetManagement.Builders;



namespace CNC_Improvements_gcode_solids.Utilities
{
    public partial class TurnEditWindow : Window
    {
        // Zoom/pan transforms applied to the drawing canvas only
        private readonly ScaleTransform _scale = new ScaleTransform(1.0, 1.0);
        private readonly TranslateTransform _translate = new TranslateTransform();
        private readonly TransformGroup _transformGroup = new TransformGroup();

        // -------------------- RENDERER (moved render/map/pick-debug here) --------------------
        private readonly TurnEditRender _renderer;

        // -------- Fillet seed radius (set by long-click on an ARC) --------
        private double? _filletSeedRadius = null;

        // -------- Select-mode long press (to store seed radius) --------
        private const int SELECT_LONG_PRESS_MS = 550;
        private const double SELECT_LONG_PRESS_MOVE_TOL_PX = 6.0;

        private bool _selectDownPending = false;
        private bool _selectLongPressFired = false;
        private Point _selectDownCanvas;
        private DispatcherTimer? _selectLongPressTimer;

        // -------- Status history (selection only) --------
        private const int STATUS_HISTORY_MAX_LINES = 2;
        private readonly Queue<string> _statusHistory = new Queue<string>(STATUS_HISTORY_MAX_LINES);

        // Controls whether the NEXT render is allowed to reset view (Fit only)
        private bool _forceFitOnNextRender = false;


        



        // Adapter views so renderer does NOT depend on your EditSeg types directly.
        private sealed class LineView : TurnEditRender.IEditSegView
        {
            public int Index { get; init; }
            public TurnEditRender.SegKind Kind => TurnEditRender.SegKind.Line;

            // World coords (X,Z) => Point(X, Z)
            public Point A { get; init; }
            public Point B { get; init; }

            // Style passthrough (important for base style revert + selection highlighting)
            public Brush? Stroke { get; init; }
            public double? Thickness { get; init; }
        }

        private sealed class ArcView : TurnEditRender.IEditArcSegView
        {
            public int Index { get; init; }
            public TurnEditRender.SegKind Kind => TurnEditRender.SegKind.Arc;

            // World coords (X,Z) => Point(X, Z)
            public Point A { get; init; }
            public Point B { get; init; }
            public Point M { get; init; }
            public Point C { get; init; }

            // Style passthrough (important)
            public Brush? Stroke { get; init; }
            public double? Thickness { get; init; }
        }

        private IReadOnlyList<TurnEditRender.IEditSegView> BuildRenderViews(IReadOnlyList<EditSeg> segs)
        {
            // IMPORTANT:
            // - segIndex used everywhere in TurnEditWindow is list index.
            // - renderer expects Index = segIndex.
            var views = new List<TurnEditRender.IEditSegView>(segs.Count);

            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];

                if (s is EditLineSeg ln)
                {
                    views.Add(new LineView
                    {
                        Index = i,
                        A = ln.A,
                        B = ln.B,
                        Stroke = s.Stroke,
                        Thickness = s.Thickness
                    });
                }
                else if (s is EditArcSeg a)
                {
                    // Renderer MUST NOT recompute center.
                    // It uses A/M/B + CENTER C and MID containment to choose sweep.
                    views.Add(new ArcView
                    {
                        Index = i,
                        A = a.A,
                        B = a.B,
                        M = a.M,
                        C = a.C,
                        Stroke = s.Stroke,
                        Thickness = s.Thickness
                    });
                }
                else
                {
                    // Unknown seg type -> ignore (render nothing)
                }
            }

            return views;
        }

        // ---- Pan mode drag state ----
        private bool _isPanning = false;
        private Point _panStartCanvas;
        private double _panStartTranslateX;
        private double _panStartTranslateY;

        // ---- RegionSet snapshot dictionary keys (match MainWindow BtnAddTurn_Click + your project) ----
        private const string KEY_ToolUsage = "__ToolUsage";
        private const string KEY_Quadrant = "__Quadrant";
        private const string KEY_StartXLine = "__StartXLine";
        private const string KEY_StartZLine = "__StartZLine";
        private const string KEY_EndXLine = "__EndXLine";
        private const string KEY_EndZLine = "__EndZLine";
        private const string KEY_TxtZExt = "TxtZExt";
        private const string KEY_NRad = "NRad";

        // Single source of truth tolerance for ALL trim/chaining decisions.
        // Editor should be stricter than FreeCAD sew tolerance.

        private static double SewTol
        {
            get
            {
                double sew = Settings.Default.SewTol;
                if (!TurnEditMath.IsFinite(sew) || sew <= 0.0) sew = 0.001;
                double t = sew;
                return (t < 1e-12) ? 1e-12 : t;
            }
        }

        private static double EditTol
        {
            get
            {
                double sew = Settings.Default.SewTol;
                if (!TurnEditMath.IsFinite(sew) || sew <= 0.0) sew = 0.001;
                double t = sew * 0.5;
                return (t < 1e-12) ? 1e-12 : t;
            }
        }




       


        // Render tag so we can clear geometry only
        private const string TAG_GEOM = "GEOM";

        private List<EditSeg> _editSegs = new List<EditSeg>();

        // Removed segments are kept here until "Replace/Insert Deleted" re-inserts them
        private readonly List<EditSeg> _removedSegs = new List<EditSeg>();

        // -------------------- Selection (max 2) + pick-circle UI --------------------
        private const string TAG_PICK = "PICK";

        // Debug: draw the flattened pick polyline on screen.
        // NOTE: set this to 0.0 to remove poly display.
        private const double PICK_DEBUG_OPACITY = 0.0;

        // --- Pick debug points (sampling from RAW entities) ---
        private const string TAG_PICK_DEBUG = "PICKDBG";
        private const double PICK_DEBUG_POINT_DIAM_PX = 6.0;

        // Pick-point generation rules (world units ~ mm)
        private const double PICK_DEBUG_SPACING_MM = 1.0;
        private const int PICK_DEBUG_MIN_POINTS = 10;
        private const int PICK_DEBUG_MAX_POINTS = 200;

        private const double PICK_DIAM_PX = 16.0;      // screen pixels (later: setting)
        private const double PICK_CROSS_LEN_PX = 24.0; // screen pixels
        private const double PICK_STROKE_PX = 2.0;     // screen pixels

        private sealed class RemovedDisplayItem
        {
            public int Index;
            public EditSeg Seg = null!;
            public string Text = "";
            public override string ToString() => Text;
        }

        private sealed class PickInfo
        {
            public int SegIndex;
            public EditSeg Seg = null!;
            public bool PickStart;         // true => Start, false => End
            public Point PickEndWorld;     // world coords (radius, Z)
            public Point ClickCanvas;      // canvas coords (logical, pre-transform)
        }

        // FIFO: A then B. 3rd select drops A, shifts B->A, new->B
        private PickInfo? _pickA = null;
        private PickInfo? _pickB = null;

        // Cached mapping from last RenderEditGeometry (world -> screen) for failure '*'
      

        // Viewport vs oversized canvas padding (canvas is 3x viewport)
        private double _padX = 0.0;
        private double _padY = 0.0;

        // Arc centre choice:
        // We IGNORE cx/cz from the script and recompute centre from (A,M,B).
        // This removes the "two possible centres" problem permanently.
        private const bool ARC_CENTER_FROM_3PTS = true;

        // ---------- MainWindow access ----------
        private MainWindow GetMain()
        {
            return Application.Current.MainWindow as MainWindow
                   ?? throw new InvalidOperationException("MainWindow not available.");
        }

        private List<string> GetGcodeLines() => GetMain().GcodeLines;
        private RichTextBox GetGcodeEditor() => GetMain().GcodeEditor;

        public TurnEditWindow()
        {
            InitializeComponent();

            _transformGroup.Children.Add(_scale);
            _transformGroup.Children.Add(_translate);

            EditCanvas.RenderTransform = _transformGroup;
            EditCanvas.RenderTransformOrigin = new Point(0, 0);

            // Renderer options wired to your existing constants/tags
            var ropt = new TurnEditRender.Options
            {
                TagGeom = TAG_GEOM,
                TagPick = TAG_PICK,
                TagPickDebug = TAG_PICK_DEBUG,

                // Renderer will set canvas size to 3x viewport during fit
                SetCanvasTo3xViewport = true,

                // Match your existing sampling rule
                ArcSampleCount = 96,

                // Pick debug rules (must remain zoom-invariant)
                PickDebugOpacity = PICK_DEBUG_OPACITY,
                PickDebugPointDiamPx = PICK_DEBUG_POINT_DIAM_PX,
                PickDebugSpacingWorld = PICK_DEBUG_SPACING_MM,
                PickDebugMinPoints = PICK_DEBUG_MIN_POINTS,
                PickDebugMaxPoints = PICK_DEBUG_MAX_POINTS



        };

            // Create renderer (owns map + draw + pick-debug)
            _renderer = new TurnEditRender(
                EditCanvas,
                ViewportHost,
                _scale,
                _translate,
                _transformGroup,
                _padX,
                _padY,
                ropt
            );

            ViewportHost.SizeChanged += (_, __) =>
            {
                // pad is still driven by viewport size (your existing convention)
                double vw = ViewportHost.ActualWidth;
                double vh = ViewportHost.ActualHeight;

                if (vw > 10 && vh > 10)
                {
                    _padX = vw;
                    _padY = vh;

                    // Tell renderer the new padding. (Renderer owns map + fit.)
                    _renderer.UpdatePad(_padX, _padY);
                }

                // Re-render last geometry on resize
                if (_editSegs != null && _editSegs.Count > 0)
                    RenderEditGeometry(_editSegs);
                _renderer.DrawBackgroundGridWorld(20.0);
            };

            // Make Pan/Select behave like radio buttons
            if (BtnModePan != null)
            {
                BtnModePan.Checked += (_, __) =>
                {
                    if (BtnModeSelect != null) BtnModeSelect.IsChecked = false;
                    UpdateUiState("Mode: Pan");
                };

                BtnModePan.Unchecked += (_, __) =>
                {
                    // If nothing is selected, default back to Select
                    if (BtnModeSelect != null && BtnModeSelect.IsChecked != true)
                        BtnModeSelect.IsChecked = true;
                };
            }

            if (BtnModeSelect != null)
            {
                BtnModeSelect.Checked += (_, __) =>
                {
                    if (BtnModePan != null) BtnModePan.IsChecked = false;
                    UpdateUiState("Mode: Select");
                };

                BtnModeSelect.Unchecked += (_, __) =>
                {
                    // If nothing is selected, default back to Select
                    if (BtnModePan != null && BtnModePan.IsChecked != true)
                        BtnModeSelect.IsChecked = true;
                };
            }



            Brush fg = GetGraphicTextBrush();

            TxtHint.Foreground = fg;

            TxtSelected.Foreground = fg;    



            UpdateUiState("Mode: Select");
        }

        private static double Norm2Pi(double a) => TurnEditMath.Norm2Pi(a);
        private static double DeltaCCW(double aFrom, double aTo) => TurnEditMath.DeltaCCW(aFrom, aTo);
        private static double DeltaCW(double aFrom, double aTo) => TurnEditMath.DeltaCW(aFrom, aTo);
        private static double Dist(Point a, Point b) => TurnEditMath.Dist(a, b);

        private void ClearPreviewOnly()
        {
            _renderer.ClearPreviewOnly();
        }

        private void UpdateUiState(string status)
        {
            // Non-selection status: overwrite both blocks, include zoom hint
            SetStatusBlocksSingleLine(status ?? "");
        }


        private void RenderEditGeometry(List<EditSeg> segs)
        {
            if (segs == null || segs.Count == 0)
            {
                ClearGeometryOnly();
                return;
            }

            // ---- SNAPSHOT CURRENT VIEW ----
            double savedScaleX = _scale.ScaleX;
            double savedScaleY = _scale.ScaleY;
            double savedTransX = _translate.X;
            double savedTransY = _translate.Y;

            // Keep renderer in sync with current pad
            _renderer.UpdatePad(_padX, _padY);

            // Build render views
            var views = BuildRenderViews(segs);

            // ---- RENDER (renderer may internally "fit") ----
            _renderer.RenderEditGeometry(views);

            // ---- RESTORE VIEW UNLESS THIS WAS A FIT ----
            if (!_forceFitOnNextRender)
            {
                _scale.ScaleX = savedScaleX;
                _scale.ScaleY = savedScaleY;
                _translate.X = savedTransX;
                _translate.Y = savedTransY;
            }

            // One-shot flag: always clear after render
            _forceFitOnNextRender = false;
        }


        private void ClearGeometryOnly()
        {
            _renderer.ClearGeometryOnly();
            
        }

        private Point WorldToScreen(Point wp)
        {
            return _renderer.WorldToScreen(wp);
        }

        private Point ScreenToWorld(Point sp)
        {
            return _renderer.ScreenToWorld(sp);
        }

        private void DrawFailureStarAtWorldPoint(Point worldPt)
        {
            _renderer.DrawFailureStarAtWorldPoint(worldPt);
        }

        private void UpdatePickDebugHitFromSeg(int segIndex, Point clickCanvas)
        {
            _renderer.UpdatePickDebugHitFromSeg(segIndex, clickCanvas);
        }

        private void TurnEditWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadTurnSetsIntoList();
                LstRegions.SelectionChanged -= LstRegions_SelectionChanged;
                LstRegions.SelectionChanged += LstRegions_SelectionChanged;
                LstRegions_SelectionChanged(LstRegions, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadTurnSetsIntoList()
        {
            LstRegions.Items.Clear();

            var main = GetMain();
            ObservableCollection<RegionSet> sets = main.TurnSets;

            if (sets == null || sets.Count == 0)
            {
                TxtFooter.Text = "No TURN sets exist.";
                return;
            }

            for (int i = 0; i < sets.Count; i++)
            {
                var s = sets[i];
                if (s == null) continue;

                var item = new ListBoxItem
                {
                    Content = s.Name ?? "(unnamed)",
                    Tag = s
                };

                LstRegions.Items.Add(item);
            }

            TxtFooter.Text = $"Loaded {LstRegions.Items.Count} TURN sets.";
        }

        private void LstRegions_SelectionChanged(object? sender, SelectionChangedEventArgs? e)
        {
            try
            {
                var selected = GetSelectedTurnSets();
                BtnSave.IsEnabled = (selected.Count > 0);

                if (selected.Count == 0)
                {
                    TxtSelected.Text = "None";
                    TxtFooter.Text = "(ready)";
                    return;
                }

                var sb = new StringBuilder();
                for (int i = 0; i < selected.Count; i++)
                {
                    if (i > 0) sb.AppendLine();
                    sb.Append(selected[i].Name ?? "(unnamed)");
                }

                TxtSelected.Text = sb.ToString();
                TxtFooter.Text = $"Selected {selected.Count} TURN set(s).";
            }
            catch
            {
                // silent
            }
        }

        private List<RegionSet> GetSelectedTurnSets()
        {
            // MUST return in list order (top->bottom)
            var list = new List<RegionSet>();

            for (int i = 0; i < LstRegions.Items.Count; i++)
            {
                if (LstRegions.Items[i] is ListBoxItem lbi && lbi.IsSelected && lbi.Tag is RegionSet rs)
                {
                    list.Add(rs);
                }
            }

            return list;
        }

        private void SyncGcodeLinesFromEditor()
        {
            var lines = GetGcodeLines();
            lines.Clear();

            var rtb = GetGcodeEditor();
            if (rtb == null)
                throw new Exception("Internal error: G-code editor not found in MainWindow.");

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

        //==========================================================
        private void BtnSubmitRegions_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SyncGcodeLinesFromEditor();

                var selectedSets = GetSelectedTurnSets();
                if (selectedSets.Count == 0)
                    throw new Exception("Select one or more TURN sets first.");

                var allLines = GetGcodeLines();

                string scriptText = TurnEditSegsLoad.BuildSelectedTurnSetsDisplayScript(
                    GetMain(),
                    selectedSets,
                    allLines,
                    KEY_ToolUsage,
                    KEY_Quadrant,
                    KEY_StartXLine,
                    KEY_StartZLine,
                    KEY_EndXLine,
                    KEY_EndZLine,
                    KEY_TxtZExt,
                    KEY_NRad
                );

                var dtos = TurnEditSegsLoad.ParseSegDtosFromScript(
                    scriptText,
                    Settings.Default.ClosingColor,
                    ARC_CENTER_FROM_3PTS
                );

                _editSegs = ConvertSegDtosToEditSegs(dtos);

                // ---- FORCE FIT AFTER LOAD ----
                _forceFitOnNextRender = true;
                _scale.ScaleX = 1.0;
                _scale.ScaleY = 1.0;
                _translate.X = 0.0;
                _translate.Y = 0.0;

                RenderEditGeometry(_editSegs);
                CenterCurrentGeometryInViewport(_editSegs);
                _renderer.DrawBackgroundGridWorld();

                if (Settings.Default.LogWindowShow)
                {
                    var ownerW = this.Owner ?? Application.Current.MainWindow;
                    var logWindow = new CNC_Improvements_gcode_solids.Utilities.LogWindow("Turn Editor: Input Shape Script", scriptText);
                    if (ownerW != null) logWindow.Owner = ownerW;
                    logWindow.Show();
                    logWindow.Activate();
                }

                UpdateUiState($"Built + rendered {_editSegs.Count} segment(s) from {selectedSets.Count} set(s).");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        // ============================================================
        // Fit centering helper (Fit must be centered X/Y)
        // ============================================================
        private void CenterCurrentGeometryInViewport(IReadOnlyList<EditSeg> segs)
        {
            if (segs == null || segs.Count == 0) return;
            if (!_renderer.MapValid) return;

            // World bounds from segs
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;

            for (int i = 0; i < segs.Count; i++)
                segs[i].ExpandWorldBounds(ref minX, ref maxX, ref minZ, ref maxZ);

            if (!TurnEditMath.IsFinite(minX) || !TurnEditMath.IsFinite(maxX) ||
                !TurnEditMath.IsFinite(minZ) || !TurnEditMath.IsFinite(maxZ))
                return;

            // Visible rect in CANVAS coords (viewport-sized rect, not 3x canvas)
            Rect vis = GetVisibleCanvasRectInCanvasCoords();
            if (vis.Width <= 1 || vis.Height <= 1) return;

            // Compute screen/canvas bounds of world AABB under CURRENT transforms
            Point s1 = WorldToScreen(new Point(minX, minZ));
            Point s2 = WorldToScreen(new Point(minX, maxZ));
            Point s3 = WorldToScreen(new Point(maxX, minZ));
            Point s4 = WorldToScreen(new Point(maxX, maxZ));

            double sMinX = Math.Min(Math.Min(s1.X, s2.X), Math.Min(s3.X, s4.X));
            double sMaxX = Math.Max(Math.Max(s1.X, s2.X), Math.Max(s3.X, s4.X));
            double sMinY = Math.Min(Math.Min(s1.Y, s2.Y), Math.Min(s3.Y, s4.Y));
            double sMaxY = Math.Max(Math.Max(s1.Y, s2.Y), Math.Max(s3.Y, s4.Y));

            Point geomCenter = new Point((sMinX + sMaxX) * 0.5, (sMinY + sMaxY) * 0.5);
            Point visCenter = new Point(vis.Left + vis.Width * 0.5, vis.Top + vis.Height * 0.5);

            // Because transform order is Scale then Translate, a translate delta in canvas coords
            // shifts the rendered geometry by that same amount in canvas coords.
            Vector d = visCenter - geomCenter;

            _translate.X += d.X;
            _translate.Y += d.Y;
        }


        private List<EditSeg> ConvertSegDtosToEditSegs(List<TurnEditSegsLoad.SegDto> dtos)
        {
            var segs = new List<EditSeg>();
            if (dtos == null || dtos.Count == 0) return segs;

            for (int i = 0; i < dtos.Count; i++)
            {
                var d = dtos[i];

                if (d.Kind == TurnEditSegsLoad.SegKind.Line)
                {
                    segs.Add(new EditLineSeg
                    {
                        Stroke = d.Stroke ?? Brushes.White,
                        Thickness = d.Thickness,
                        A = d.A,
                        B = d.B
                    });
                }
                else
                {
                    segs.Add(new EditArcSeg
                    {
                        Stroke = d.Stroke ?? Brushes.White,
                        Thickness = d.Thickness,
                        A = d.A,
                        M = d.M,
                        B = d.B,
                        C = d.C,
                        CCW = d.CCW
                    });
                }
            }

            return segs;
        }

        // ============================================================
        // Save: chain/snap -> closed loop -> output region G-code -> append + create set
        // ============================================================
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_editSegs == null || _editSegs.Count == 0)
                    throw new Exception("Nothing to save. Click Submit and build geometry first.");

                double sewTol = SewTol;
                double editTol = EditTol;

                int beforeCount = _editSegs.Count;

                if (!TryChainAndSnap(_editSegs, editTol,
                        out List<EditSeg> ordered,
                        out double maxGap,
                        out bool isClosed,
                        out Point failWorld,
                        out string failReason))
                {
                    ShowChainDiagnosticsIfEnabled(
                        "Turn Editor: Chain Diagnostics (Save failed)",
                        failReason,
                        beforeCount,
                        ordered?.Count ?? 0,
                        sewTol,
                        editTol,
                        maxGap,
                        false);

                    UpdateUiState("Shape not closed.");
                    DrawFailureStarAtWorldPoint(failWorld);
                    MessageBox.Show("Shape not closed.\n\n" + failReason, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ShowChainDiagnosticsIfEnabled(
                    "Turn Editor: Chain Diagnostics (Save)",
                    "Save",
                    beforeCount,
                    ordered.Count,
                    sewTol,
                    editTol,
                    maxGap,
                    isClosed);

                // Replace the editor geometry with the deterministic chained result.
                _editSegs = ordered;

                // Export (NO tool comp). Use IK to remove R ambiguity.
                var outSegs = ConvertEditSegsToOutputSegs(ordered);
                List<string> newRegionLines = TurnEditOutputGcode.BuildRegionGcodeFromClosedLoop_IK(outSegs);

                // Name
                string? proposed = PromptForRegionName("New TURN Edit Region Name", "Turn Edit Region");
                if (string.IsNullOrWhiteSpace(proposed))
                {
                    UpdateUiState("Save cancelled.");
                    return;
                }

                string newName = MakeUniqueTurnSetName(proposed.Trim());

                // Tag ST/END for editor output only
                var marked = TurnEditOutputGcode.AppendStartEndMarkers(newRegionLines, newName);

                // Append to editor
                SyncGcodeLinesFromEditor();
                AppendRegionBlockToEditor(marked);
                SyncGcodeLinesFromEditor();

                // Create and add new set using the SAME SetManagement builders + normalizers as MainWindow.
                // We store the region as anchored lines (#uid,n#...) and snapshot marker keys point at anchored lines.
                var main = GetMain();

                // Compute TURN marker indices (0-based into regionLines)
                var (firstX, firstZ, lastX, lastZ) = TurnEditOutputGcode.ComputeTurnMarkerIndices(newRegionLines);

                if (firstX < 0 || firstZ < 0 || lastX < 0 || lastZ < 0)
                    throw new Exception("Cannot save region: X/Z markers not found.");

                var newSet = BuildTurnRegion.Create(
                    regionName: newName,
                    regionLines: newRegionLines,
                    startXIndex: firstX,
                    startZIndex: firstZ,
                    endXIndex: lastX,
                    endZIndex: lastZ,
                    toolUsage: "OFF",
                    quadrant: "3",
                    txtZExt: "-100",
                    nRad: "0.8",
                    snapshotDefaults: null
                );

                // TurnEditWindow wants these defaults for the NEW set.
                newSet.ShowInViewAll = false;
                newSet.ExportEnabled = true;

                main.TurnSets.Add(newSet);
                try { main.SelectedTurnSet = newSet; } catch { }

                ShowLogWindowIfEnabled("Turn Editor: Saved Region (Generated)", marked, newName);

                // Re-render with the stored chained geometry
                RenderEditGeometry(_editSegs);
                ApplySelectionVisuals();
                DrawPickMarkers();

                UpdateUiState($"Saved new TURN set '{newName}' ({marked.Count} line(s)).");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        // ============================================================
        // Save: Output conversion helpers
        // ============================================================
        private static List<TurnEditOutputGcode.Seg> ConvertEditSegsToOutputSegs(IReadOnlyList<EditSeg> ordered)
        {
            var list = new List<TurnEditOutputGcode.Seg>(ordered?.Count ?? 0);
            if (ordered == null) return list;

            for (int i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];

                if (s is EditLineSeg ln)
                {
                    list.Add(new TurnEditOutputGcode.Seg
                    {
                        Kind = TurnEditOutputGcode.SegKind.Line,
                        A = ln.A,
                        B = ln.B
                    });
                }
                else if (s is EditArcSeg a)
                {
                    list.Add(new TurnEditOutputGcode.Seg
                    {
                        Kind = TurnEditOutputGcode.SegKind.Arc,
                        A = a.A,
                        B = a.B,
                        M = a.M,
                        C = a.C,
                        CCW = a.CCW
                    });
                }
                else
                {
                    throw new Exception("Unknown segment type during output conversion.");
                }
            }

            return list;
        }





        private void ShowChainDiagnosticsIfEnabled(string title, string context, int segCountBefore, int segCountAfter, double sewTol, double editTol, double maxEndpointGap, bool closed)
        {
            if (!Settings.Default.LogWindowShow) return;

            var sb = new StringBuilder();
            sb.AppendLine("=== TURN EDIT: DIAGNOSTICS ===");
            sb.AppendLine();
            sb.AppendLine("Context: " + (context ?? ""));
            sb.AppendLine($"SewTol  = {sewTol.ToString("0.########", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"EditTol = {editTol.ToString("0.########", CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine($"Segments before = {segCountBefore}");
            sb.AppendLine($"Segments after  = {segCountAfter}");
            sb.AppendLine($"Max endpoint gap after chaining = {maxEndpointGap.ToString("0.########", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Closed (editor) = {(closed ? "YES" : "NO")}");

            // Dump output-seg diagnostics (what export will use)
            try
            {
                sb.AppendLine();
                sb.AppendLine(TurnEditOutputGcode.BuildSegDiagnosticsText(ConvertEditSegsToOutputSegs(_editSegs)));
            }
            catch (Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine("=== OUTPUT SEGS (diagnostics failed) ===");
                sb.AppendLine(ex.Message);
            }



            var ownerW = this.Owner ?? Application.Current.MainWindow;
            var logWindow = new CNC_Improvements_gcode_solids.Utilities.LogWindow(title, sb.ToString());
            if (ownerW != null) logWindow.Owner = ownerW;
            logWindow.Show();
            logWindow.Activate();
        }


        // ============================================================
        // UI: Fit / Cancel / zoom
        // ============================================================
        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            // Explicitly allow renderer to reset view ONCE
            _forceFitOnNextRender = true;

            _scale.ScaleX = 1.0;
            _scale.ScaleY = 1.0;
            _translate.X = 0.0;
            _translate.Y = 0.0;

            if (_editSegs != null && _editSegs.Count > 0)
            {
                // 1) Let renderer do its fit (scale + initial translate)
                RenderEditGeometry(_editSegs);

                // 2) Then force TRUE centering inside the current visible viewport (X + Y)
                CenterCurrentGeometryInView(_editSegs);
            }

            UpdateUiState("Fit");

            _renderer.DrawBackgroundGridWorld();
        }


        // Computes conservative world bounds for the current edit geometry.
        private static Rect GetWorldBounds(IReadOnlyList<EditSeg> segs)
        {
            if (segs == null || segs.Count == 0)
                return Rect.Empty;

            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minZ = double.PositiveInfinity;
            double maxZ = double.NegativeInfinity;

            for (int i = 0; i < segs.Count; i++)
            {
                segs[i].ExpandWorldBounds(ref minX, ref maxX, ref minZ, ref maxZ);
            }

            if (!TurnEditMath.IsFinite(minX) || !TurnEditMath.IsFinite(maxX) ||
                !TurnEditMath.IsFinite(minZ) || !TurnEditMath.IsFinite(maxZ))
                return Rect.Empty;

            if (maxX < minX || maxZ < minZ)
                return Rect.Empty;

            return new Rect(new Point(minX, minZ), new Point(maxX, maxZ));
        }

        // After the renderer's Fit, nudge translate so geometry is centered in the visible viewport (both X and Y).
        private void CenterCurrentGeometryInView(IReadOnlyList<EditSeg> segs)
        {
            if (segs == null || segs.Count == 0) return;
            if (!_renderer.MapValid) return;

            Rect wb = GetWorldBounds(segs);
            if (wb.IsEmpty) return;

            // Geometry bounds in CANVAS coords (using current map/transform after renderer fit)
            Point p1 = WorldToScreen(new Point(wb.Left, wb.Top));
            Point p2 = WorldToScreen(new Point(wb.Right, wb.Bottom));

            double gLeft = Math.Min(p1.X, p2.X);
            double gRight = Math.Max(p1.X, p2.X);
            double gTop = Math.Min(p1.Y, p2.Y);
            double gBottom = Math.Max(p1.Y, p2.Y);

            Point geomCenterCanvas = new Point((gLeft + gRight) * 0.5, (gTop + gBottom) * 0.5);

            // Visible viewport rect expressed in CANVAS coords (already accounts for pan/zoom)
            Rect vis = _renderer.GetVisibleCanvasRectInCanvasCoords();
            if (vis.Width <= 1 || vis.Height <= 1) return;

            Point viewCenterCanvas = new Point(vis.Left + vis.Width * 0.5, vis.Top + vis.Height * 0.5);

            // Translate is AFTER scale in the transform chain, so this delta is in canvas/screen units.
            Vector delta = viewCenterCanvas - geomCenterCanvas;

            if (TurnEditMath.IsFinite(delta.X) && TurnEditMath.IsFinite(delta.Y))
            {
                _translate.X += delta.X;
                _translate.Y += delta.Y;
            }
        }





        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_editSegs == null || _editSegs.Count == 0)
                {
                    UpdateUiState("Remove: no geometry.");
                    return;
                }

                if (_pickA == null)
                {
                    UpdateUiState("Remove: nothing selected.");
                    return;
                }

                // Rule:
                // - 1 selected -> remove that (A)
                // - 2 selected -> remove first (A), then B becomes A
                int removeIdx = _pickA.SegIndex;
                if (removeIdx < 0 || removeIdx >= _editSegs.Count)
                {
                    UpdateUiState("Remove: invalid selection.");
                    return;
                }

                // Capture removed item
                _removedSegs.Add(CloneSeg(_editSegs[removeIdx]));

                // Remove from geometry list
                _editSegs.RemoveAt(removeIdx);

                // Fix selection indices after removal (shift down)
                if (_pickA != null)
                {
                    if (_pickA.SegIndex == removeIdx) _pickA = null;
                    else if (_pickA.SegIndex > removeIdx) _pickA.SegIndex--;
                }

                if (_pickB != null)
                {
                    if (_pickB.SegIndex == removeIdx) _pickB = null;
                    else if (_pickB.SegIndex > removeIdx) _pickB.SegIndex--;
                }

                // B becomes A
                if (_pickA == null && _pickB != null)
                {
                    _pickA = _pickB;
                    _pickB = null;
                }

                // Re-render and re-apply selection visuals
                RenderEditGeometry(_editSegs);
                ApplySelectionVisuals();
                DrawPickMarkers();

                UpdateUiState($"Removed seg {removeIdx + 1}. Deleted pool: {_removedSegs.Count}. Remaining: {_editSegs.Count}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string DescribeSeg(EditSeg s)
        {
            var inv = CultureInfo.InvariantCulture;

            if (s is EditLineSeg ln)
            {
                return $"LINE A({ln.A.X.ToString("0.###", inv)},{ln.A.Y.ToString("0.###", inv)}) B({ln.B.X.ToString("0.###", inv)},{ln.B.Y.ToString("0.###", inv)})";
            }

            if (s is EditArcSeg a)
            {
                string dir = a.CCW ? "ARC CCW" : "ARC CW";
                double r = a.Radius;

                return $"{dir} A({a.A.X.ToString("0.###", inv)},{a.A.Y.ToString("0.###", inv)}) " +
                       $"B({a.B.X.ToString("0.###", inv)},{a.B.Y.ToString("0.###", inv)}) " +
                       $"C({a.C.X.ToString("0.###", inv)},{a.C.Y.ToString("0.###", inv)}) R={r.ToString("0.###", inv)}";
            }

            return s.GetType().Name;
        }

        private List<RemovedDisplayItem> BuildRemovedDisplayList()
        {
            var list = new List<RemovedDisplayItem>();

            for (int i = 0; i < _removedSegs.Count; i++)
            {
                var seg = _removedSegs[i];
                list.Add(new RemovedDisplayItem
                {
                    Index = i,
                    Seg = seg,
                    Text = $"[{i + 1}] {DescribeSeg(seg)}"
                });
            }

            return list;
        }

        private void BtnReplace_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_removedSegs.Count == 0)
                {
                    UpdateUiState("Replace: no deleted items.");
                    return;
                }

                var ownerW = this.Owner ?? Application.Current.MainWindow;

                var w = new Window
                {
                    Title = "Deleted Segments (Insert Back)",
                    Width = 980,
                    Height = 520,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ResizeMode = ResizeMode.CanResize,
                    Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                    Owner = ownerW
                };

                var root = new Grid { Margin = new Thickness(10) };
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var lst = new ListBox
                {
                    SelectionMode = SelectionMode.Extended,
                    Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
                    Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(1),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                };

                void RefreshList()
                {
                    lst.ItemsSource = null;
                    lst.ItemsSource = BuildRemovedDisplayList();
                }

                RefreshList();

                Grid.SetRow(lst, 0);
                root.Children.Add(lst);

                var btnRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0)
                };

                var btnInsert = new Button { Content = "Insert Selected", Width = 140, Margin = new Thickness(0, 0, 10, 0) };
                var btnClose = new Button { Content = "Close", Width = 120 };

                btnRow.Children.Add(btnInsert);
                btnRow.Children.Add(btnClose);

                Grid.SetRow(btnRow, 1);
                root.Children.Add(btnRow);

                w.Content = root;

                btnClose.Click += (_, __) => w.Close();

                btnInsert.Click += (_, __) =>
                {
                    var chosen = lst.SelectedItems.Cast<RemovedDisplayItem>().ToList();
                    if (chosen.Count == 0) return;

                    // Insert back into main list (ordering not important -> append)
                    for (int i = 0; i < chosen.Count; i++)
                        _editSegs.Add(CloneSeg(chosen[i].Seg));

                    // Remove from deleted pool (remove highest index first)
                    foreach (var it in chosen.OrderByDescending(x => x.Index))
                        _removedSegs.RemoveAt(it.Index);

                    // After any task: clear selection
                    _pickA = null;
                    _pickB = null;

                    RenderEditGeometry(_editSegs);
                    ApplySelectionVisuals();
                    DrawPickMarkers();
                    RefreshList();

                    UpdateUiState($"Inserted {chosen.Count} deleted seg(s). Deleted pool: {_removedSegs.Count}. Total: {_editSegs.Count}.");

                    if (_removedSegs.Count == 0)
                        w.Close();
                };

                w.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        //=============+++++
        private void BtnFillet_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Fillet requires TWO selected elements (A and B)
                if (_pickA == null || _pickB == null || _pickA.Seg == null || _pickB.Seg == null)
                {
                    UpdateUiState("Fillet: select 2 elements first (A and B).");
                    return;
                }

                if (!_renderer.MapValid)
                {
                    UpdateUiState("Fillet: view not ready (Submit + render first).");
                    return;
                }

                // Adapt your EditSeg -> TurnEditFillet.SegView
                TurnEditFillet.SegView MakeSegView(EditSeg s)
                {
                    if (s is EditLineSeg ln)
                    {
                        return new TurnEditFillet.SegView
                        {
                            Kind = TurnEditFillet.SegKind.Line,
                            A = ln.A,
                            B = ln.B
                        };
                    }

                    if (s is EditArcSeg a)
                    {
                        return new TurnEditFillet.SegView
                        {
                            Kind = TurnEditFillet.SegKind.Arc,
                            A = a.A,
                            M = a.M,
                            B = a.B,
                            C = a.C,
                            CCW = a.CCW
                        };
                    }

                    throw new Exception("Fillet: unsupported segment type.");
                }

                var aPick = new TurnEditFillet.Pick
                {
                    Seg = MakeSegView(_pickA.Seg),
                    PickEndWorld = _pickA.PickEndWorld
                };

                var bPick = new TurnEditFillet.Pick
                {
                    Seg = MakeSegView(_pickB.Seg),
                    PickEndWorld = _pickB.PickEndWorld
                };

                var host = new FilletHost(this);

                if (!TurnEditFillet.Run(aPick, bPick, host, out string? line, out string status, _filletSeedRadius))

                {
                    if (!string.IsNullOrWhiteSpace(status))
                        UpdateUiState(status);
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    UpdateUiState("Fillet: no segment returned.");
                    return;
                }

                // Single insert helper (your existing path)
                if (!TryAddSegFromRegionInputLine_ToEditSegs(line, out _, out string err))
                {
                    UpdateUiState("Fillet insert failed: " + err);
                    return;
                }

                // Standard post-task behavior
                _pickA = null;
                _pickB = null;

                RenderEditGeometry(_editSegs);
                ApplySelectionVisuals();
                DrawPickMarkers();

                UpdateUiState("Fillet: inserted candidate arc.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private sealed class FilletHost : TurnEditFillet.IHost
        {
            private readonly TurnEditWindow _w;

            public FilletHost(TurnEditWindow w)
            {
                _w = w;
            }

            public bool MapValid => _w._renderer.MapValid;

            public void ClearPreviewOnly()
            {
                _w._renderer.ClearPreviewOnly();
            }

            public void DrawPreviewPolylineWorld(IReadOnlyList<Point> worldPts, Brush stroke, double thickness, double opacity)
            {
                _w._renderer.DrawPreviewPolylineWorld(worldPts, stroke, thickness, opacity);
            }

            public void DrawPreviewPointWorld(Point worldPt, Brush fill, double diamPx, double opacity)
            {
                _w._renderer.DrawPreviewPointWorld(worldPt, fill, diamPx, opacity);
            }
            
            public bool LogWindowShow => Settings.Default.LogWindowShow;

            public void ShowLogWindow(string title, string text)
            {
                _w.ShowLogWindowAlways(title, text);
            }
        }








        // ============================================================
        // Mouse + selection/pan/zoom
        // ============================================================

        private void EditCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            EditCanvas.Focus();

            // PAN MODE: start drag (unchanged)
            if (BtnModePan != null && BtnModePan.IsChecked == true)
            {
                _isPanning = true;
                _panStartCanvas = e.GetPosition(EditCanvas);
                _panStartTranslateX = _translate.X;
                _panStartTranslateY = _translate.Y;
                EditCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // SELECT MODE: arm long-press; actual selection happens on MouseUp (short click)
            if (BtnModeSelect != null && BtnModeSelect.IsChecked == true)
            {
                _selectDownPending = true;
                _selectLongPressFired = false;
                _selectDownCanvas = e.GetPosition(EditCanvas);

                StartSelectLongPressTimer();
                EditCanvas.CaptureMouse();

                e.Handled = true;
                return;
            }
        }


        private void HandleSelectClick(Point clickCanvas)
        {
            if (_editSegs == null || _editSegs.Count == 0) return;

            // Selection is driven by renderer's pick points.
            int chosenIdx = PickClosestSegByPickPoints(clickCanvas, out double bestDist);
            if (chosenIdx < 0) return;

            // Debug: show nearest pick point on the chosen segment (orange)
            UpdatePickDebugHitFromSeg(chosenIdx, clickCanvas);

            // Toggle off if clicked same selected segment (your rule)
            if (_pickA != null && _pickA.SegIndex == chosenIdx)
            {
                _pickA = _pickB;
                _pickB = null;
                ApplySelectionVisuals();
                DrawPickMarkers();
                PushSelectionStatusLine("A cleared");
                return;
            }

            if (_pickB != null && _pickB.SegIndex == chosenIdx)
            {
                _pickB = null;
                ApplySelectionVisuals();
                DrawPickMarkers();
                PushSelectionStatusLine("B cleared");
                return;
            }

            // Store nearest endpoint to click (screen space)
            var seg = _editSegs[chosenIdx];
            Point sStart = WorldToScreen(seg.Start);
            Point sEnd = WorldToScreen(seg.End);

            bool pickStart = Dist(clickCanvas, sStart) <= Dist(clickCanvas, sEnd);
            Point pickedWorld = pickStart ? seg.Start : seg.End;

            var pick = new PickInfo
            {
                SegIndex = chosenIdx,
                Seg = seg,
                PickStart = pickStart,
                PickEndWorld = pickedWorld,
                ClickCanvas = clickCanvas
            };

            // FIFO max 2: A then B. Third drops A, shifts B->A.
            if (_pickA == null)
            {
                _pickA = pick;
            }
            else if (_pickB == null)
            {
                _pickB = pick;
            }
            else
            {
                _pickA = _pickB;
                _pickB = pick;
            }

            ApplySelectionVisuals();
            DrawPickMarkers();

            PushSelectionStatusLine(null);
        }



        private void PushSelectionStatusLine(string? note)
        {
            string line = BuildSelectionStatusLine(note);
            if (string.IsNullOrWhiteSpace(line)) return;

            // NEWEST AT TOP: rebuild queue with new line first
            var list = _statusHistory.ToList();
            list.Insert(0, line);
            if (list.Count > STATUS_HISTORY_MAX_LINES)
                list.RemoveRange(STATUS_HISTORY_MAX_LINES, list.Count - STATUS_HISTORY_MAX_LINES);

            _statusHistory.Clear();
            for (int i = 0; i < list.Count; i++)
                _statusHistory.Enqueue(list[i]);

            // Selection status: show history, newest in GraphicTextColor, older grey
            RenderSelectionStatusHistory();
        }



        private string BuildSelectionStatusLine(string? note)
        {
            string a = (_pickA == null) ? "A=-" : DescribePick(_pickA, "A");
            string b = (_pickB == null) ? "B=-" : DescribePick(_pickB, "B");

            if (!string.IsNullOrWhiteSpace(note))
                return $"Select: {a} | {b} | {note}";

            return $"Select: {a} | {b}";
        }

        private static string DescribePick(PickInfo p, string label)
        {
            string endTxt = p.PickStart ? "Start" : "End";
            return $"{label}[{p.SegIndex + 1} {endTxt}] {DescribeSegForStatus(p.Seg)}";
        }

        private static string DescribeSegForStatus(EditSeg s)
        {
            var inv = CultureInfo.InvariantCulture;

            static string Pt(Point p) =>
                $"({p.X.ToString("0.###", CultureInfo.InvariantCulture)},{p.Y.ToString("0.###", CultureInfo.InvariantCulture)})";

            if (s is EditLineSeg ln)
            {
                return $"LINE A{Pt(ln.A)} B{Pt(ln.B)}";
            }

            if (s is EditArcSeg a)
            {
                double r = a.Radius;
                // per your request: Arc R... CP(x,z) start(x,z) end(x,z)
                return $"ARC R={r.ToString("0.###", inv)} CP{Pt(a.C)} S{Pt(a.A)} E{Pt(a.B)}";
            }

            return s.GetType().Name;
        }




        //===================================+++++
        private int PickClosestSegByPickPoints(Point clickCanvas, out double bestDist)
        {
            // Selection is driven by renderer's pick-debug points (even if not drawn).
            return _renderer.PickClosestSegByPickPoints(clickCanvas, out bestDist);
        }

        private void ApplySelectionVisuals()
        {
            // Use renderer-owned dictionaries directly (removes duplicate state in TurnEditWindow later)
            foreach (var kv in _renderer.ShapeBySegIndex)
            {
                int idx = kv.Key;
                Shape sh = kv.Value;

                bool isA = (_pickA != null && _pickA.SegIndex == idx);
                bool isB = (_pickB != null && _pickB.SegIndex == idx);

                if (isA || isB)
                {
                    if (_renderer.BaseStyleBySegIndex.TryGetValue(idx, out var baseStyle))
                    {
                        sh.Stroke = isA ? Brushes.Lime : Brushes.DeepSkyBlue;
                        sh.StrokeThickness = Math.Max(1.0, baseStyle.thick + 1.0);
                    }
                    else
                    {
                        sh.Stroke = isA ? Brushes.Lime : Brushes.DeepSkyBlue;
                    }
                }
                else
                {
                    if (_renderer.BaseStyleBySegIndex.TryGetValue(idx, out var baseStyle))
                    {
                        sh.Stroke = baseStyle.stroke;
                        sh.StrokeThickness = baseStyle.thick;
                    }
                }
            }
        }

        private void ClearPickMarkersOnly()
        {
            for (int i = EditCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (EditCanvas.Children[i] is FrameworkElement fe && fe.Tag is string tag && tag == TAG_PICK)
                {
                    EditCanvas.Children.RemoveAt(i);
                }
            }
        }

        private void DrawPickMarkers()
        {
            ClearPickMarkersOnly();
            if (_pickA != null) DrawOnePickMarker(_pickA);
            if (_pickB != null) DrawOnePickMarker(_pickB);
        }

        private void DrawOnePickMarker(PickInfo p)
        {
            double scale = Math.Max(1e-9, _scale.ScaleX);
            double invScale = 1.0 / scale;

            double r = (PICK_DIAM_PX * 0.5) * invScale;
            double len = PICK_CROSS_LEN_PX * invScale;
            double thick = PICK_STROKE_PX * invScale;

            Brush stroke = Brushes.Orange;

            // circle
            var el = new Ellipse
            {
                Width = r * 2.0,
                Height = r * 2.0,
                Stroke = stroke,
                StrokeThickness = thick,
                Fill = Brushes.Transparent,
                Tag = TAG_PICK
            };

            Canvas.SetLeft(el, p.ClickCanvas.X - r);
            Canvas.SetTop(el, p.ClickCanvas.Y - r);
            EditCanvas.Children.Add(el);

            // crosshair
            var h = new Line
            {
                X1 = p.ClickCanvas.X - len,
                Y1 = p.ClickCanvas.Y,
                X2 = p.ClickCanvas.X + len,
                Y2 = p.ClickCanvas.Y,
                Stroke = stroke,
                StrokeThickness = thick,
                Tag = TAG_PICK
            };

            var v = new Line
            {
                X1 = p.ClickCanvas.X,
                Y1 = p.ClickCanvas.Y - len,
                X2 = p.ClickCanvas.X,
                Y2 = p.ClickCanvas.Y + len,
                Stroke = stroke,
                StrokeThickness = thick,
                Tag = TAG_PICK
            };

            EditCanvas.Children.Add(h);
            EditCanvas.Children.Add(v);
        }

        private void EditCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            // PAN MODE move (unchanged)
            if (_isPanning)
            {
                if (BtnModePan == null || BtnModePan.IsChecked != true) return;

                Point p = e.GetPosition(EditCanvas);

                double dx = (p.X - _panStartCanvas.X) * _scale.ScaleX;
                double dy = (p.Y - _panStartCanvas.Y) * _scale.ScaleY;

                _translate.X = _panStartTranslateX + dx;
                _translate.Y = _panStartTranslateY + dy;

                e.Handled = true;
                return;
            }

            // SELECT MODE: cancel long-press if user drags too far
            if (_selectDownPending && !_selectLongPressFired)
            {
                Point p = e.GetPosition(EditCanvas);
                double dx = p.X - _selectDownCanvas.X;
                double dy = p.Y - _selectDownCanvas.Y;

                if ((dx * dx + dy * dy) > (SELECT_LONG_PRESS_MOVE_TOL_PX * SELECT_LONG_PRESS_MOVE_TOL_PX))
                {
                    CancelSelectLongPressTimer();
                }
            }
        }


        private void EditCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // PAN end (unchanged)
            if (_isPanning)
            {
                _isPanning = false;
                if (EditCanvas.IsMouseCaptured) EditCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            // SELECT: decide short click vs long press
            if (_selectDownPending)
            {
                CancelSelectLongPressTimer();

                bool fired = _selectLongPressFired;
                _selectDownPending = false;
                _selectLongPressFired = false;

                if (EditCanvas.IsMouseCaptured) EditCanvas.ReleaseMouseCapture();

                // Short click => do the normal selection
                if (!fired)
                    HandleSelectClick(_selectDownCanvas);

                e.Handled = true;
                return;
            }
        }


        private void EditCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            // PAN cancel (unchanged)
            if (_isPanning)
            {
                _isPanning = false;
                if (EditCanvas.IsMouseCaptured) EditCanvas.ReleaseMouseCapture();
                return;
            }

            // SELECT cancel
            if (_selectDownPending)
            {
                CancelSelectLongPressTimer();
                _selectDownPending = false;
                _selectLongPressFired = false;
                if (EditCanvas.IsMouseCaptured) EditCanvas.ReleaseMouseCapture();
            }
        }


        private void StartSelectLongPressTimer()
        {
            if (_selectLongPressTimer == null)
            {
                _selectLongPressTimer = new DispatcherTimer();
                _selectLongPressTimer.Tick += SelectLongPressTimer_Tick;
            }

            _selectLongPressTimer.Interval = TimeSpan.FromMilliseconds(SELECT_LONG_PRESS_MS);
            _selectLongPressTimer.Stop();
            _selectLongPressTimer.Start();
        }

        private void CancelSelectLongPressTimer()
        {
            if (_selectLongPressTimer != null)
                _selectLongPressTimer.Stop();
        }

        private void SelectLongPressTimer_Tick(object? sender, EventArgs e)
        {
            if (_selectLongPressTimer != null)
                _selectLongPressTimer.Stop();

            if (!_selectDownPending) return;
            if (_selectLongPressFired) return;

            _selectLongPressFired = true;

            // Long click action: store fillet seed radius from ARC under cursor
            StoreFilletSeedRadiusFromCanvasPoint(_selectDownCanvas);
        }

        private void StoreFilletSeedRadiusFromCanvasPoint(Point clickCanvas)
        {
            if (_editSegs == null || _editSegs.Count == 0) return;

            int idx = PickClosestSegByPickPoints(clickCanvas, out _);
            if (idx < 0 || idx >= _editSegs.Count) return;

            var seg = _editSegs[idx];

            if (seg is not EditArcSeg a)
            {
                PushStatusHistoryLineTop("R store: NOT AN ARC");
                return;
            }

            double r = a.Radius;
            if (r <= 1e-9)
            {
                PushStatusHistoryLineTop("R store: ARC R invalid");
                return;
            }

            string msg = $"Store fillet seed radius R={r.ToString("0.###", CultureInfo.InvariantCulture)} ?";
            var res = MessageBox.Show(msg, "Fillet Seed Radius", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                _filletSeedRadius = r;
                PushStatusHistoryLineTop($"R={r.ToString("0.###", CultureInfo.InvariantCulture)} stored (Fillet seed)");
            }
            else
            {
                PushStatusHistoryLineTop($"R={r.ToString("0.###", CultureInfo.InvariantCulture)} not stored");
            }
        }

        // Reuse the same status history display (top line = current)
        private void PushStatusHistoryLineTop(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            var list = _statusHistory.ToList();
            list.Insert(0, line);
            if (list.Count > STATUS_HISTORY_MAX_LINES)
                list.RemoveRange(STATUS_HISTORY_MAX_LINES, list.Count - STATUS_HISTORY_MAX_LINES);

            _statusHistory.Clear();
            for (int i = 0; i < list.Count; i++)
                _statusHistory.Enqueue(list[i]);

            RenderSelectionStatusHistory();
        }




        private void BtnBtnModePan(object sender, RoutedEventArgs e)
        {
            bool panOn = (BtnModePan != null && BtnModePan.IsChecked == true);

            if (panOn)
            {
                if (BtnModeSelect != null) BtnModeSelect.IsChecked = false;
                if (EditCanvas != null) EditCanvas.Cursor = Cursors.Hand;
                UpdateUiState("Mode: Pan");
            }
            else
            {
                if (BtnModeSelect != null) BtnModeSelect.IsChecked = true;
                if (EditCanvas != null) EditCanvas.Cursor = Cursors.Cross;
                UpdateUiState("Mode: Select");
            }
        }

        private void BtnBtnModeSelect(object sender, RoutedEventArgs e)
        {
            // Select toggles itself, but selecting Select must turn Pan off.
            bool selectOn = (BtnModeSelect != null && BtnModeSelect.IsChecked == true);

            if (selectOn)
            {
                if (BtnModePan != null) BtnModePan.IsChecked = false;

                // refresh: clear selections (your rule)
                _pickA = null;
                _pickB = null;

                ApplySelectionVisuals();
                DrawPickMarkers();

                if (EditCanvas != null) EditCanvas.Cursor = Cursors.Cross;
                UpdateUiState("Mode: Select");
            }
            else
            {
                // Don’t allow Select to be off
                if (BtnModeSelect != null) BtnModeSelect.IsChecked = true;
                if (EditCanvas != null) EditCanvas.Cursor = Cursors.Cross;
                UpdateUiState("Mode: Select");
            }
        }

        private void EditCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            const double zoomStep = 1.2;

            double factor = (e.Delta > 0) ? zoomStep : (1.0 / zoomStep);

            double newScaleX = _scale.ScaleX * factor;
            double newScaleY = _scale.ScaleY * factor;

            const double minScale = 0.1;
            const double maxScale = 50.0;

            if (newScaleX < minScale || newScaleX > maxScale) return;

            Point p = e.GetPosition(EditCanvas);

            double oldScaleX = _scale.ScaleX;
            double oldScaleY = _scale.ScaleY;

            _scale.ScaleX = newScaleX;
            _scale.ScaleY = newScaleY;

            _translate.X = (oldScaleX - newScaleX) * p.X + _translate.X;
            _translate.Y = (oldScaleY - newScaleY) * p.Y + _translate.Y;

            e.Handled = true;

            // Keep pick-debug dots + pick markers zoom-invariant.
            // (They are drawn with inverse scale, so redraw after scale changes.)
            try
            {
                _renderer.RenderPickDebugPoints();
                DrawPickMarkers();
                _renderer.DrawBackgroundGridWorld();
            }
            catch
            {
                // never crash on wheel
            }
        }

        // ============================================================
        // Helpers: prompt name + unique name
        // ============================================================
        private string? PromptForRegionName(string title, string label)
        {
            var ownerW = this.Owner ?? Application.Current.MainWindow;

            var w = new Window
            {
                Title = title,
                Width = 520,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                Owner = ownerW,
                Content = null
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tbLabel = new TextBlock
            {
                Text = label,
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(tbLabel, 0);
            root.Children.Add(tbLabel);

            var tb = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 10),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14
            };
            Grid.SetRow(tb, 1);
            root.Children.Add(tb);

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnOk = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "Cancel", Width = 90 };

            btns.Children.Add(btnOk);
            btns.Children.Add(btnCancel);

            Grid.SetRow(btns, 2);
            root.Children.Add(btns);

            w.Content = root;

            string? result = null;

            btnOk.Click += (s, e) =>
            {
                result = tb.Text;
                w.DialogResult = true;
                w.Close();
            };

            btnCancel.Click += (s, e) =>
            {
                w.DialogResult = false;
                w.Close();
            };

            w.Loaded += (s, e) =>
            {
                PositionDialogBottomRight(w, ownerW, 16.0);
                tb.Focus();
                tb.SelectAll();
            };

            bool? ok = w.ShowDialog();
            if (ok != true) return null;

            return result;
        }

        private string MakeUniqueTurnSetName(string baseName)
        {
            string name = baseName;
            var main = GetMain();

            bool exists = false;
            for (int i = 0; i < main.TurnSets.Count; i++)
            {
                var s = main.TurnSets[i];
                if (s != null && string.Equals(s.Name, name, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists) return name;

            int k = 2;
            while (true)
            {
                string candidate = $"{baseName}_{k}";

                bool found = false;
                for (int i = 0; i < main.TurnSets.Count; i++)
                {
                    var s = main.TurnSets[i];
                    if (s != null && string.Equals(s.Name, candidate, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found) return candidate;
                k++;
            }
        }

        // ============================================================
        // Region resolve helpers (match TurningPage rule)
        // ============================================================
        private static string NormalizeLineForMatch(string? s)
        {
            if (s == null) return "";
            string t = s.Trim();
            t = Regex.Replace(t, @"\s+", " ");
            return t;
        }

        private static bool TryResolveRegionRange(
            List<string> allLines,
            ObservableCollection<string> regionLines,
            out int start,
            out int end)
        {
            start = -1;
            end = -1;

            if (allLines == null || allLines.Count == 0) return false;
            if (regionLines == null || regionLines.Count == 0) return false;

            int n = regionLines.Count;
            if (n > allLines.Count) return false;

            var needle = new string[n];
            for (int i = 0; i < n; i++)
                needle[i] = NormalizeLineForMatch(regionLines[i]);

            int matchCount = 0;
            int foundStart = -1;

            for (int s = 0; s <= allLines.Count - n; s++)
            {
                bool ok = true;

                for (int j = 0; j < n; j++)
                {
                    if (NormalizeLineForMatch(allLines[s + j]) != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    matchCount++;
                    foundStart = s;
                    if (matchCount > 1) break;
                }
            }

            if (matchCount != 1 || foundStart < 0) return false;

            start = foundStart;
            end = foundStart + n - 1;
            return true;
        }

        // ============================================================
        // Build script text (same as TurningPage "View All" style)
        // ============================================================
        //=================================================================================

        // ============================================================
        // Script parsing: styles + transform + geometry (skip closing-color blocks)
        // ============================================================
        private static string NormalizeHexColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string t = s.Trim();
            if (!t.StartsWith("#", StringComparison.Ordinal)) t = "#" + t;
            if (t.Length == 7) t = "#FF" + t.Substring(1);
            return t.ToUpperInvariant();
        }

        private static Brush BrushFromHex(string hexNorm, Brush fallback)
        {
            try
            {
                var bc = new BrushConverter();
                var b = (Brush)bc.ConvertFromString(hexNorm);
                if (b != null && b.CanFreeze) b.Freeze();
                return b ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

       
       

        // ============================================================
        // Editable geometry model (single coherent API)
        // ============================================================
        private abstract class EditSeg
        {
            public Brush Stroke = Brushes.White;
            public double Thickness = 1.0;

            public abstract Point Start { get; }
            public abstract Point End { get; }

            public abstract void SetStart(Point p);
            public abstract void SetEnd(Point p);
            public abstract void ReverseInPlace();
            public abstract void ExpandWorldBounds(ref double minX, ref double maxX, ref double minZ, ref double maxZ);
        }

        private sealed class EditLineSeg : EditSeg
        {
            public Point A; // Start
            public Point B; // End

            public override Point Start => A;
            public override Point End => B;

            public override void SetStart(Point p) => A = p;
            public override void SetEnd(Point p) => B = p;

            public override void ReverseInPlace()
            {
                var t = A;
                A = B;
                B = t;
            }

            public override void ExpandWorldBounds(ref double minX, ref double maxX, ref double minZ, ref double maxZ)
            {
                minX = Math.Min(minX, Math.Min(A.X, B.X));
                maxX = Math.Max(maxX, Math.Max(A.X, B.X));
                minZ = Math.Min(minZ, Math.Min(A.Y, B.Y));
                maxZ = Math.Max(maxZ, Math.Max(A.Y, B.Y));
            }
        }

        //=====================++++
        private sealed class EditArcSeg : EditSeg
        {
            public Point A; // Start
            public Point M; // Mid (must lie on the chosen sweep)
            public Point B; // End
            public Point C; // Center
            public bool CCW; // true => CCW, false => CW

            public override Point Start => A;
            public override Point End => B;

            public override void SetStart(Point p) => A = p;
            public override void SetEnd(Point p) => B = p;

            public override void ReverseInPlace()
            {
                var t = A;
                A = B;
                B = t;
                CCW = !CCW;
            }

            public double Radius => Dist(A, C);

            public override void ExpandWorldBounds(ref double minX, ref double maxX, ref double minZ, ref double maxZ)
            {
                double r = Radius;

                if (r < 1e-9)
                {
                    minX = Math.Min(minX, Math.Min(A.X, B.X));
                    maxX = Math.Max(maxX, Math.Max(A.X, B.X));
                    minZ = Math.Min(minZ, Math.Min(A.Y, B.Y));
                    maxZ = Math.Max(maxZ, Math.Max(A.Y, B.Y));
                    return;
                }

                // conservative full circle bounds for fit
                minX = Math.Min(minX, C.X - r);
                maxX = Math.Max(maxX, C.X + r);
                minZ = Math.Min(minZ, C.Y - r);
                maxZ = Math.Max(maxZ, C.Y + r);

                minX = Math.Min(minX, Math.Min(A.X, B.X));
                maxX = Math.Max(maxX, Math.Max(A.X, B.X));
                minZ = Math.Min(minZ, Math.Min(A.Y, B.Y));
                maxZ = Math.Max(maxZ, Math.Max(A.Y, B.Y));
            }

            public void GetWpfArcFlags(out bool isLargeArc, out SweepDirection sweepDir)
            {
                // Decide whether MID lies on the "short" sweep.
                // If MID not on short, we force the large-arc branch.
                double aA = Norm2Pi(Math.Atan2(A.Y - C.Y, A.X - C.X));
                double aM = Norm2Pi(Math.Atan2(M.Y - C.Y, M.X - C.X));
                double aB = Norm2Pi(Math.Atan2(B.Y - C.Y, B.X - C.X));

                if (CCW)
                {
                    double dAB = DeltaCCW(aA, aB);
                    double dAM = DeltaCCW(aA, aM);
                    bool midOnShort = (dAM <= dAB + 1e-9);

                    // If mid not on short, use other branch (large)
                    isLargeArc = midOnShort ? (dAB > Math.PI) : (dAB <= Math.PI);
                    sweepDir = SweepDirection.Counterclockwise;
                }
                else
                {
                    double dAB = DeltaCW(aA, aB);
                    double dAM = DeltaCW(aA, aM);
                    bool midOnShort = (dAM <= dAB + 1e-9);

                    isLargeArc = midOnShort ? (dAB > Math.PI) : (dAB <= Math.PI);
                    sweepDir = SweepDirection.Clockwise;
                }
            }
        }

        // ============================================================
        // Chain + snap to closed loop
        // ============================================================
        private EditSeg CloneSeg(EditSeg s)
        {
            if (s is EditLineSeg ln)
            {
                return new EditLineSeg
                {
                    Stroke = ln.Stroke,
                    Thickness = ln.Thickness,
                    A = ln.A,
                    B = ln.B
                };
            }

            if (s is EditArcSeg a)
            {
                return new EditArcSeg
                {
                    Stroke = a.Stroke,
                    Thickness = a.Thickness,
                    A = a.A,
                    M = a.M,
                    B = a.B,
                    C = a.C,
                    CCW = a.CCW
                };
            }

            throw new Exception("Unknown EditSeg type in CloneSeg.");
        }

        private bool TryChainAndSnap(
    List<EditSeg> input,
    double tol,
    out List<EditSeg> ordered,
    out double maxEndpointGap,
    out bool isClosed,
    out Point failPointWorld,
    out string failReason)
        {
            ordered = new List<EditSeg>();
            maxEndpointGap = 0.0;
            isClosed = false;
            failPointWorld = new Point(double.NaN, double.NaN);
            failReason = "";

            if (input == null || input.Count == 0)
            {
                failReason = "No segments.";
                return false;
            }

            if (!TurnEditMath.IsFinite(tol) || tol <= 0)
                tol = 1e-6;

            // --------------------------------------------------------------------
            // ROBUST ORDER-INDEPENDENT CHAIN:
            // 1) Cluster endpoints into nodes (within tol).
            // 2) Each segment connects 2 nodes.
            // 3) Closed loop requires every node degree == 2.
            // 4) Traverse deterministically from "lowest Z then lowest X" node.
            // 5) Snap every segment endpoint EXACTLY to node representative point.
            // --------------------------------------------------------------------

            // Clone input so we never mutate original list
            var segs = new List<EditSeg>(input.Count);
            for (int i = 0; i < input.Count; i++)
                segs.Add(CloneSeg(input[i]));

            // Node clustering
            var nodes = new List<Point>();               // representative point per node
            var nodeDegree = new List<int>();            // degree per node

            int FindOrAddNode(Point p, ref double maxSnap)
            {
                int best = -1;
                double bestD = double.PositiveInfinity;

                for (int i = 0; i < nodes.Count; i++)
                {
                    double d = Dist(p, nodes[i]);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }

                if (best >= 0 && bestD <= tol)
                {
                    if (bestD > maxSnap) maxSnap = bestD;
                    return best;
                }

                nodes.Add(p);
                nodeDegree.Add(0);
                return nodes.Count - 1;
            }

            double maxSnapDist = 0.0;

            // Segment graph record
            // We keep (seg, n0, n1). n0/n1 are the clustered nodes for seg.Start/seg.End
            var edges = new List<(EditSeg seg, int n0, int n1)>(segs.Count);

            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];

                int n0 = FindOrAddNode(s.Start, ref maxSnapDist);
                int n1 = FindOrAddNode(s.End, ref maxSnapDist);

                if (n0 == n1)
                {
                    // Degenerate segment (both ends same node) -> breaks loop topology
                    failPointWorld = nodes[n0];
                    maxEndpointGap = maxSnapDist;
                    failReason = "Degenerate segment: start and end collapse to same node.";
                    return false;
                }

                edges.Add((s, n0, n1));
                nodeDegree[n0]++;
                nodeDegree[n1]++;
            }

            // Closed loop needs all nodes degree == 2
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodeDegree[i] != 2)
                {
                    failPointWorld = nodes[i];
                    maxEndpointGap = maxSnapDist;
                    failReason = $"Topology not a single closed loop: node degree {nodeDegree[i]} (expected 2).";
                    return false;
                }
            }

            // Build adjacency: node -> list of edge indices
            var adj = new List<List<int>>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
                adj.Add(new List<int>(2));

            for (int ei = 0; ei < edges.Count; ei++)
            {
                adj[edges[ei].n0].Add(ei);
                adj[edges[ei].n1].Add(ei);
            }

            // Deterministic start node: lowest Z (Y), then lowest X
            int startNode = 0;
            for (int i = 1; i < nodes.Count; i++)
            {
                var a = nodes[i];
                var b = nodes[startNode];

                if (a.Y < b.Y - 1e-12) startNode = i;
                else if (Math.Abs(a.Y - b.Y) <= 1e-12 && a.X < b.X - 1e-12) startNode = i;
            }

            // Traverse the loop
            var usedEdge = new bool[edges.Count];

            int curNode = startNode;
            int prevNode = -1;

            for (int step = 0; step < edges.Count; step++)
            {
                // pick next unused edge from curNode
                int e0 = adj[curNode][0];
                int e1 = adj[curNode][1];

                int pickEdge = -1;

                if (!usedEdge[e0] && !usedEdge[e1])
                {
                    // both available (first step only). Choose deterministic "next node":
                    // pick the edge that leads to the node with lower (Z then X).
                    int next0 = (edges[e0].n0 == curNode) ? edges[e0].n1 : edges[e0].n0;
                    int next1 = (edges[e1].n0 == curNode) ? edges[e1].n1 : edges[e1].n0;

                    Point p0 = nodes[next0];
                    Point p1 = nodes[next1];

                    bool chooseE0 = false;

                    if (p0.Y < p1.Y - 1e-12) chooseE0 = true;
                    else if (Math.Abs(p0.Y - p1.Y) <= 1e-12 && p0.X < p1.X - 1e-12) chooseE0 = true;

                    pickEdge = chooseE0 ? e0 : e1;
                }
                else if (!usedEdge[e0])
                {
                    pickEdge = e0;
                }
                else if (!usedEdge[e1])
                {
                    pickEdge = e1;
                }
                else
                {
                    failPointWorld = nodes[curNode];
                    maxEndpointGap = maxSnapDist;
                    failReason = "Traversal failed: both incident edges already used.";
                    return false;
                }

                usedEdge[pickEdge] = true;

                var (seg, n0, n1) = edges[pickEdge];

                // Orient so seg.Start is at curNode
                if (n0 != curNode && n1 == curNode)
                {
                    // reverse the segment AND swap nodes so n0 always matches seg.Start node
                    seg.ReverseInPlace();
                    int t = n0; n0 = n1; n1 = t;
                }

                if (n0 != curNode)
                {
                    failPointWorld = nodes[curNode];
                    maxEndpointGap = maxSnapDist;
                    failReason = "Traversal failed: edge does not touch current node.";
                    return false;
                }

                // Snap endpoints EXACTLY to node representative points
                seg.SetStart(nodes[n0]);
                seg.SetEnd(nodes[n1]);

                ordered.Add(seg);

                prevNode = curNode;
                curNode = n1;
            }

            // Must end at start node for closed loop
            if (curNode != startNode)
            {
                failPointWorld = nodes[curNode];
                maxEndpointGap = maxSnapDist;
                failReason = "Traversal ended at wrong node: loop not closed.";
                return false;
            }

            // Compute max endpoint gap based on node snapping (this is the only real "gap" now)
            maxEndpointGap = maxSnapDist;
            isClosed = true;

            return true;
        }



        // ============================================================
        // Export: closed loop -> TURN G-code (IK, no tool comp)
        // X in editor is RADIUS. TURN G-code X is DIAMETER => output X = 2*radius
        // ============================================================
        private List<string> BuildRegionGcodeFromClosedLoop_IK(List<EditSeg> ordered)
        {
            var outLines = new List<string>();
            var inv = CultureInfo.InvariantCulture;

            // IMPORTANT:
            // Save/Builder round-trip must not introduce endpoint gaps.
            // Using "0.###" can create tiny mismatches after re-parse (and then the builder/freecad sew check trips).
            // So we output higher precision everywhere.
            const string F = "0.########"; // 8dp is plenty for mm-scale geometry

            if (ordered == null || ordered.Count == 0) return outLines;

            Point p0 = ordered[0].Start;

            outLines.Add(string.Format(
                inv,
                "G1 X{0} Z{1}",
                (p0.X * 2.0).ToString(F, inv),
                p0.Y.ToString(F, inv)));

            for (int i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];

                if (s is EditLineSeg ln)
                {
                    Point pe = ln.End;

                    outLines.Add(string.Format(
                        inv,
                        "G1 X{0} Z{1}",
                        (pe.X * 2.0).ToString(F, inv),
                        pe.Y.ToString(F, inv)));
                }
                else if (s is EditArcSeg a)
                {
                    Point ps = a.Start;
                    Point pe = a.End;

                    double r = a.Radius;
                    if (r < 1e-9) continue;

                    // X is DIAMETER in output => I must be in diameter-units too
                    double I = (a.C.X - ps.X) * 2.0;
                    double K = (a.C.Y - ps.Y);

                    string g = a.CCW ? "G3" : "G2";

                    outLines.Add(string.Format(
                        inv,
                        "{0} X{1} Z{2} I{3} K{4}",
                        g,
                        (pe.X * 2.0).ToString(F, inv),
                        pe.Y.ToString(F, inv),
                        I.ToString(F, inv),
                        K.ToString(F, inv)));
                }
                else
                {
                    throw new Exception("Unknown segment type during export.");
                }
            }

            return outLines;
        }



        //===========++++
        // ============================================================
        // Save plumbing: markers + append editor + new set
        // ============================================================
        private static bool LineHasAxis(string? line, char axis)
        {
            if (string.IsNullOrEmpty(line)) return false;

            string a = axis.ToString();
            var rx = new Regex(@"(?i)(?<![A-Z])" + a + @"\s*([-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)");
            return rx.IsMatch(line);
        }

        private List<string> AppendStartEndMarkers(List<string> lines, string newRegName)
        {
            if (lines == null || lines.Count == 0)
                throw new Exception("No lines to tag.");

            int firstX = -1, firstZ = -1, lastX = -1, lastZ = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i] ?? "";
                if (firstX < 0 && LineHasAxis(s, 'X')) firstX = i;
                if (firstZ < 0 && LineHasAxis(s, 'Z')) firstZ = i;
                if (LineHasAxis(s, 'X')) lastX = i;
                if (LineHasAxis(s, 'Z')) lastZ = i;
            }

            if (firstX < 0 || firstZ < 0 || lastX < 0 || lastZ < 0)
                throw new Exception("Cannot tag region: X/Z markers not found.");

            string tagST = $" ({newRegName} ST)";
            string tagEND = $" ({newRegName} END)";

            


            lines.Insert(0, tagST);
            


           lines.Add(tagEND);

            return lines;
        }

        private void AppendRegionBlockToEditor(List<string> appendedLines)
        {
            var rtb = GetGcodeEditor();
            if (rtb == null) throw new Exception("Internal error: G-code editor not found in MainWindow.");

            string block = string.Join("\r\n", appendedLines);

            TextRange tr = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
            string existing = tr.Text ?? "";

            bool endsWithNewline = existing.EndsWith("\n") || existing.EndsWith("\r\n");
            if (!endsWithNewline) rtb.AppendText("\r\n");
            rtb.AppendText("");
            
            rtb.AppendText(block);
            rtb.AppendText("\r\n");
            rtb.ScrollToEnd();
        }

        

        private void ShowLogWindowIfEnabled(string title, List<string> regionLines, string regionName)
        {
            if (!Settings.Default.LogWindowShow) return;

            var sb = new StringBuilder();
            sb.AppendLine($"=== TURN EDIT: SAVED REGION '{regionName}' ===");
            sb.AppendLine();

            for (int i = 0; i < regionLines.Count; i++)
                sb.AppendLine(regionLines[i] ?? "");

            var ownerW = this.Owner ?? Application.Current.MainWindow;
            var logWindow = new CNC_Improvements_gcode_solids.Utilities.LogWindow(title, sb.ToString());
            if (ownerW != null) logWindow.Owner = ownerW;
            logWindow.Show();
            logWindow.Activate();
        }

        // ============================================================
        // Add Line tool (unchanged)
        // ============================================================
        private enum AddLineOrientation
        {
            Vertical,   // vertical on screen => constant world Z
            Horizontal  // horizontal on screen => constant world X
        }




        private AddLineOrientation? PromptAddLineOrientation()
        {
            var ownerW = this.Owner ?? Application.Current.MainWindow;

            var w = new Window
            {
                Title = "Add Line",
                Width = 360,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                Owner = ownerW
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tb = new TextBlock
            {
                Text = "Create line through selected endpoint:",
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(tb, 0);
            root.Children.Add(tb);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnV = new Button { Content = "Vertical", Width = 110, Margin = new Thickness(0, 0, 10, 0) };
            var btnH = new Button { Content = "Horizontal", Width = 110 };

            btnRow.Children.Add(btnV);
            btnRow.Children.Add(btnH);

            Grid.SetRow(btnRow, 1);
            root.Children.Add(btnRow);

            w.Content = root;

            AddLineOrientation? result = null;

            btnV.Click += (_, __) =>
            {
                result = AddLineOrientation.Vertical;
                w.DialogResult = true;
                w.Close();
            };

            btnH.Click += (_, __) =>
            {
                result = AddLineOrientation.Horizontal;
                w.DialogResult = true;
                w.Close();
            };

            bool? ok = w.ShowDialog();
            if (ok != true) return null;

            return result;
        }


        private sealed class AddLinePointResult
        {
            public AddLineOrientation Orientation;
            public Point WorldPoint; // world coords (X=radius, Z)
        }

        /// <summary>
        /// Add Line dialog for the NO-SELECTION case:
        /// - user enters X and Z (world)
        /// - chooses Vertical or Horizontal
        /// Returns null if cancelled.
        /// </summary>
        private AddLinePointResult? PromptAddLinePointAndOrientation()
        {
            var ownerW = this.Owner ?? Application.Current.MainWindow;

            var w = new Window
            {
                Title = "Add Line (World Point)",
                Width = 440,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                Owner = ownerW
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // label
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // inputs
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

            var tb = new TextBlock
            {
                Text = "Create line through WORLD point (X radius, Z):",
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(tb, 0);
            root.Children.Add(tb);

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lblX = new TextBlock
            {
                Text = "X:",
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(lblX, 0);
            grid.Children.Add(lblX);

            var txtX = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Text = "0",
                Margin = new Thickness(0, 0, 14, 0)
            };
            Grid.SetColumn(txtX, 1);
            grid.Children.Add(txtX);

            var lblZ = new TextBlock
            {
                Text = "Z:",
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(lblZ, 2);
            grid.Children.Add(lblZ);

            var txtZ = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Text = "0"
            };
            Grid.SetColumn(txtZ, 3);
            grid.Children.Add(txtZ);

            Grid.SetRow(grid, 1);
            root.Children.Add(grid);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnV = new Button { Content = "Vertical", Width = 110, Margin = new Thickness(0, 0, 10, 0) };
            var btnH = new Button { Content = "Horizontal", Width = 110, Margin = new Thickness(0, 0, 10, 0) };
            var btnCancel = new Button { Content = "Cancel", Width = 110 };

            btnRow.Children.Add(btnV);
            btnRow.Children.Add(btnH);
            btnRow.Children.Add(btnCancel);

            Grid.SetRow(btnRow, 3);
            root.Children.Add(btnRow);

            w.Content = root;

            AddLinePointResult? result = null;

            bool TryParse(out double x, out double z)
            {
                x = 0; z = 0;
                var inv = CultureInfo.InvariantCulture;

                string sx = (txtX.Text ?? "").Trim();
                string sz = (txtZ.Text ?? "").Trim();

                if (!double.TryParse(sx, NumberStyles.Float, inv, out x))
                    return false;

                if (!double.TryParse(sz, NumberStyles.Float, inv, out z))
                    return false;

                return true;
            }

            void Commit(AddLineOrientation orient)
            {
                if (!TryParse(out double x, out double z))
                {
                    MessageBox.Show(
                        "Invalid X or Z value.\n\nUse numbers like: 12  or  12.34  or  -5.6",
                        "Add Line",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return; // keep dialog open
                }

                result = new AddLinePointResult
                {
                    Orientation = orient,
                    WorldPoint = new Point(x, z)
                };

                w.DialogResult = true;
                w.Close();
            }

            btnV.Click += (_, __) => Commit(AddLineOrientation.Vertical);
            btnH.Click += (_, __) => Commit(AddLineOrientation.Horizontal);

            btnCancel.Click += (_, __) =>
            {
                w.DialogResult = false;
                w.Close();
            };

            w.Loaded += (_, __) =>
            {
                PositionDialogBottomRight(w, ownerW, 16.0);
                txtX.Focus();
                txtX.SelectAll();
            };

            bool? ok = w.ShowDialog();
            if (ok != true) return null;

            return result;
        }







        // Visible rectangle in CANVAS coordinates of the drawn geometry (accounts for current pan/zoom)
        // because pan/zoom is a RenderTransform on EditCanvas children.
        private Rect GetVisibleCanvasRectInCanvasCoords()
        {
            // Correct: visible rect is based on the VIEWPORT size, not the 3x canvas size.
            return _renderer.GetVisibleCanvasRectInCanvasCoords();
        }

        private void BtnAddLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_editSegs == null) _editSegs = new List<EditSeg>();

                // Style for new line:
                // - If we have a selection, use selection's style
                // - Else use profile color/width
                Brush stroke = _pickA?.Seg?.Stroke ?? BrushFromHex(NormalizeHexColor(Settings.Default.ProfileColor), Brushes.White);
                double thick = (_pickA?.Seg != null && _pickA.Seg.Thickness > 0) ? _pickA.Seg.Thickness : Settings.Default.ProfileWidth;

                // Case: 2-selected => line between selected endpoints (NO dialog)
                if (_pickA != null && _pickB != null)
                {
                    Point a = _pickA.PickEndWorld;
                    Point b = _pickB.PickEndWorld;

                    _editSegs.Add(new EditLineSeg { Stroke = stroke, Thickness = thick, A = a, B = b });

                    // Any task completed clears selection
                    _pickA = null;
                    _pickB = null;

                    RenderEditGeometry(_editSegs);
                    ApplySelectionVisuals();
                    DrawPickMarkers();

                    UpdateUiState("Add Line: created line between selected endpoints.");
                    return;
                }

                // Visible bounds of the VIEW (not the 3x canvas)
                Rect vis = GetVisibleCanvasRectInCanvasCoords();
                if (vis.Width <= 0 || vis.Height <= 0)
                {
                    UpdateUiState("Add Line: invalid view bounds.");
                    return;
                }

                // Case: NO selection => show dialog with X/Z + Vertical/Horizontal
                if (_pickA == null)
                {
                    var dlg = PromptAddLinePointAndOrientation();
                    if (dlg == null)
                    {
                        UpdateUiState("Add Line: cancelled.");
                        return;
                    }

                    Point wp = dlg.WorldPoint;      // world (X=radius, Z)
                    Point sp = WorldToScreen(wp);    // canvas coords

                    Point p1s, p2s;

                    if (dlg.Orientation == AddLineOrientation.Vertical)
                    {
                        // Vertical on screen => constant screen X => constant world Z
                        p1s = new Point(sp.X, vis.Top);
                        p2s = new Point(sp.X, vis.Bottom);
                    }
                    else
                    {
                        // Horizontal on screen => constant screen Y => constant world X
                        p1s = new Point(vis.Left, sp.Y);
                        p2s = new Point(vis.Right, sp.Y);
                    }

                    Point p1w = ScreenToWorld(p1s);
                    Point p2w = ScreenToWorld(p2s);

                    _editSegs.Add(new EditLineSeg { Stroke = stroke, Thickness = thick, A = p1w, B = p2w });

                    RenderEditGeometry(_editSegs);
                    ApplySelectionVisuals();
                    DrawPickMarkers();

                    UpdateUiState($"Add Line: created {(dlg.Orientation == AddLineOrientation.Vertical ? "vertical" : "horizontal")} view line through X={wp.X.ToString("0.###", CultureInfo.InvariantCulture)} Z={wp.Y.ToString("0.###", CultureInfo.InvariantCulture)}.");
                    return;
                }

                // Case: 1-selected => keep your existing behavior (orientation-only dialog)
                var orient = PromptAddLineOrientation();
                if (orient == null)
                {
                    UpdateUiState("Add Line: cancelled.");
                    return;
                }

                // Use selected endpoint as the point to pass through
                Point wpSel = _pickA.PickEndWorld;
                Point spSel = WorldToScreen(wpSel);

                Point p1sSel, p2sSel;

                if (orient.Value == AddLineOrientation.Vertical)
                {
                    p1sSel = new Point(spSel.X, vis.Top);
                    p2sSel = new Point(spSel.X, vis.Bottom);
                }
                else
                {
                    p1sSel = new Point(vis.Left, spSel.Y);
                    p2sSel = new Point(vis.Right, spSel.Y);
                }

                Point p1wSel = ScreenToWorld(p1sSel);
                Point p2wSel = ScreenToWorld(p2sSel);

                _editSegs.Add(new EditLineSeg { Stroke = stroke, Thickness = thick, A = p1wSel, B = p2wSel });

                // Any task completed clears selection
                _pickA = null;
                _pickB = null;

                RenderEditGeometry(_editSegs);
                ApplySelectionVisuals();
                DrawPickMarkers();

                UpdateUiState($"Add Line: created {(orient.Value == AddLineOrientation.Vertical ? "vertical" : "horizontal")} view line through selected endpoint.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void ShowLogWindowAlways(string title, string text)
        {
            if (!Settings.Default.LogWindowShow) return;

            var ownerW = this.Owner ?? Application.Current.MainWindow;
            var logWindow = new CNC_Improvements_gcode_solids.Utilities.LogWindow(title, text);
            if (ownerW != null) logWindow.Owner = ownerW;
            logWindow.Show();
            logWindow.Activate();
        }

        private bool TryAddSegFromRegionInputLine(
            List<EditSeg> segs,
            string line,
            Brush stroke,
            double thickness,
            out EditSeg? added,
            out string error)
        {
            added = null;
            error = "";

            if (segs == null)
            {
                error = "Target seg list is null.";
                return false;
            }

            if (!TurnEditSegsLoad.TryParseRegionInputLineToSegDto(line, stroke, thickness, out var dto, out error))
                return false;

            if (dto == null)
            {
                error = "Internal: dto null.";
                return false;
            }

            if (dto.Kind == TurnEditSegsLoad.SegKind.Line)
            {
                var seg = new EditLineSeg
                {
                    Stroke = dto.Stroke ?? Brushes.White,
                    Thickness = dto.Thickness,
                    A = dto.A,
                    B = dto.B
                };
                segs.Add(seg);
                added = seg;
                return true;
            }
            else
            {
                var seg = new EditArcSeg
                {
                    Stroke = dto.Stroke ?? Brushes.White,
                    Thickness = dto.Thickness,
                    A = dto.A,
                    M = dto.M,
                    B = dto.B,
                    C = dto.C,
                    CCW = dto.CCW
                };
                segs.Add(seg);
                added = seg;
                return true;
            }
        }

        // Convenience wrapper for your common case: add to _editSegs using selection style
        private bool TryAddSegFromRegionInputLine_ToEditSegs(string line, out EditSeg? added, out string error)
        {
            if (_editSegs == null) _editSegs = new List<EditSeg>();

            // Default style = first selection if present, else profile color/width
            Brush stroke = _pickA?.Seg?.Stroke ?? BrushFromHex(NormalizeHexColor(Settings.Default.ProfileColor), Brushes.White);
            double thick = (_pickA?.Seg != null && _pickA.Seg.Thickness > 0) ? _pickA.Seg.Thickness : Settings.Default.ProfileWidth;

            return TryAddSegFromRegionInputLine(_editSegs, line, stroke, thick, out added, out error);
        }

        private void BtnTrim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_editSegs == null || _editSegs.Count == 0)
                {
                    UpdateUiState("Trim: no geometry loaded.");
                    return;
                }

                if (_pickA == null || _pickB == null || _pickA.Seg == null || _pickB.Seg == null)
                {
                    UpdateUiState("Trim: select 2 elements first (A=trim element, B=target).");
                    return;
                }

                if (!_renderer.MapValid)
                {
                    UpdateUiState("Trim: view not ready (Submit + render first).");
                    return;
                }

                int beforeCount = _editSegs.Count;
                double sewTol = SewTol;
                double editTol = EditTol;

                TurnEditTrim.SegView MakeSegView(EditSeg s)
                {
                    if (s is EditLineSeg ln)
                    {
                        return new TurnEditTrim.SegView
                        {
                            Kind = TurnEditTrim.SegKind.Line,
                            A = ln.A,
                            B = ln.B
                        };
                    }

                    if (s is EditArcSeg a)
                    {
                        return new TurnEditTrim.SegView
                        {
                            Kind = TurnEditTrim.SegKind.Arc,
                            A = a.A,
                            M = a.M,
                            B = a.B,
                            C = a.C,
                            CCW = a.CCW
                        };
                    }

                    throw new Exception("Trim: unsupported segment type.");
                }

                var aPick = new TurnEditTrim.Pick
                {
                    SegIndex = _pickA.SegIndex,
                    Seg = MakeSegView(_pickA.Seg),
                    PickStart = _pickA.PickStart,
                    PickEndWorld = _pickA.PickEndWorld
                };

                var bPick = new TurnEditTrim.Pick
                {
                    SegIndex = _pickB.SegIndex,
                    Seg = MakeSegView(_pickB.Seg),
                    PickStart = _pickB.PickStart,
                    PickEndWorld = _pickB.PickEndWorld
                };

                var host = new TrimHost(this);

                if (!TurnEditTrim.Run(aPick, bPick, host, out int replaceIdx, out string? line, out string status))
                {
                    if (!string.IsNullOrWhiteSpace(status))
                        UpdateUiState(status);
                    return;
                }

                if (replaceIdx < 0 || replaceIdx >= _editSegs.Count || string.IsNullOrWhiteSpace(line))
                {
                    UpdateUiState("Trim: invalid tool return.");
                    return;
                }

                // Preserve original style for replaced segment
                Brush stroke = _pickA.Seg.Stroke;
                double thick = _pickA.Seg.Thickness;

                if (!TryReplaceSegFromRegionInputLine(replaceIdx, line, stroke, thick, out string err))
                {
                    UpdateUiState("Trim replace failed: " + err);
                    return;
                }

                ClearPreviewOnly();

                _pickA = null;
                _pickB = null;

                // Rebuild chain deterministically using EditTol.
                bool chainedOk = TryChainAndSnap(_editSegs, editTol,
                    out List<EditSeg> ordered,
                    out double maxGap,
                    out bool isClosed,
                    out Point failWorld,
                    out string failReason);

                if (chainedOk)
                {
                    _editSegs = ordered;

                    RenderEditGeometry(_editSegs);
                    ApplySelectionVisuals();
                    DrawPickMarkers();

                    ShowChainDiagnosticsIfEnabled(
                        "Turn Editor: Chain Diagnostics (Trim)",
                        "Trim",
                        beforeCount,
                        _editSegs.Count,
                        sewTol,
                        editTol,
                        maxGap,
                        isClosed);

                    UpdateUiState("Trim: applied + re-chained.");
                    return;
                }

                // Trim applied, but chain failed: render current geometry and show diagnostics.
                RenderEditGeometry(_editSegs);
                ApplySelectionVisuals();
                DrawPickMarkers();
                DrawFailureStarAtWorldPoint(failWorld);

                ShowChainDiagnosticsIfEnabled(
                    "Turn Editor: Chain Diagnostics (Trim failed)",
                    failReason,
                    beforeCount,
                    _editSegs.Count,
                    sewTol,
                    editTol,
                    maxGap,
                    false);

                UpdateUiState("Trim: applied but chain failed (see diagnostics).");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Turn Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private sealed class TrimHost : TurnEditTrim.IHostPolyline
        {
            private readonly TurnEditWindow _w;

            public TrimHost(TurnEditWindow w)
            {
                _w = w;
            }

            public bool MapValid => _w._renderer.MapValid;

            public void ClearPreviewOnly() => _w._renderer.ClearPreviewOnly();

            public void DrawPreviewPointWorld(Point worldPt, Brush fill, double diamPx, double opacity) =>
                _w._renderer.DrawPreviewPointWorld(worldPt, fill, diamPx, opacity);

            public void DrawPreviewPolylineWorld(IReadOnlyList<Point> worldPts, Brush stroke, double thickness, double opacity) =>
                _w._renderer.DrawPreviewPolylineWorld(worldPts, stroke, thickness, opacity);
        }









        // Single replace helper (ONE place that interprets region-input text into EditSeg and updates list)
        private bool TryReplaceSegFromRegionInputLine(int index, string line, Brush stroke, double thickness, out string error)
        {
            error = "";

            if (index < 0 || index >= _editSegs.Count)
            {
                error = "Index out of range.";
                return false;
            }

            if (!TurnEditSegsLoad.TryParseRegionInputLineToSegDto(line, stroke, thickness, out var dto, out error))
                return false;

            if (dto == null)
            {
                error = "DTO null.";
                return false;
            }

            EditSeg newSeg = ConvertOneDtoToEditSeg(dto);
            _editSegs[index] = newSeg;
            return true;
        }

        private EditSeg ConvertOneDtoToEditSeg(TurnEditSegsLoad.SegDto dto)
        {
            if (dto.Kind == TurnEditSegsLoad.SegKind.Line)
            {
                return new EditLineSeg
                {
                    Stroke = dto.Stroke ?? Brushes.White,
                    Thickness = dto.Thickness,
                    A = dto.A,
                    B = dto.B
                };
            }

            return new EditArcSeg
            {
                Stroke = dto.Stroke ?? Brushes.White,
                Thickness = dto.Thickness,
                A = dto.A,
                M = dto.M,
                B = dto.B,
                C = dto.C,
                CCW = dto.CCW
            };
        }



        private Brush GetGraphicTextBrush()
        {
            // Uses your app setting color for on-screen info text
            return BrushFromHex(NormalizeHexColor(Settings.Default.GraphicTextColor), Brushes.LightGray);
        }

        private void SetStatusBlocksSingleLine(string status)
        {
            Brush fg = GetGraphicTextBrush();

            // TxtStatus
            TxtStatus.Inlines.Clear();
            TxtStatus.Inlines.Add(new Run(status ?? "") { Foreground = fg });
            TxtStatus.Inlines.Add(new LineBreak());
            //TxtStatus.Inlines.Add(new Run("Zoom mouse wheel") { Foreground = fg });

            // TxtFooter (same as status)
            TxtFooter.Inlines.Clear();
            TxtFooter.Inlines.Add(new Run(status ?? "") { Foreground = fg });
            TxtFooter.Inlines.Add(new LineBreak());
            //TxtFooter.Inlines.Add(new Run("Zoom mouse wheel") { Foreground = fg });
        }

        private void RenderSelectionStatusHistory()
        {
            Brush fgNew = GetGraphicTextBrush();
            Brush fgOld = Brushes.Gray;

            // Build line list from queue
            var lines = _statusHistory.ToList();

            // TxtStatus
            TxtStatus.Inlines.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                bool isTop = (i == 0);
                TxtStatus.Inlines.Add(new Run(lines[i]) { Foreground = isTop ? fgNew : fgOld });

                TxtStatus.Inlines.Add(new LineBreak());
            }
            //TxtStatus.Inlines.Add(new Run("Zoom mouse wheel") { Foreground = fgNew });

            // TxtFooter (same as status)
            TxtFooter.Inlines.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                bool isTop = (i == 0);
                TxtFooter.Inlines.Add(new Run(lines[i]) { Foreground = isTop ? fgNew : fgOld });
                TxtFooter.Inlines.Add(new LineBreak());
            }
            //TxtFooter.Inlines.Add(new Run("Zoom mouse wheel") { Foreground = fgNew });
        }


        // Place a dialog window at the bottom-right of its owner (with a small margin).
        private static void PositionDialogBottomRight(Window dlg, Window? owner, double marginPx = 16.0)
        {
            try
            {
                if (dlg == null) return;

                // If no owner, use the main window if available.
                owner ??= Application.Current?.MainWindow;

                if (owner == null) return;

                // Ensure we have real sizes.
                double ow = owner.ActualWidth;
                double oh = owner.ActualHeight;

                // Fallback if ActualWidth/Height not ready.
                if (!TurnEditMath.IsFinite(ow) || ow <= 1) ow = owner.Width;
                if (!TurnEditMath.IsFinite(oh) || oh <= 1) oh = owner.Height;

                double dw = dlg.Width;
                double dh = dlg.Height;

                // Bottom-right inside owner
                double left = owner.Left + ow - dw - marginPx;
                double top = owner.Top + oh - dh - marginPx;

                if (!TurnEditMath.IsFinite(left) || !TurnEditMath.IsFinite(top))
                    return;

                dlg.Left = left;
                dlg.Top = top;
            }
            catch
            {
                // never crash because of positioning
            }
        }






    }
}








