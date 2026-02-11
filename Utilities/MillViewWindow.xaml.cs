using CNC_Improvements_gcode_solids.Pages; // for MillPage.ColorList
using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FillRule = Clipper2Lib.FillRule;
// IMPORTANT: avoid collision with your CNC_Improvements_gcode_solids.Geometry namespace
using WpfGeometry = System.Windows.Media.Geometry;

namespace CNC_Improvements_gcode_solids.Utilities
{
    public partial class MillViewWindow : Window
    {


        // ONE place for these constants (stop the drift)
        private const double CLIPPER_SCALE = 1_000_000_000.0;
        private const double DEDUPE_SCALE = 1_000_000.0;
        private static double CLIPPER_CHORD_TOL = .001;   // world units
        private static double SNAP_TOL = .1;   // world units
        private static int MIN_ARC_POINTS = 10;

        // private bool _cancelTransformForView = false; 


        private Window? _clipperVertexLogWindow = null;


        private Window? _arcTableLogWindow = null;
        // TRUE SHAPE: combined log buffers (single LogWindow output)
        private readonly System.Text.StringBuilder sbArcList = new System.Text.StringBuilder(64 * 1024);
        private readonly System.Text.StringBuilder sbArcFit = new System.Text.StringBuilder(256 * 1024);
        private readonly System.Text.StringBuilder sbPythonFreecadInput = new System.Text.StringBuilder(256 * 1024);

        private readonly System.Text.StringBuilder sbClipperVertices = new System.Text.StringBuilder(256 * 1024);


        private sealed class ParsedLoop
        {
            public int LoopIndex;
            public string Kind = ""; // "OUTER" or "ISLAND"
            public List<ParsedVertex> Verts = new();
            public List<ParsedArcRun> Arcs = new(); // in discovery order
        }

        private sealed class ParsedVertex
        {
            public int Vid;
            public double X;
            public double Y;
        }

        private sealed class ParsedArcRun
        {
            public int ArcNo;
            public double R;
            public int StartVid;
            public int MidVid;
            public int EndVid;
        }

        public void RemUnusableButtons(bool visible)

        {
            BtnClipperD.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            BtnTrueD.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }



        // Vertex cleanup: remove consecutive identical points coming out of Clipper union.
        // Keep epsilon tiny so we only remove true duplicates from integer scaling/union artifacts.
        private const double LOOP_DEDUPE_EPS = 1.0 / CLIPPER_SCALE;

        private static bool PointsNear(Point a, Point b, double eps)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return (dx * dx + dy * dy) <= (eps * eps);
        }
        private sealed class ClipperUnionLoop
        {
            public int LoopIndex;
            public bool IsHole;                 // "island"
            public double SignedAreaScaled;     // signed area in scaled coordinate units^2 (sign only used)
            public List<Point> WorldPts = new(); // world coords (double)
        }









        // ------------------------------------------
        // Public model used by MillPage
        // ------------------------------------------
        public sealed class PathSeg
        {
            public int Index { get; set; }
            public string Type { get; set; } = ""; // "LINE", "ARC3_CW", "ARC3_CCW"

            public double X1 { get; set; }
            public double Y1 { get; set; }
            public double Xm { get; set; } // arcs only
            public double Ym { get; set; } // arcs only
            public double X2 { get; set; }
            public double Y2 { get; set; }

            // NEW: per-segment tool diameter (used by VIEW ALL so each region can differ)
            // If NaN/invalid => viewer falls back to constructor toolDia.
            public double ToolDia { get; set; } = double.NaN;

            // ------------------------------------------------------------
            // viewer-only transform metadata (passed in from MillPage)
            // ------------------------------------------------------------
            public string RegionName { get; set; } = "";
            public string MatrixName { get; set; } = "";

            // Convention: RotZ is CW-positive (your rule)
            public double RotZDeg { get; set; } = 0.0;

            // Only applied if 180 (mirror about vertical Y axis): x -> -x
            public double RotYDeg { get; set; } = 0.0;

            // Kept for logging / future (NOT applied in viewer yet per your rule)
            public double Tx { get; set; } = 0.0;
            public double Ty { get; set; } = 0.0;
            public double Tz { get; set; } = 0.0;


            public Brush RegionColor { get; set; } = Brushes.Gray;

            // NEW: wire interpretation flags (per SET; repeated on each seg for convenience)
            public bool IsClosedWire { get; set; } = false;     // Closed CL Wire mode
            public bool ClosedWireInner { get; set; } = false;  // Pocket fill
            public bool ClosedWireOuter { get; set; } = false;  // Outer box minus profile fill


        }

        private double ToolRadWorldForSeg(PathSeg s)
        {
            if (s != null && double.IsFinite(s.ToolDia) && s.ToolDia > 0.0)
                return s.ToolDia * 0.5;

            // fallback = constructor tool dia
            return _toolRad;
        }


        // ------------------------------------------
        // Per-canvas zoom/pan container
        // ------------------------------------------
        private sealed class Viewport
        {
            public readonly ScaleTransform Scale = new ScaleTransform(1.0, 1.0);
            public readonly TranslateTransform Translate = new TranslateTransform();
            public readonly TransformGroup Group = new TransformGroup();

            public Viewport()
            {
                Group.Children.Add(Scale);
                Group.Children.Add(Translate);
            }

            public void Reset()
            {
                Scale.ScaleX = 1.0;
                Scale.ScaleY = 1.0;
                Translate.X = 0.0;
                Translate.Y = 0.0;
            }

            public void ZoomAtPoint(Point canvasPoint, int wheelDelta)
            {
                const double zoomStep = 1.2;
                double factor = (wheelDelta > 0) ? zoomStep : (1.0 / zoomStep);

                double newScaleX = Scale.ScaleX * factor;
                double newScaleY = Scale.ScaleY * factor;

                const double minScale = 0.1;
                const double maxScale = 50.0;
                if (newScaleX < minScale || newScaleX > maxScale)
                    return;

                double oldScaleX = Scale.ScaleX;
                double oldScaleY = Scale.ScaleY;

                Scale.ScaleX = newScaleX;
                Scale.ScaleY = newScaleY;

                Translate.X = (oldScaleX - newScaleX) * canvasPoint.X + Translate.X;
                Translate.Y = (oldScaleY - newScaleY) * canvasPoint.Y + Translate.Y;
            }

            public Point InverseTransformPoint(Point p)
            {
                double sx = Scale.ScaleX;
                double sy = Scale.ScaleY;
                if (Math.Abs(sx) < 1e-12) sx = 1e-12;
                if (Math.Abs(sy) < 1e-12) sy = 1e-12;

                return new Point(
                    (p.X - Translate.X) / sx,
                    (p.Y - Translate.Y) / sy);
            }
        }

        private sealed class RenderItem
        {
            public PathSeg Seg = new PathSeg();
            public WpfGeometry Geo = WpfGeometry.Empty;
            public Path Path = new Path();

            // per-item normal style (THIS is the fix)
            public Brush FillNormal = Brushes.Transparent;
            public Brush StrokeNormal = Brushes.Transparent;

            public bool IsArc;
            public bool IsBand;                // arc only
            public double ArcR;                // world radius (arc only)
            public Point ArcCenterWorld;       // arc only
        }


        // ------------------------------------------
        // Inputs
        // ------------------------------------------
        private readonly double _toolDia;
        private readonly double _toolLen;
        private readonly double _zPlane;
        private readonly double _toolRad;
        // Display-only values (VIEW ALL wants these NaN, but geometry still uses real toolDia)
        private double _dispToolDia;
        private double _dispToolLen;
        private double _dispZPlane;


        private List<PathSeg> _segsRaw;
        private List<PathSeg> _segs;   // transformed-for-view copy (viewer-only)



        // ------------------------------------------
        // Render / interaction state
        // ------------------------------------------
        private readonly Viewport _vp = new Viewport();
        private readonly List<RenderItem> _items = new();
        private int _selectedIndex = -1;

        private bool _didInitialFit = false;
        private bool _showLabels = true;

        // mapping world->base-screen
        private double _minX, _maxY, _scale, _margin;
        private double _centerOffsetX; // extra screen-space offset to horizontally center the extents

        // ------------------------------------------
        // Styles (defaults; then overwritten from Settings)
        // ------------------------------------------
        private Brush _fillNormal = new SolidColorBrush(Color.FromArgb(90, 0, 255, 0));
        private static readonly Brush FillSelected = new SolidColorBrush(Color.FromArgb(110, 255, 165, 0));

        private Brush _strokeNormal = new SolidColorBrush(Color.FromArgb(220, 0, 255, 0));
        private static readonly Brush StrokeSelected = DebugPalette.StrokeSelected;

        private static readonly Brush DetailGrey = new SolidColorBrush(Color.FromRgb(190, 190, 190));

        // Used for TextBlocks drawn into the CANVAS (labels/origin)
        private Brush _graphicText = GraphicsPalette.GraphicTextBrush;

        // Settings-driven stroke widths
        private double _profileWidth = Settings.Default.ProfileWidth;

        // NEW: CL overlay style (settings-driven)
        private Brush _clStroke = GraphicsPalette.CLBrush;
        private double _clWidth = 2.0;

        // NEW: Closing fill (settings-driven)
        private Brush _closingFill = GraphicsPalette.ClosingBrush;

        // NEW: CL toggle state (GuidedTool only; ClosedWire forces CL on)
        private bool _showCL = false;

        // NEW: avoid spamming the same ClosedWire diagnostic repeatedly
        private readonly HashSet<string> _closedWireDiagShown = new HashSet<string>(StringComparer.Ordinal);





        //-----------------------------------------------------------------------
        public MillViewWindow(double toolDia, double toolLen, double zPlane, List<PathSeg> segs)
        {
            InitializeComponent();
            ApplyViewerStylesFromSettings();

            TxtSummary.Foreground = _graphicText;

            CLIPPER_CHORD_TOL = Settings.Default.ClipperInputPolyTol;    // world units
            SNAP_TOL = Settings.Default.SnapRad;                         // world units
            MIN_ARC_POINTS = Settings.Default.MinArcPoints;

            _toolDia = toolDia;
            _toolLen = toolLen;
            _zPlane = zPlane;

            // Display defaults to what was passed, but can be overridden for VIEW ALL.
            _dispToolDia = toolDia;
            _dispToolLen = toolLen;
            _dispZPlane = zPlane;

            // Geometry must still render even if display is NaN.
            _toolRad = double.IsFinite(toolDia) ? toolDia * 0.5 : 0.0;

            _segsRaw = segs ?? new List<PathSeg>();
            _segs = TransformSegmentsForView(_segsRaw, false);  // viewer-only transform applied here

            TopCanvas.RenderTransform = _vp.Group;
            TopCanvas.RenderTransformOrigin = new Point(0, 0);

            // NEW: CL toggle default (GuidedTool only; ClosedWire forces CL on in Render())
            _showCL = false;
            _closedWireDiagShown.Clear();

            Loaded += MillViewWindow_Loaded;
            SizeChanged += MillViewWindow_SizeChanged;

            SyncToggleButtonText();
            UpdateSummary();
            UpdateInfoPanel();
        }




        public void SetDisplayParams(double toolDia, double toolLen, double zPlane)
        {
            _dispToolDia = toolDia;
            _dispToolLen = toolLen;
            _dispZPlane = zPlane;

            UpdateSummary();
            UpdateInfoPanel();
        }




        private void ApplyViewerStylesFromSettings()
        {
            // ProfileWidth drives the outline thickness
            try
            {
                _profileWidth = Settings.Default.ProfileWidth;
            }
            catch
            {
                _profileWidth = 1.2;
            }

            if (!double.IsFinite(_profileWidth) || _profileWidth <= 0.0)
                _profileWidth = 1.2;

            // GraphicTextColor drives InfoText + canvas labels
            try { _graphicText = GraphicsPalette.GraphicTextBrush; }
            catch { _graphicText = Brushes.Yellow; }

            // CL stroke + width
            try { _clStroke = GraphicsPalette.CLBrush; }
            catch { _clStroke = Brushes.Magenta; }

            try
            {
                _clWidth = Settings.Default.CLWidth;
            }
            catch
            {
                _clWidth = 2.0;
            }

            if (!double.IsFinite(_clWidth) || _clWidth <= 0.0)
                _clWidth = 2.0;

            // Closing fill colour
            try { _closingFill = GraphicsPalette.ClosingBrush; }
            catch { _closingFill = Brushes.Gray; }

            if (InfoText != null)
                InfoText.Foreground = _graphicText;

            



        }


        private static void BuildRegionBrushes(Brush regionColor, out Brush fill, out Brush stroke)
        {
            // Defaults (green) if anything goes wrong
            Brush defFill = new SolidColorBrush(Color.FromArgb(90, 0, 255, 0));
            Brush defStroke = new SolidColorBrush(Color.FromArgb(220, 0, 255, 0));
            if (defFill.CanFreeze) defFill.Freeze();
            if (defStroke.CanFreeze) defStroke.Freeze();

            if (regionColor == null)
            {
                fill = defFill;
                stroke = defStroke;
                return;
            }

            try
            {
                if (regionColor is SolidColorBrush scb)
                {
                    var c = scb.Color;

                    var f = new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B));
                    var s = new SolidColorBrush(Color.FromArgb(220, c.R, c.G, c.B));

                    if (f.CanFreeze) f.Freeze();
                    if (s.CanFreeze) s.Freeze();

                    fill = f;
                    stroke = s;
                    return;
                }

                // Non-solid brush (gradient, etc.) -> clone so opacity changes don't affect shared instances
                var f2 = regionColor.CloneCurrentValue();
                var s2 = regionColor.CloneCurrentValue();

                f2.Opacity = 0.35;   // similar strength to alpha 90
                s2.Opacity = 0.85;   // similar strength to alpha 220

                if (f2.CanFreeze) f2.Freeze();
                if (s2.CanFreeze) s2.Freeze();

                fill = f2;
                stroke = s2;
            }
            catch
            {
                fill = defFill;
                stroke = defStroke;
            }
        }


        private void MillViewWindow_Loaded(object sender, RoutedEventArgs e)
        {
            FitAndRender();
        }

        private void MillViewWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_didInitialFit)
                FitAndRender();
        }

        // ------------------------------------------
        // Toolbar
        // ------------------------------------------
        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            FitAndRender();
        }
        private void BtnToggleCL_Click(object sender, RoutedEventArgs e)
        {
            _showCL = !_showCL;
            SyncToggleButtonText();
            Render();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _vp.Reset();
            Render();
        }
        private bool HasAnyClosedWire()
        {
            if (_segs == null || _segs.Count == 0) return false;
            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];
                if (s != null && s.IsClosedWire)
                    return true;
            }
            return false;
        }

        private void ShowClosedWireButtonsNotUsedMessage()
        {
            MessageBox.Show(
                "Closed CL Wire mode is active.\n\nThe Raw / Pre-Clipper / Clipper / Candidate / TrueShape displays are not used for Closed CL Wire.\nClosed CL Wire always displays the CL wire (and inner/outer shading when closed).",
                "Closed CL Wire",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }


        private void BtnToggleLabels_Click(object sender, RoutedEventArgs e)
        {
            _showLabels = !_showLabels;
            SyncToggleButtonText();
            Render();
        }

        private void SyncToggleButtonText()
        {
            if (BtnToggleLabels != null)
                BtnToggleLabels.Content = _showLabels ? "Labels: OFF" : "Labels: ON";

            if (BtnToggleCL != null)
                BtnToggleCL.Content = _showCL ? "CL: ON" : "CL: OFF";
        }



        // ------------------------------------------
        // Mouse
        // ------------------------------------------
        private void TopCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Point p = e.GetPosition(TopCanvas);
            _vp.ZoomAtPoint(p, e.Delta);
            e.Handled = true;
        }

        private void TopCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            TopCanvas.Focus();

            if (_items.Count == 0)
                return;

            Point pCanvas = e.GetPosition(TopCanvas);
            Point pBase = _vp.InverseTransformPoint(pCanvas);

            int best = -1;
            double bestArea = double.MaxValue;

            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];

                if (!it.Geo.Bounds.Contains(pBase))
                    continue;

                if (!it.Geo.FillContains(pBase))
                    continue;

                double area = it.Geo.Bounds.Width * it.Geo.Bounds.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = it.Seg.Index;
                }
            }

            if (best >= 0)
            {
                _selectedIndex = best;
                ApplySelectionStyles();
                UpdateSummary();
                UpdateInfoPanel();
            }
        }

        // ------------------------------------------
        // Render pipeline
        // ------------------------------------------
        private void FitAndRender()
        {
            _vp.Reset();
            ComputeWorldFit();
            Render();
            _didInitialFit = true;
        }

        private void ComputeWorldFit()
        {
            double w = TopCanvas.ActualWidth;
            double h = TopCanvas.ActualHeight;
            if (w < 10) w = 900;
            if (h < 10) h = 600;

            _margin = 30.0;
            _centerOffsetX = 0.0;

            if (_segs.Count == 0)
            {
                _minX = 0;
                _maxY = 0;
                _scale = 1;
                return;
            }

            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;

            foreach (var s in _segs)
            {
                double toolRadW = ToolRadWorldForSeg(s);

                if (s.Type == "LINE")
                {
                    double sx = Math.Min(s.X1, s.X2) - toolRadW;
                    double ex = Math.Max(s.X1, s.X2) + toolRadW;
                    double sy = Math.Min(s.Y1, s.Y2) - toolRadW;
                    double ey = Math.Max(s.Y1, s.Y2) + toolRadW;

                    minX = Math.Min(minX, sx);
                    maxX = Math.Max(maxX, ex);
                    minY = Math.Min(minY, sy);
                    maxY = Math.Max(maxY, ey);
                }
                else if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                {
                    if (TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2, out double cx, out double cy, out double r))
                    {
                        double outerR = r + toolRadW;
                        minX = Math.Min(minX, cx - outerR);
                        maxX = Math.Max(maxX, cx + outerR);
                        minY = Math.Min(minY, cy - outerR);
                        maxY = Math.Max(maxY, cy + outerR);
                    }
                    else
                    {
                        double sx = Math.Min(s.X1, s.X2) - toolRadW;
                        double ex = Math.Max(s.X1, s.X2) + toolRadW;
                        double sy = Math.Min(s.Y1, s.Y2) - toolRadW;
                        double ey = Math.Max(s.Y1, s.Y2) + toolRadW;

                        minX = Math.Min(minX, sx);
                        maxX = Math.Max(maxX, ex);
                        minY = Math.Min(minY, sy);
                        maxY = Math.Max(maxY, ey);
                    }
                }
            }

            if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
            {
                minX = 0; maxX = 1; minY = 0; maxY = 1;
            }

            double rangeX = maxX - minX; if (rangeX <= 1e-9) rangeX = 1;
            double rangeY = maxY - minY; if (rangeY <= 1e-9) rangeY = 1;

            double availW = w - 2 * _margin;
            double availH = h - 2 * _margin;

            double sxFit = availW / rangeX;
            double syFit = availH / rangeY;
            _scale = Math.Min(sxFit, syFit);

            _minX = minX;
            _maxY = maxY;

            // horizontally center
            double contentW = rangeX * _scale;
            double leftoverW = availW - contentW;
            _centerOffsetX = (leftoverW > 0) ? leftoverW * 0.5 : 0.0;
        }


        private Point W2S(double x, double y)
        {
            double sx = (x - _minX) * _scale + _margin + _centerOffsetX;
            double sy = (_maxY - y) * _scale + _margin;
            return new Point(sx, sy);
        }

        private double WR2SR(double rWorld) => rWorld * _scale;



        private static bool WorldNear(double x1, double y1, double x2, double y2, double tol)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return (dx * dx + dy * dy) <= (tol * tol);
        }

        private bool TryBuildClosedWireBoundaryPointsWorld(
    List<PathSeg> segs,
    out List<Point> ptsWorld,
    out string failReason)
        {
            // ClosedWire = must be a single chain AND must close.
            return TryBuildWireDisplayPointsWorld(segs, requireClosedLoop: true, out ptsWorld, out failReason);
        }



        // Same sampling/display logic as Closed CL Wire, but does NOT require the loop to close.
        // Used for GuidedTool CL overlay so arcs render correctly (not endpoint chords).
        private bool TryBuildWireDisplayPointsWorld(
            List<PathSeg> segs,
            bool requireClosedLoop,
            out List<Point> ptsWorld,
            out string failReason)
        {
            ptsWorld = new List<Point>();
            failReason = "";

            if (segs == null || segs.Count == 0)
            {
                failReason = "Empty path.";
                return false;
            }

            // Verify single chained path in listed order
            double curX = segs[0].X1;
            double curY = segs[0].Y1;
            ptsWorld.Add(new Point(curX, curY));

            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];

                if (!WorldNear(curX, curY, s.X1, s.Y1, SNAP_TOL))
                {
                    failReason = $"Path is not a single chained wire (break at seg {s.Index}).";
                    return false;
                }

                if (s.Type == "LINE")
                {
                    ptsWorld.Add(new Point(s.X2, s.Y2));
                    curX = s.X2;
                    curY = s.Y2;
                    continue;
                }

                if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                {
                    if (!TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2, out double cx, out double cy, out double r))
                    {
                        failReason = $"Arc circle solve failed (seg {s.Index}).";
                        return false;
                    }

                    bool ccw = (s.Type == "ARC3_CCW");

                    double a1 = Math.Atan2(s.Y1 - cy, s.X1 - cx);
                    double a2 = Math.Atan2(s.Y2 - cy, s.X2 - cx);

                    double sweep = ccw ? CcwDelta(a1, a2) : -CcwDelta(a2, a1);
                    double sweepAbs = Math.Abs(sweep);

                    int divs = DivsForArcByChordTol(r, sweepAbs, CLIPPER_CHORD_TOL, minDivs: Math.Max(8, MIN_ARC_POINTS), maxDivs: 5000);
                    if (divs < 2) divs = 2;

                    // Add sampled points excluding the first (already in ptsWorld)
                    for (int k = 1; k <= divs; k++)
                    {
                        double ang = a1 + sweep * k / divs;
                        double x = cx + Math.Cos(ang) * r;
                        double y = cy + Math.Sin(ang) * r;
                        ptsWorld.Add(new Point(x, y));
                    }

                    curX = s.X2;
                    curY = s.Y2;
                    continue;
                }

                failReason = $"Unsupported seg type: {s.Type} (seg {s.Index}).";
                return false;
            }

            if (requireClosedLoop)
            {
                // Must be closed
                var first = ptsWorld[0];
                var last = ptsWorld[ptsWorld.Count - 1];
                if (!WorldNear(first.X, first.Y, last.X, last.Y, SNAP_TOL))
                {
                    failReason = "Loop not closed.";
                    return false;
                }

                // Force exact closure for WPF fill stability
                ptsWorld[ptsWorld.Count - 1] = first;
            }

            return true;
        }


        private void ShowClosedWireNotClosedDiagOnce(string regionName, string extraReason)
        {
            string key = regionName ?? "";
            if (_closedWireDiagShown.Contains(key))
                return;

            _closedWireDiagShown.Add(key);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"MILL set \"{regionName}\": Closed CL Wire selected but loop is not closed.");
            if (!string.IsNullOrWhiteSpace(extraReason))
                sb.AppendLine(extraReason.Trim());
            sb.AppendLine("This path can only use GuidedTool.");
            sb.AppendLine("No pocket/outer shading generated.");

            string msg = sb.ToString();

            // Always notify the user. LogWindow is optional; MessageBox is mandatory.
            MessageBox.Show(msg, "Closed CL Wire : Not Closed", MessageBoxButton.OK, MessageBoxImage.Warning);

          //if (Settings.Default.LogWindowShow)
            //{
              //  var win = new LogWindow("Closed CL Wire : Not Closed", msg) { Owner = this };
               // win.Show();
            //}
        }



        private void DrawCLPolyline(List<Point> ptsWorld, bool forceOn)
        {
            if (ptsWorld == null || ptsWorld.Count < 2)
                return;

            bool draw = forceOn || _showCL;
            if (!draw)
                return;

            var pl = new Polyline
            {
                Stroke = _clStroke,
                StrokeThickness = Math.Max(0.5, _clWidth),
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };

            for (int i = 0; i < ptsWorld.Count; i++)
                pl.Points.Add(W2S(ptsWorld[i].X, ptsWorld[i].Y));

            TopCanvas.Children.Add(pl);
        }

        private void RenderClosedWireFillInner(List<Point> ptsWorld)
        {
            var poly = new Polygon
            {
                Stroke = Brushes.Transparent,
                StrokeThickness = 0,
                Fill = _closingFill,
                Opacity = 0.35,
                IsHitTestVisible = false
            };

            for (int i = 0; i < ptsWorld.Count; i++)
                poly.Points.Add(W2S(ptsWorld[i].X, ptsWorld[i].Y));

            TopCanvas.Children.Add(poly);
        }

        private void RenderClosedWireFillOuter(List<Point> ptsWorldTransformedProfile, List<PathSeg> segsRawForRegion, double rotZDeg, double rotYDeg)
        {
            if (ptsWorldTransformedProfile == null || ptsWorldTransformedProfile.Count < 3)
                return;

            if (segsRawForRegion == null || segsRawForRegion.Count == 0)
                return;

            // ------------------------------------------------------------
            // 1) Compute bounds in RAW space (before transform)
            //    Include arcs by sampling (so bounds match the real shape).
            // ------------------------------------------------------------
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;

            for (int i = 0; i < segsRawForRegion.Count; i++)
            {
                var s = segsRawForRegion[i];
                if (s == null) continue;

                minX = Math.Min(minX, Math.Min(s.X1, s.X2));
                maxX = Math.Max(maxX, Math.Max(s.X1, s.X2));
                minY = Math.Min(minY, Math.Min(s.Y1, s.Y2));
                maxY = Math.Max(maxY, Math.Max(s.Y1, s.Y2));

                if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                {
                    if (TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2, out double cx, out double cy, out double r))
                    {
                        bool ccw = (s.Type == "ARC3_CCW");

                        double a1 = Math.Atan2(s.Y1 - cy, s.X1 - cx);
                        double a2 = Math.Atan2(s.Y2 - cy, s.X2 - cx);

                        double sweep = ccw ? CcwDelta(a1, a2) : -CcwDelta(a2, a1);
                        double sweepAbs = Math.Abs(sweep);

                        int divs = DivsForArcByChordTol(r, sweepAbs, CLIPPER_CHORD_TOL, minDivs: Math.Max(8, MIN_ARC_POINTS), maxDivs: 2000);
                        if (divs < 2) divs = 2;

                        for (int k = 0; k <= divs; k++)
                        {
                            double ang = a1 + sweep * k / divs;
                            double x = cx + Math.Cos(ang) * r;
                            double y = cy + Math.Sin(ang) * r;

                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }
            }

            if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
                return;

            const double PAD = 100.0;

            double l = minX - PAD, r2 = maxX + PAD, b = minY - PAD, t = maxY + PAD;

            // ------------------------------------------------------------
            // 2) Build RAW box corners in requested order, then TRANSFORM them
            //    (so display box is defined BEFORE the transform matrix).
            // ------------------------------------------------------------
            bool mirrorX = IsRotY180(rotYDeg);

            TransformPoint2D(r2, t, rotZDeg, mirrorX, out double p1x, out double p1y); // (maxX,maxY)
            TransformPoint2D(r2, b, rotZDeg, mirrorX, out double p2x, out double p2y); // (maxX,minY)
            TransformPoint2D(l, b, rotZDeg, mirrorX, out double p3x, out double p3y); // (minX,minY)
            TransformPoint2D(l, t, rotZDeg, mirrorX, out double p4x, out double p4y); // (minX,maxY)

            // Box figure (TRANSFORMED world coords -> screen)
            var rectFig = new PathFigure { StartPoint = W2S(p1x, p1y), IsClosed = true, IsFilled = true };
            rectFig.Segments.Add(new LineSegment(W2S(p2x, p2y), true));
            rectFig.Segments.Add(new LineSegment(W2S(p3x, p3y), true));
            rectFig.Segments.Add(new LineSegment(W2S(p4x, p4y), true));
            rectFig.Segments.Add(new LineSegment(W2S(p1x, p1y), true));

            // Profile figure (already TRANSFORMED)
            var profFig = new PathFigure
            {
                StartPoint = W2S(ptsWorldTransformedProfile[0].X, ptsWorldTransformedProfile[0].Y),
                IsClosed = true,
                IsFilled = true
            };
            for (int i = 1; i < ptsWorldTransformedProfile.Count; i++)
                profFig.Segments.Add(new LineSegment(W2S(ptsWorldTransformedProfile[i].X, ptsWorldTransformedProfile[i].Y), true));

            // EvenOdd fill: box minus profile
            var pg = new PathGeometry(new[] { rectFig, profFig })
            {
                FillRule = System.Windows.Media.FillRule.EvenOdd
            };

            // Fill (closing color)
            var fillPath = new Path
            {
                Data = pg,
                Fill = _closingFill,
                Opacity = 0.35,
                Stroke = Brushes.Transparent,
                StrokeThickness = 0,
                IsHitTestVisible = false
            };
            TopCanvas.Children.Add(fillPath);

            // Box stroke in ProfileColor
            Brush boxStroke;
            try { boxStroke = UiUtilities.HexBrush(Settings.Default.ProfileColor); }
            catch { boxStroke = Brushes.Magenta; }

            // Outline only (reuse rectFig geometry)
            var rectOnly = new PathGeometry(new[] { rectFig.Clone() })
            {
                FillRule = System.Windows.Media.FillRule.Nonzero
            };

            var strokePath = new Path
            {
                Data = rectOnly,
                Fill = Brushes.Transparent,
                Stroke = boxStroke,
                StrokeThickness = Math.Max(0.5, _profileWidth),
                Opacity = 1.0,
                IsHitTestVisible = false
            };
            TopCanvas.Children.Add(strokePath);
        }









        private void Render()
        {
            TopCanvas.Children.Clear();
            _items.Clear();

            if (_segs == null || _segs.Count == 0)
            {
                UpdateSummary();
                UpdateInfoPanel();
                return;
            }

            ApplyViewerStylesFromSettings();
            DrawOriginIfInView();

            // Group TRANSFORMED segs by RegionName (preserve first-seen order)
            var order = new List<string>();
            var groups = new Dictionary<string, List<PathSeg>>(StringComparer.Ordinal);

            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];
                string key = s.RegionName ?? "";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<PathSeg>();
                    groups[key] = list;
                    order.Add(key);
                }
                list.Add(s);
            }

            // Group RAW segs by RegionName (same keys)
            var rawGroups = new Dictionary<string, List<PathSeg>>(StringComparer.Ordinal);
            if (_segsRaw != null)
            {
                for (int i = 0; i < _segsRaw.Count; i++)
                {
                    var s = _segsRaw[i];
                    if (s == null) continue;

                    string key = s.RegionName ?? "";
                    if (!rawGroups.TryGetValue(key, out var list))
                    {
                        list = new List<PathSeg>();
                        rawGroups[key] = list;
                    }
                    list.Add(s);
                }
            }

            for (int gi = 0; gi < order.Count; gi++)
            {
                string regionKey = order[gi];
                var segsGroup = groups[regionKey];
                if (segsGroup == null || segsGroup.Count == 0)
                    continue;

                bool isClosedWire = segsGroup[0].IsClosedWire;
                bool inner = segsGroup[0].ClosedWireInner;
                bool outer = segsGroup[0].ClosedWireOuter;

                if (isClosedWire)
                {
                    // ClosedWire: CL always shown; optional fill if closed
                    if (!TryBuildClosedWireBoundaryPointsWorld(segsGroup, out var ptsWorld, out string failReason))
                    {
                        ShowClosedWireNotClosedDiagOnce(regionKey, failReason);

                        // still draw CL as best-effort polyline using endpoints in order
                        var ptsFallback = new List<Point>();
                        if (segsGroup.Count > 0)
                        {
                            ptsFallback.Add(new Point(segsGroup[0].X1, segsGroup[0].Y1));
                            for (int i = 0; i < segsGroup.Count; i++)
                                ptsFallback.Add(new Point(segsGroup[i].X2, segsGroup[i].Y2));
                        }
                        DrawCLPolyline(ptsFallback, forceOn: true);
                        continue;
                    }

                    if (inner)
                    {
                        RenderClosedWireFillInner(ptsWorld);
                    }
                    else if (outer)
                    {
                        rawGroups.TryGetValue(regionKey, out var rawForRegion);
                        RenderClosedWireFillOuter(
                            ptsWorld,
                            rawForRegion ?? segsGroup,     // fallback if raw missing
                            segsGroup[0].RotZDeg,
                            segsGroup[0].RotYDeg);
                    }

                    DrawCLPolyline(ptsWorld, forceOn: true);
                    continue;
                }

                // GuidedTool (existing behaviour)
                for (int i = 0; i < segsGroup.Count; i++)
                {
                    var s = segsGroup[i];
                    double toolRadW = ToolRadWorldForSeg(s);

                    var item = new RenderItem();
                    item.Seg = s;

                    WpfGeometry geo = WpfGeometry.Empty;
                    bool isArc = false;
                    bool isBand = false;
                    double arcR = double.NaN;
                    Point arcCenterW = new Point(double.NaN, double.NaN);

                    if (s.Type == "LINE")
                    {
                        geo = BuildLineCapsuleGeometry(s, toolRadW);
                    }
                    else if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                    {
                        isArc = true;

                        if (!TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2, out double cx, out double cy, out double r))
                        {
                            var p1 = W2S(s.X1, s.Y1);
                            var p2 = W2S(s.X2, s.Y2);
                            double rad = WR2SR(toolRadW);

                            var ggFallback = new GeometryGroup { FillRule = (System.Windows.Media.FillRule)FillRule.NonZero };
                            ggFallback.Children.Add(new EllipseGeometry(p1, rad, rad));
                            ggFallback.Children.Add(new EllipseGeometry(p2, rad, rad));
                            ggFallback.Freeze();
                            geo = ggFallback;
                        }
                        else
                        {
                            arcR = r;
                            arcCenterW = new Point(cx, cy);

                            isBand = (r > toolRadW + 1e-9);
                            geo = isBand
                                ? BuildArcBandGeometry(s, cx, cy, r, toolRadW)
                                : BuildArcSmallPieGeometry(s, cx, cy, r, toolRadW);
                        }
                    }

                    item.IsArc = isArc;
                    item.IsBand = isBand;
                    item.ArcR = arcR;
                    item.ArcCenterWorld = arcCenterW;

                    item.Geo = geo;

                    BuildRegionBrushes(s.RegionColor, out Brush fill, out Brush stroke);
                    item.FillNormal = fill;
                    item.StrokeNormal = stroke;

                    var path = new Path
                    {
                        Data = geo,
                        Fill = item.FillNormal,
                        Stroke = item.StrokeNormal,
                        StrokeThickness = Math.Max(0.5, _profileWidth)
                    };

                    item.Path = path;
                    TopCanvas.Children.Add(path);

                    if (_showLabels)
                        DrawLabelForSegment(item);

                    _items.Add(item);
                }

                // GuidedTool: CL optional overlay when toggle is ON
                // IMPORTANT: use the SAME wire sampling logic as Closed CL Wire display,
                // but do NOT require closure (so arcs render as arcs, not endpoint chords).
                if (_showCL)
                {
                    if (TryBuildWireDisplayPointsWorld(segsGroup, requireClosedLoop: false, out var ptsWorld, out string _))
                    {
                        DrawCLPolyline(ptsWorld, forceOn: false);
                    }
                    else
                    {
                        // Fallback: original endpoint-only polyline (should be rare)
                        var ptsFallback = new List<Point>();
                        ptsFallback.Add(new Point(segsGroup[0].X1, segsGroup[0].Y1));
                        for (int i = 0; i < segsGroup.Count; i++)
                            ptsFallback.Add(new Point(segsGroup[i].X2, segsGroup[i].Y2));

                        DrawCLPolyline(ptsFallback, forceOn: false);
                    }
                }

            }

            ApplySelectionStyles();
            UpdateSummary();
            UpdateInfoPanel();
        }







        private void ApplySelectionStyles()
        {
            double normalThk = Math.Max(0.5, _profileWidth);
            double selThk = Math.Max(normalThk + 1.0, normalThk * 1.6);

            foreach (var it in _items)
            {
                bool sel = (it.Seg.Index == _selectedIndex);

                it.Path.Fill = sel ? FillSelected : (it.FillNormal ?? _fillNormal);
                it.Path.Stroke = sel ? StrokeSelected : (it.StrokeNormal ?? _strokeNormal);
                it.Path.StrokeThickness = sel ? selThk : normalThk;
            }
        }


        private void DrawOriginIfInView()
        {
            double invScale = 1.0 / Math.Max(_scale, 1e-9);
            double padW = 10.0 * invScale;

            double availW = TopCanvas.ActualWidth - 2 * _margin;
            double availH = TopCanvas.ActualHeight - 2 * _margin;

            double leftWorldX = _minX - (_centerOffsetX * invScale);
            double rightWorldX = leftWorldX + Math.Max(availW, 0.0) * invScale;

            double topWorldY = _maxY;
            double botWorldY = _maxY - Math.Max(availH, 0.0) * invScale;

            if (0 < (leftWorldX - padW) || 0 > (rightWorldX + padW) || 0 < (botWorldY - padW) || 0 > (topWorldY + padW))
                return;

            var o = W2S(0, 0);
            double len = 25.0;

            TopCanvas.Children.Add(new Line { X1 = o.X - len, Y1 = o.Y, X2 = o.X + len, Y2 = o.Y, Stroke = DetailGrey, StrokeThickness = 1.2 });
            TopCanvas.Children.Add(new Line { X1 = o.X, Y1 = o.Y - len, X2 = o.X, Y2 = o.Y + len, Stroke = DetailGrey, StrokeThickness = 1.2 });

            var t = new TextBlock
            {
                Text = "0,0",
                Foreground = _graphicText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11
            };
            Canvas.SetLeft(t, o.X + 6);
            Canvas.SetTop(t, o.Y + 6);
            TopCanvas.Children.Add(t);
        }

        private void DrawLabelForSegment(RenderItem it)
        {
            var p = W2S(it.Seg.X1, it.Seg.Y1);

            var label = new TextBlock
            {
                Text = it.Seg.Index.ToString(CultureInfo.InvariantCulture),
                Foreground = _graphicText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            };
            Canvas.SetLeft(label, p.X + 6);
            Canvas.SetTop(label, p.Y + 6);
            TopCanvas.Children.Add(label);
        }

        // ------------------------------------------
        // Geometries
        // ------------------------------------------


        private static Point ScaleToRadius(Point center, Point basePt, double newR)
        {
            double vx = basePt.X - center.X;
            double vy = basePt.Y - center.Y;
            double d = Math.Sqrt(vx * vx + vy * vy);
            if (d < 1e-9)
                return basePt;

            double f = newR / d;
            return new Point(center.X + vx * f, center.Y + vy * f);
        }

        private static SweepDirection Opposite(SweepDirection s)
            => (s == SweepDirection.Clockwise) ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;

        private static void GetSweep(Point c, Point p1, Point pm, Point p2, out SweepDirection sd, out bool isLargeArc)
        {
            // We compute sweep in SCREEN coords; W2S inverts Y which mirrors CW/CCW.
            // So compute normal sweep (passes through pm) then flip direction at end.

            double a1 = Math.Atan2(p1.Y - c.Y, p1.X - c.X);
            double am = Math.Atan2(pm.Y - c.Y, pm.X - c.X);
            double a2 = Math.Atan2(p2.Y - c.Y, p2.X - c.X);

            double d12_ccw = CcwDelta(a1, a2);
            double d1m_ccw = CcwDelta(a1, am);

            bool ccwPassesMid = d1m_ccw <= d12_ccw + 1e-12;

            if (ccwPassesMid)
            {
                sd = SweepDirection.Counterclockwise;
                isLargeArc = d12_ccw > Math.PI;
            }
            else
            {
                sd = SweepDirection.Clockwise;
                double d12_cw = CcwDelta(a2, a1);
                isLargeArc = d12_cw > Math.PI;
            }

            sd = Opposite(sd);
        }

        private static double CcwDelta(double aFrom, double aTo)
        {
            double d = aTo - aFrom;
            while (d < 0) d += 2.0 * Math.PI;
            while (d >= 2.0 * Math.PI) d -= 2.0 * Math.PI;
            return d;
        }

        private static bool TryCircleFrom3Points(
            double x1, double y1,
            double x2, double y2,
            double x3, double y3,
            out double cx, out double cy, out double r)
        {
            cx = cy = r = double.NaN;

            double a = x1 - x2;
            double b = y1 - y2;
            double c = x1 - x3;
            double d = y1 - y3;

            double e = ((x1 * x1 - x2 * x2) + (y1 * y1 - y2 * y2)) * 0.5;
            double f = ((x1 * x1 - x3 * x3) + (y1 * y1 - y3 * y3)) * 0.5;

            double det = a * d - b * c;
            if (Math.Abs(det) < 1e-12)
                return false;

            cx = (d * e - b * f) / det;
            cy = (-c * e + a * f) / det;

            double dx = x1 - cx;
            double dy = y1 - cy;
            r = Math.Sqrt(dx * dx + dy * dy);

            return double.IsFinite(r) && r > 0.0;
        }










        // ------------------------------------------
        // Info text
        // ------------------------------------------
        private void UpdateSummary()
        {
            var inv = CultureInfo.InvariantCulture;

            double dispToolRad = double.IsFinite(_dispToolDia) ? _dispToolDia * 0.5 : double.NaN;

            string sel = (_selectedIndex >= 0) ? $"  Selected={_selectedIndex}" : "";
            TxtSummary.Text =
                $"ToolDia={_dispToolDia.ToString("0.###", inv)}  ToolRad={dispToolRad.ToString("0.###", inv)}  " +
                $"ToolLen={_dispToolLen.ToString("0.###", inv)}  ZPlane={_dispZPlane.ToString("0.###", inv)}  " +
                $"Segments={_segs.Count}{sel}";
        }

        private void UpdateInfoPanel()
        {
            if (InfoText != null)
                InfoText.Foreground = _graphicText;

            var inv = CultureInfo.InvariantCulture;
            double dispToolRad = double.IsFinite(_dispToolDia) ? _dispToolDia * 0.5 : double.NaN;

            if (_selectedIndex < 0)
            {
                InfoText.Text =
                    $"TOOL\n" +
                    $"Dia={_dispToolDia.ToString("0.###", inv)}  Rad={dispToolRad.ToString("0.###", inv)}\n" +
                    $"Len={_dispToolLen.ToString("0.###", inv)}  ZPlane={_dispZPlane.ToString("0.###", inv)}\n\n" +
                    $"Click a filled region to select a segment.";
                return;
            }

            var it = _items.FirstOrDefault(x => x.Seg.Index == _selectedIndex);
            if (it == null)
            {
                InfoText.Text =
                    $"TOOL\n" +
                    $"Dia={_dispToolDia.ToString("0.###", inv)}  Rad={dispToolRad.ToString("0.###", inv)}\n" +
                    $"Len={_dispToolLen.ToString("0.###", inv)}  ZPlane={_dispZPlane.ToString("0.###", inv)}";
                return;
            }

            // Per-segment tool dia (VIEW ALL)
            string segToolLine = "";
            if (double.IsFinite(it.Seg.ToolDia) && it.Seg.ToolDia > 0.0)
            {
                double segRad = it.Seg.ToolDia * 0.5;
                segToolLine = $"SegToolDia={it.Seg.ToolDia:0.###}  SegToolRad={segRad:0.###}\n";
            }

            string segLine =
                $"SEG {it.Seg.Index}  {it.Seg.Type}\n" +
                $"Region={it.Seg.RegionName}  Matrix={it.Seg.MatrixName}\n" +
                segToolLine +
                $"RotY={it.Seg.RotYDeg:0.###}°  RotZ={it.Seg.RotZDeg:0.###}°  " +
                $"Tx={it.Seg.Tx:0.###}  Ty={it.Seg.Ty:0.###}  Tz={it.Seg.Tz:0.###}\n" +
                $"P1=({it.Seg.X1:0.###},{it.Seg.Y1:0.###})  " +
                $"P2=({it.Seg.X2:0.###},{it.Seg.Y2:0.###})\n";

            if (it.IsArc)
            {
                string cls = it.IsBand ? "BAND (r > toolRad)" : "SMALLPIE (r <= toolRad)";
                segLine +=
                    $"Pm=({it.Seg.Xm:0.###},{it.Seg.Ym:0.###})\n" +
                    $"C=({it.ArcCenterWorld.X:0.###},{it.ArcCenterWorld.Y:0.###})  r={it.ArcR:0.###}\n" +
                    $"{cls}";
            }

            InfoText.Text =
                $"TOOL\n" +
                $"Dia={_dispToolDia:0.###}  Rad={dispToolRad:0.###}\n" +
                $"Len={_dispToolLen:0.###}  ZPlane={_dispZPlane:0.###}\n\n" +
                segLine;
        }


        // ============================================================
        // Viewer-only transforms
        // Rule: RotZ first (CW+), then RotY (only 180 => mirror X).
        // NOTE: Tx/Ty/Tz are NOT applied here (per your current rule).
        // ============================================================
        private static List<PathSeg> TransformSegmentsForView(List<PathSeg> raw, bool cancelTransform)
        {
            if (raw == null || raw.Count == 0)
                return raw ?? new List<PathSeg>();

            var outList = new List<PathSeg>(raw.Count);

            for (int i = 0; i < raw.Count; i++)
            {
                var s = raw[i];
                if (s == null)
                    continue;

                // Decide rotations ONCE per segment, AFTER null-check
                double zRot = cancelTransform ? 0.0 : s.RotZDeg;
                double yRot = cancelTransform ? 0.0 : s.RotYDeg;

                bool mirrorX = IsRotY180(yRot);

                TransformPoint2D(s.X1, s.Y1, zRot, mirrorX, out double x1, out double y1);
                TransformPoint2D(s.X2, s.Y2, zRot, mirrorX, out double x2, out double y2);

                double xm = s.Xm;
                double ym = s.Ym;
                if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                    TransformPoint2D(s.Xm, s.Ym, zRot, mirrorX, out xm, out ym);

                string type = s.Type ?? "";
                if (mirrorX && (type == "ARC3_CW" || type == "ARC3_CCW"))
                    type = (type == "ARC3_CW") ? "ARC3_CCW" : "ARC3_CW";

                outList.Add(new PathSeg
                {
                    Index = s.Index,
                    Type = type,
                    X1 = x1,
                    Y1 = y1,
                    Xm = xm,
                    Ym = ym,
                    X2 = x2,
                    Y2 = y2,

                    ToolDia = s.ToolDia,

                    RegionName = s.RegionName ?? "",
                    MatrixName = s.MatrixName ?? "",

                    // IMPORTANT: if we cancel, we want the viewer to ALSO show 0/0 in the info panel
                    RotZDeg = zRot,
                    RotYDeg = yRot,

                    Tx = s.Tx,
                    Ty = s.Ty,
                    Tz = s.Tz,

                    RegionColor = s.RegionColor,

                    // FIX: preserve ClosedWire mode flags through transform
                    IsClosedWire = s.IsClosedWire,
                    ClosedWireInner = s.ClosedWireInner,
                    ClosedWireOuter = s.ClosedWireOuter
                });
            }

            return outList;
        }








        private static void TransformPoint2D(double x, double y, double rotZDegCw, bool mirrorX, out double xo, out double yo)
        {
            double rad = -rotZDegCw * (Math.PI / 180.0); // CW-positive
            double c = Math.Cos(rad);
            double s = Math.Sin(rad);

            double xr = x * c - y * s;
            double yr = x * s + y * c;

            if (mirrorX)
                xr = -xr;

            xo = xr;
            yo = yr;
        }

        private static bool IsRotY180(double rotYDeg)
        {
            double a = Norm360(rotYDeg);
            return Math.Abs(a - 180.0) < 1e-3;
        }

        private static double Norm360(double deg)
        {
            if (!double.IsFinite(deg))
                return 0.0;

            deg %= 360.0;
            if (deg < 0.0) deg += 360.0;
            return deg;
        }

        private static bool SnapIsChecked(SetManagement.UiStateSnapshot? snap, string key)
        {
            if (snap == null || snap.Values == null) return false;
            if (!snap.Values.TryGetValue(key, out string v)) return false;
            return v == "1";
        }

        private static void GetMillWireModes(SetManagement.RegionSet set, out bool isClosedWire, out bool inner, out bool outer)
        {
            // Defaults: GuidedTool behaviour (ClosedWire off)
            isClosedWire = false;
            inner = false;
            outer = false;

            var snap = set?.PageSnapshot;

            bool guided = SnapIsChecked(snap, "GuidedTool");
            bool closed = SnapIsChecked(snap, "ClosedWire");
            bool inr = SnapIsChecked(snap, "ClosedInner");
            bool outr = SnapIsChecked(snap, "ClosedOuter");

            // wiregroup mutual exclusion (ClosedWire wins if both somehow true)
            isClosedWire = closed && !guided ? true : (closed ? true : false);

            if (!isClosedWire)
                return;

            // inout only meaningful for ClosedWire
            inner = inr;
            outer = outr;

            // consistent default: OUTER if neither selected
            if (!inner && !outer)
                outer = true;

            // if both selected (bad state) prefer OUTER (consistent)
            if (inner && outer)
            {
                inner = false;
                outer = true;
            }
        }



        // ======================================================================
        // Clipper Display Toggle and Rendering
        // - chordal tolerance for arcs = 0.001 (world units)
        // - force-close every polygon before union
        // ======================================================================

        private bool _showClipper = false;

        // ======================================================================
        // SHARED CLIPPER PIPELINE (single source of truth)
        // - Both: (a) Clipper display, and (b) vertex-point listing
        //   must come from the same computed result.
        // - We also classify OUTER vs ISLAND (hole) by signed area.
        // - Display rule: ISLANDs are filled WHITE.
        // ======================================================================








        private List<ClipperUnionLoop> BuildClipperUnionLoopsWorld(
    out int subjCount,
    out int resultCount,
    out long totalResultVertices)
        {
            subjCount = 0;
            resultCount = 0;
            totalResultVertices = 0;

            var loops = new List<ClipperUnionLoop>();

            if (_segs == null || _segs.Count == 0)
                return loops;

            // Build subjects EXACTLY like RenderClipperBoundary used to,
            // but centralized so Display + VertexList share it.
            var subj = BuildClipperSubjectPolygonsForUnion(CLIPPER_SCALE, CLIPPER_CHORD_TOL);
            subjCount = subj.Count;

            // Union
            var result = new Clipper2Lib.Paths64();
            var clipper = new Clipper2Lib.Clipper64();
            clipper.AddSubject(subj);
            clipper.Execute(Clipper2Lib.ClipType.Union, Clipper2Lib.FillRule.NonZero, result);

            resultCount = result.Count;

            // Convert each result polygon to a world loop + classify hole/outer by signed area
            for (int i = 0; i < result.Count; i++)
            {
                var poly = result[i];
                if (poly == null || poly.Count < 3)
                    continue;

                // Work with an OPEN ring for area + listing (Clipper may or may not close).
                var open = MakeOpen(poly);
                if (open.Count < 3)
                    continue;

                double area = SignedArea64Open(open);
                bool isHole = area < 0.0;   // convention: negative area => CW => hole

                var loop = new ClipperUnionLoop
                {
                    LoopIndex = loops.Count,
                    IsHole = isHole,
                    SignedAreaScaled = area
                };

                for (int k = 0; k < open.Count; k++)
                {
                    double wx = open[k].X / CLIPPER_SCALE;
                    double wy = open[k].Y / CLIPPER_SCALE;
                    var p = new Point(wx, wy);

                    if (loop.WorldPts.Count == 0)
                    {
                        loop.WorldPts.Add(p);
                    }
                    else
                    {
                        var last = loop.WorldPts[loop.WorldPts.Count - 1];
                        if (!PointsNear(last, p, LOOP_DEDUPE_EPS))
                            loop.WorldPts.Add(p);
                    }
                }

                // Safety: if last ended up equal to first (can happen after cleanup), keep OPEN ring
                if (loop.WorldPts.Count >= 2)
                {
                    var first = loop.WorldPts[0];
                    var last = loop.WorldPts[loop.WorldPts.Count - 1];
                    if (PointsNear(first, last, LOOP_DEDUPE_EPS))
                        loop.WorldPts.RemoveAt(loop.WorldPts.Count - 1);
                }

                // Count AFTER cleanup (this is what downstream logic actually sees)
                totalResultVertices += loop.WorldPts.Count;
                loops.Add(loop);

            }

            return loops;
        }

        private Clipper2Lib.Paths64 BuildClipperSubjectPolygonsForUnion(double scale, double chordTol)
        {
            var subj = new Clipper2Lib.Paths64();
            if (_segs == null || _segs.Count == 0)
                return subj;

            // IMPORTANT: Closed CL Wire sets do NOT participate in the toolpath/clipper pipeline
            var circleKeys = new HashSet<(long cx, long cy, long r)>();

            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];
                if (s == null)
                    continue;

                if (s.IsClosedWire)
                    continue;

                double toolRad = ToolRadWorldForSeg(s);

                Clipper2Lib.Path64 body;

                if (s.Type == "LINE")
                {
                    // Correct helper name in your file:
                    body = BuildLineBodyPolygon(s, toolRad, scale);
                }
                else if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                {
                    // if arc radius > toolRad => band; else pie
                    if (!TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2, out double cx, out double cy, out double r))
                    {
                        // fallback: just circles at endpoints
                        AddCircleSubject(subj, circleKeys, s.X1, s.Y1, toolRad, scale, chordTol, $"C1 Seg={s.Index}");
                        AddCircleSubject(subj, circleKeys, s.X2, s.Y2, toolRad, scale, chordTol, $"C2 Seg={s.Index}");
                        continue;
                    }

                    body = (r > toolRad + 1e-9)
                        ? BuildArcBandPolygon(s, toolRad, scale, chordTol)
                        : BuildArcPiePolygon(s, toolRad, scale, chordTol);
                }
                else
                {
                    continue;
                }

                AddSubjectIfValid(subj, body, $"BODY Seg={s.Index}");

                AddCircleSubject(subj, circleKeys, s.X1, s.Y1, toolRad, scale, chordTol, $"C1 Seg={s.Index}");
                AddCircleSubject(subj, circleKeys, s.X2, s.Y2, toolRad, scale, chordTol, $"C2 Seg={s.Index}");
            }

            return subj;
        }



        // Make a Path64 "open" (no duplicated last=first) for consistent area + listing.
        private static Clipper2Lib.Path64 MakeOpen(Clipper2Lib.Path64 p)
        {
            var outP = new Clipper2Lib.Path64(p);
            if (outP.Count < 2)
                return outP;

            var a = outP[0];
            var b = outP[outP.Count - 1];
            if (a.X == b.X && a.Y == b.Y)
                outP.RemoveAt(outP.Count - 1);

            return outP;
        }

        // Signed area for an OPEN ring in scaled integer space.
        // Sign is what we care about (outer vs hole).
        private static double SignedArea64Open(Clipper2Lib.Path64 pOpen)
        {
            if (pOpen == null || pOpen.Count < 3)
                return 0.0;

            double a = 0.0;
            int n = pOpen.Count;

            // classic polygon area (open ring)
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pj = pOpen[j];
                var pi = pOpen[i];
                a += (pj.X * (double)pi.Y) - (pi.X * (double)pj.Y);
            }

            return 0.5 * a;
        }

















        // ------------------------------------------
        // Display Clipper boundary
        // ------------------------------------------
        private void BtnClipperDispaly_Click(object sender, RoutedEventArgs e)
        {
            if (HasAnyClosedWire())
            {
                ShowClosedWireButtonsNotUsedMessage();
                return;
            }

            _showClipper = true;

            // Keep current zoom/pan. (Hold SHIFT to refit.)
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                _vp.Reset();
                ComputeWorldFit();
            }

            RenderClipperBoundary();
        }



        // ------------------------------------------
        // Display raw geometry (default mode)
        // ------------------------------------------
        private void BtnRawDispaly_Click(object sender, RoutedEventArgs e)
        {
            _showClipper = false;
            Render();
        }






        // ------------------------------------------
        // Build and render the Clipper outline (world-aligned to viewport)
        //
        // FIXES:
        // - chordal tolerance for arcs (world units)
        // - force-close every polygon
        // - remove duplicate consecutive points (rounding artifacts)
        // - normalize winding so ALL subjects are CCW (prevents NonZero "pie holes")
        // - de-dupe endpoint circles so shared vertices don’t add the exact same circle twice
        // ------------------------------------------
        private void RenderClipperBoundary()
        {
            TopCanvas.Children.Clear();

            if (_segs == null || _segs.Count == 0)
                return;

            // Ensure settings-driven widths/colors are up to date
            ApplyViewerStylesFromSettings();

            // IMPORTANT: display + vertex listing must come from the SAME computed union.
            int subjCount, resultCount;
            long totalVerts;
            var loops = BuildClipperUnionLoopsWorld(out subjCount, out resultCount, out totalVerts);

            // Requested colors:
            //   OUTER  = stroke YELLOW, fill YELLOW
            //   ISLAND = stroke YELLOW, fill BLACK
            Brush strokeOutside = DebugPalette.ClipperStrokeOutside;
            Brush strokeIsland = DebugPalette.ClipperStrokeIsland;
            Brush fillOutside = DebugPalette.ClipperFillOutside;
            Brush fillIslandInside = DebugPalette.ClipperFillIslandInside;






            for (int i = 0; i < loops.Count; i++)
            {
                var loop = loops[i];
                if (loop.WorldPts.Count < 3)
                    continue;

                bool isIsland = loop.IsHole;

                var poly = new Polygon
                {
                    StrokeThickness = Math.Max(1.0, _profileWidth + 0.5),



                    Stroke = isIsland ? strokeIsland : strokeOutside,
                    Fill = isIsland ? fillIslandInside : fillOutside
                };

                for (int k = 0; k < loop.WorldPts.Count; k++)
                {
                    var w = loop.WorldPts[k];
                    poly.Points.Add(W2S(w.X, w.Y));
                }

                TopCanvas.Children.Add(poly);
            }

            var info = new TextBlock
            {
                Text = $"Clipper union: subj={subjCount}  loops={resultCount}  totalVerts={totalVerts}  chordTol={CLIPPER_CHORD_TOL}",
                Foreground = _graphicText,
                FontSize = 12
            };
            Canvas.SetLeft(info, 10);
            Canvas.SetTop(info, 10);
            TopCanvas.Children.Add(info);
        }





        // ======================================================================
        // Subject add helpers (NEW)
        // ======================================================================
        private static void AddSubjectIfValid(Clipper2Lib.Paths64 subj, Clipper2Lib.Path64 p, string tagForDebug)
        {
            if (p == null || p.Count < 3)
                return;

            // remove duplicate consecutive points (common after rounding)
            RemoveConsecutiveDuplicates(p);

            // must be at least a triangle
            if (p.Count < 3)
                return;

            // close it
            EnsureClosed(p);

            // if closing made it too small, bail
            if (p.Count < 4)
                return;

            // normalize winding to CCW to prevent NonZero holes
            NormalizeToCCW(p);

            // still valid?
            if (p.Count >= 4)
                subj.Add(p);
        }

        private static void AddCircleSubject(
            Clipper2Lib.Paths64 subj,
            HashSet<(long cx, long cy, long r)> circleKeys,
            double cxW, double cyW, double rW,
            double scale, double chordTol,
            string tagForDebug)
        {
            if (!double.IsFinite(cxW) || !double.IsFinite(cyW) || !double.IsFinite(rW) || rW <= 0.0)
                return;

            long icx = (long)Math.Round(cxW * scale);
            long icy = (long)Math.Round(cyW * scale);
            long ir = (long)Math.Round(rW * scale);

            if (ir <= 0)
                return;

            // de-dupe exact same circle (shared endpoints)
            var key = (icx, icy, ir);
            if (!circleKeys.Add(key))
                return;

            var c = BuildCirclePolygon(cxW, cyW, rW, scale, chordTol);
            AddSubjectIfValid(subj, c, tagForDebug);
        }


        // ======================================================================
        // 2D POLYGON BUILDERS – body shapes (caps handled by endpoint circles)
        // ======================================================================

        private static Clipper2Lib.Path64 BuildLineBodyPolygon(PathSeg s, double toolRad, double scale)
        {
            double dx = s.X2 - s.X1;
            double dy = s.Y2 - s.Y1;
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            if (segLen < 1e-12)
                return new Clipper2Lib.Path64();

            // left normal relative to p1->p2
            double nx = -(dy / segLen);
            double ny = (dx / segLen);

            double x1L = s.X1 + nx * toolRad;
            double y1L = s.Y1 + ny * toolRad;
            double x2L = s.X2 + nx * toolRad;
            double y2L = s.Y2 + ny * toolRad;

            double x2R = s.X2 - nx * toolRad;
            double y2R = s.Y2 - ny * toolRad;
            double x1R = s.X1 - nx * toolRad;
            double y1R = s.Y1 - ny * toolRad;

            var path = new Clipper2Lib.Path64
    {
        ToP64(x1L, y1L, scale),
        ToP64(x2L, y2L, scale),
        ToP64(x2R, y2R, scale),
        ToP64(x1R, y1R, scale)
    };

            return path;
        }

        private static Clipper2Lib.Path64 BuildArcBandPolygon(PathSeg s, double toolRad, double scale, double chordTol)
        {
            if (!TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2,
                                      out double cx, out double cy, out double r))
                return new Clipper2Lib.Path64();

            if (r <= toolRad + 1e-9)
                return new Clipper2Lib.Path64();

            double outerR = r + toolRad;
            double innerR = r - toolRad;

            bool ccw = (s.Type == "ARC3_CCW");

            double a1 = Math.Atan2(s.Y1 - cy, s.X1 - cx);
            double a2 = Math.Atan2(s.Y2 - cy, s.X2 - cx);

            double sweep = ccw ? CcwDelta(a1, a2) : -CcwDelta(a2, a1);
            double sweepAbs = Math.Abs(sweep);

            int divs = DivsForArcByChordTol(outerR, sweepAbs, chordTol, minDivs: 48, maxDivs: 20000);

            var path = new Clipper2Lib.Path64();

            // Outer arc
            for (int i = 0; i <= divs; i++)
            {
                double ang = a1 + sweep * i / divs;
                double x = cx + Math.Cos(ang) * outerR;
                double y = cy + Math.Sin(ang) * outerR;
                path.Add(ToP64(x, y, scale));
            }

            // Inner arc reversed
            for (int i = divs; i >= 0; i--)
            {
                double ang = a1 + sweep * i / divs;
                double x = cx + Math.Cos(ang) * innerR;
                double y = cy + Math.Sin(ang) * innerR;
                path.Add(ToP64(x, y, scale));
            }

            return path;
        }

        private static Clipper2Lib.Path64 BuildArcPiePolygon(PathSeg s, double toolRad, double scale, double chordTol)
        {
            if (!TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2,
                                      out double cx, out double cy, out double rSmall))
                return new Clipper2Lib.Path64();

            double bigR = rSmall + toolRad;

            bool ccw = (s.Type == "ARC3_CCW");

            double a1 = Math.Atan2(s.Y1 - cy, s.X1 - cx);
            double a2 = Math.Atan2(s.Y2 - cy, s.X2 - cx);

            double sweep = ccw ? CcwDelta(a1, a2) : -CcwDelta(a2, a1);
            double sweepAbs = Math.Abs(sweep);

            int divs = DivsForArcByChordTol(bigR, sweepAbs, chordTol, minDivs: 48, maxDivs: 20000);

            var path = new Clipper2Lib.Path64();

            var cpt = ToP64(cx, cy, scale);
            path.Add(cpt);

            for (int i = 0; i <= divs; i++)
            {
                double ang = a1 + sweep * i / divs;
                double x = cx + Math.Cos(ang) * bigR;
                double y = cy + Math.Sin(ang) * bigR;
                path.Add(ToP64(x, y, scale));
            }

            path.Add(cpt);

            return path;
        }

        private static Clipper2Lib.Path64 BuildCirclePolygon(double cx, double cy, double r, double scale, double chordTol)
        {
            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(r) || r <= 0.0)
                return new Clipper2Lib.Path64();

            const double sweepAbs = 2.0 * Math.PI;
            int divs = DivsForArcByChordTol(r, sweepAbs, chordTol, minDivs: 64, maxDivs: 20000);

            var path = new Clipper2Lib.Path64(divs);

            for (int i = 0; i < divs; i++)
            {
                double ang = (2.0 * Math.PI) * i / divs;
                double x = cx + Math.Cos(ang) * r;
                double y = cy + Math.Sin(ang) * r;
                path.Add(ToP64(x, y, scale));
            }

            return path;
        }


        // ======================================================================
        // Chordal tolerance -> division count
        // chordTol is max sagitta: sagitta = R*(1 - cos(dθ/2)) <= chordTol
        // => dθ = 2*acos(1 - chordTol/R)
        // ======================================================================
        private static int DivsForArcByChordTol(double radius, double sweepAbs, double chordTol, int minDivs, int maxDivs)
        {
            if (!double.IsFinite(radius) || radius <= 1e-12)
                return minDivs;

            if (!double.IsFinite(sweepAbs) || sweepAbs <= 1e-12)
                return minDivs;

            if (!double.IsFinite(chordTol) || chordTol <= 0.0)
                return minDivs;

            if (chordTol >= radius)
                return minDivs;

            double cosArg = 1.0 - (chordTol / radius);
            if (cosArg < -1.0) cosArg = -1.0;
            if (cosArg > 1.0) cosArg = 1.0;

            double dTheta = 2.0 * Math.Acos(cosArg);
            if (!double.IsFinite(dTheta) || dTheta <= 1e-12)
                return maxDivs;

            int n = (int)Math.Ceiling(sweepAbs / dTheta);
            if (n < minDivs) n = minDivs;
            if (n > maxDivs) n = maxDivs;
            return n;
        }


        // ======================================================================
        // Close + winding normalization helpers (NEW)
        // ======================================================================
        private static bool IsClosed(Clipper2Lib.Path64 p)
        {
            if (p == null || p.Count < 2)
                return false;
            var a = p[0];
            var b = p[p.Count - 1];
            return a.X == b.X && a.Y == b.Y;
        }

        private static void EnsureClosed(Clipper2Lib.Path64 p)
        {
            if (p == null || p.Count == 0)
                return;
            if (!IsClosed(p))
                p.Add(p[0]);
        }

        private static void RemoveConsecutiveDuplicates(Clipper2Lib.Path64 p)
        {
            if (p == null || p.Count < 2)
                return;

            // If closed, temporarily open it to clean properly
            bool closed = IsClosed(p);
            if (closed)
                p.RemoveAt(p.Count - 1);

            for (int i = p.Count - 1; i > 0; i--)
            {
                if (p[i].X == p[i - 1].X && p[i].Y == p[i - 1].Y)
                    p.RemoveAt(i);
            }

            if (closed && p.Count > 0)
                p.Add(p[0]);
        }

        private static void NormalizeToCCW(Clipper2Lib.Path64 p)
        {
            if (p == null || p.Count < 4)
                return;

            // work on an open ring for area and reverse
            bool closed = IsClosed(p);
            if (closed)
                p.RemoveAt(p.Count - 1);

            double area = SignedArea(p);

            // If CW (negative area), reverse to CCW
            if (area < 0.0)
                p.Reverse();

            // re-close
            if (p.Count > 0)
                p.Add(p[0]);
        }

        private static double SignedArea(Clipper2Lib.Path64 p)
        {
            // p is expected OPEN here (no duplicated last point)
            if (p == null || p.Count < 3)
                return 0.0;

            double a = 0.0;
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                var p1 = p[i];
                var p2 = p[(i + 1) % n];
                a += (p1.X * (double)p2.Y) - (p2.X * (double)p1.Y);
            }
            return 0.5 * a;
        }

        private static Clipper2Lib.Point64 ToP64(double x, double y, double scale)
        {
            long ix = (long)Math.Round(x * scale);
            long iy = (long)Math.Round(y * scale);
            return new Clipper2Lib.Point64(ix, iy);
        }






        private WpfGeometry BuildLineCapsuleGeometry(PathSeg s, double toolRadWorld)
        {
            Point p1 = W2S(s.X1, s.Y1);
            Point p2 = W2S(s.X2, s.Y2);

            double rad = WR2SR(toolRadWorld);

            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9)
                return new EllipseGeometry(p1, rad, rad);

            double ux = dx / len;
            double uy = dy / len;

            double nx = -uy;
            double ny = ux;

            Point p1L = new Point(p1.X + nx * rad, p1.Y + ny * rad);
            Point p2L = new Point(p2.X + nx * rad, p2.Y + ny * rad);
            Point p2R = new Point(p2.X - nx * rad, p2.Y - ny * rad);
            Point p1R = new Point(p1.X - nx * rad, p1.Y - ny * rad);

            var rect = new StreamGeometry();
            using (var ctx = rect.Open())
            {
                ctx.BeginFigure(p1L, isFilled: true, isClosed: true);
                ctx.LineTo(p2L, true, false);
                ctx.LineTo(p2R, true, false);
                ctx.LineTo(p1R, true, false);
            }
            rect.Freeze();

            // WPF FillRule enum name differs across libs in your file; avoid name entirely.
            var gg = new GeometryGroup { FillRule = (System.Windows.Media.FillRule)1 };
            gg.Children.Add(rect);
            gg.Children.Add(new EllipseGeometry(p1, rad, rad));
            gg.Children.Add(new EllipseGeometry(p2, rad, rad));
            gg.Freeze();

            return gg;
        }

        private WpfGeometry BuildArcBandGeometry(PathSeg s, double cxW, double cyW, double rW, double toolRadWorld)
        {
            Point c = W2S(cxW, cyW);

            Point p1 = W2S(s.X1, s.Y1);
            Point pm = W2S(s.Xm, s.Ym);
            Point p2 = W2S(s.X2, s.Y2);

            double toolR = WR2SR(toolRadWorld);
            double r = WR2SR(rW);

            double outerR = r + toolR;
            double innerR = r - toolR;

            Point o1 = ScaleToRadius(c, p1, outerR);
            Point o2 = ScaleToRadius(c, p2, outerR);

            Point i1 = ScaleToRadius(c, p1, innerR);
            Point i2 = ScaleToRadius(c, p2, innerR);

            GetSweep(c, p1, pm, p2, out SweepDirection sd, out bool isLargeArc);

            var fig = new PathFigure { StartPoint = o1, IsClosed = true, IsFilled = true };
            fig.Segments.Add(new ArcSegment
            {
                Point = o2,
                Size = new Size(outerR, outerR),
                RotationAngle = 0,
                IsLargeArc = isLargeArc,
                SweepDirection = sd,
                IsStroked = true
            });

            fig.Segments.Add(new LineSegment { Point = i2, IsStroked = true });

            fig.Segments.Add(new ArcSegment
            {
                Point = i1,
                Size = new Size(innerR, innerR),
                RotationAngle = 0,
                IsLargeArc = isLargeArc,
                SweepDirection = Opposite(sd),
                IsStroked = true
            });

            // WPF FillRule enum name differs across libs in your file; avoid name entirely.
            var pg = new PathGeometry(new[] { fig }) { FillRule = (System.Windows.Media.FillRule)1 };
            pg.Freeze();

            var gg = new GeometryGroup { FillRule = (System.Windows.Media.FillRule)1 };
            gg.Children.Add(pg);
            gg.Children.Add(new EllipseGeometry(p1, toolR, toolR));
            gg.Children.Add(new EllipseGeometry(p2, toolR, toolR));
            gg.Freeze();

            return gg;
        }

        private WpfGeometry BuildArcSmallPieGeometry(PathSeg s, double cxW, double cyW, double rW, double toolRadWorld)
        {
            Point c = W2S(cxW, cyW);

            Point p1 = W2S(s.X1, s.Y1);
            Point pm = W2S(s.Xm, s.Ym);
            Point p2 = W2S(s.X2, s.Y2);

            double toolR = WR2SR(toolRadWorld);
            double r = WR2SR(rW);

            double bigR = r + toolR;

            Point b1 = ScaleToRadius(c, p1, bigR);
            Point b2 = ScaleToRadius(c, p2, bigR);

            GetSweep(c, p1, pm, p2, out SweepDirection sd, out bool isLargeArc);

            var fig = new PathFigure { StartPoint = c, IsClosed = true, IsFilled = true };
            fig.Segments.Add(new LineSegment { Point = b1, IsStroked = true });
            fig.Segments.Add(new ArcSegment
            {
                Point = b2,
                Size = new Size(bigR, bigR),
                RotationAngle = 0,
                IsLargeArc = isLargeArc,
                SweepDirection = sd,
                IsStroked = true
            });
            fig.Segments.Add(new LineSegment { Point = c, IsStroked = true });

            // WPF FillRule enum name differs across libs in your file; avoid name entirely.
            var pg = new PathGeometry(new[] { fig }) { FillRule = (System.Windows.Media.FillRule)1 };
            pg.Freeze();

            return pg;
        }



        // ------------------------------------------
        // Display PRE-Clipper polygons (input polys)
        // ------------------------------------------
        private void BtnPreClipperDispaly_Click(object sender, RoutedEventArgs e)
        {
            if (HasAnyClosedWire())
            {
                ShowClosedWireButtonsNotUsedMessage();
                return;
            }

            _showClipper = true;

            // Keep current zoom/pan. (Hold SHIFT to refit.)
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                _vp.Reset();
                ComputeWorldFit();
            }

            RenderPreClipperBoundary();
        }


        // ------------------------------------------
        // Draw the INPUT polygons that will be fed to Clipper (before union)
        // Each polygon gets a distinct color (MillPage.ColorList)
        // ------------------------------------------
        private void RenderPreClipperBoundary()
        {
            TopCanvas.Children.Clear();

            if (_segs == null || _segs.Count == 0)
                return;

            ApplyViewerStylesFromSettings();


            // Build subject polygons exactly like the union path (but do NOT union)
            Clipper2Lib.Paths64 subj = BuildClipperSubjectPolygonsForUnion(CLIPPER_SCALE, CLIPPER_CHORD_TOL);

            int colorCount = (MillPage.ColorList != null && MillPage.ColorList.Count > 0)
                ? MillPage.ColorList.Count
                : 1;

            long totalPts = 0;

            for (int i = 0; i < subj.Count; i++)
            {
                var poly = subj[i];
                if (poly == null || poly.Count < 2)
                    continue;

                totalPts += poly.Count;

                Brush stroke = (colorCount > 1)
                    ? MillPage.ColorList[i % colorCount]
                    : DebugPalette.ClipperGood;

                var pl = new Polyline
                {
                    Stroke = stroke,
                    StrokeThickness = Math.Max(1.0, _profileWidth + 0.3),
                    Fill = Brushes.Transparent
                };

                // Convert back to world then through W2S (same as your normal view)
                for (int k = 0; k < poly.Count; k++)
                {
                    double wx = poly[k].X / CLIPPER_SCALE;
                    double wy = poly[k].Y / CLIPPER_SCALE;
                    pl.Points.Add(W2S(wx, wy));
                }

                // close visually (even if already closed)
                if (pl.Points.Count > 0)
                    pl.Points.Add(pl.Points[0]);

                TopCanvas.Children.Add(pl);
            }

            var info = new TextBlock
            {
                Text = $"PRE-CLIPPER polys: {subj.Count}   totalPts={totalPts}   chordTol={CLIPPER_CHORD_TOL}",
                Foreground = _graphicText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            };
            Canvas.SetLeft(info, 10);
            Canvas.SetTop(info, 10);
            TopCanvas.Children.Add(info);
        }



        // ------------------------------------------
        // Display CANDIDATE boundary curves
        // - NO fill (stroke only)
        // - Uses MillPage.ColorList
        // - Lines: draw ONLY the two offset side lines (NO rectangle end lines)
        // - Arcs r>toolRad: draw outer+inner offset arcs (NO radial joins), + endpoint full circles
        // - Arcs r<=toolRad: draw ONLY the outer offset arc (NO pie radial lines), + endpoint full circles
        // - Endpoint caps: draw FULL circles, de-duped across shared endpoints
        // ------------------------------------------
        private void BtnCandigate_Click(object sender, RoutedEventArgs e)
        {
            if (HasAnyClosedWire())
            {
                ShowClosedWireButtonsNotUsedMessage();
                return;
            }

            _showClipper = true;

            // Keep current zoom/pan. (Hold SHIFT to refit.)
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                _vp.Reset();
                ComputeWorldFit();
            }

            RenderCandidateBoundary();
        }


        private void RenderCandidateBoundary()
        {
            TopCanvas.Children.Clear();

            if (_segs == null || _segs.Count == 0)
                return;

            // Ensure settings-driven widths/colors are up to date
            ApplyViewerStylesFromSettings();




            int colorCount = (MillPage.ColorList != null && MillPage.ColorList.Count > 0)
                ? MillPage.ColorList.Count
                : 1;

            var circleKeys = new HashSet<(long cx, long cy, long r)>();

            long primitives = 0;

            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];
                double toolRad = ToolRadWorldForSeg(s);

                Brush stroke = (colorCount > 1)
                    ? MillPage.ColorList[i % colorCount]
                    : DebugPalette.ClipperGood;

                double thk = Math.Max(0.5, _profileWidth);

                // ------------------------------------------
                // LINE: draw ONLY the 2 offset side lines (no end lines),
                // then full circles at endpoints (de-duped)
                // ------------------------------------------
                if (s.Type == "LINE")
                {
                    AddCandidateLineOffsetSides(s, toolRad, stroke, thk, ref primitives);
                    AddCandidateEndpointCircle(s.X1, s.Y1, toolRad, stroke, thk, circleKeys, DEDUPE_SCALE, CLIPPER_CHORD_TOL, ref primitives);
                    AddCandidateEndpointCircle(s.X2, s.Y2, toolRad, stroke, thk, circleKeys, DEDUPE_SCALE, CLIPPER_CHORD_TOL, ref primitives);
                    continue;
                }

                // ------------------------------------------
                // ARCS
                // ------------------------------------------
                if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                {
                    if (!TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2, out double cx, out double cy, out double r))
                    {
                        // If circle fit fails, still draw endpoint circles (de-duped)
                        AddCandidateEndpointCircle(s.X1, s.Y1, toolRad, stroke, thk, circleKeys, DEDUPE_SCALE, CLIPPER_CHORD_TOL, ref primitives);
                        AddCandidateEndpointCircle(s.X2, s.Y2, toolRad, stroke, thk, circleKeys, DEDUPE_SCALE, CLIPPER_CHORD_TOL, ref primitives);
                        continue;
                    }

                    bool ccw = (s.Type == "ARC3_CCW");

                    double a1 = Math.Atan2(s.Y1 - cy, s.X1 - cx);
                    double a2 = Math.Atan2(s.Y2 - cy, s.X2 - cx);
                    double sweep = ccw ? CcwDelta(a1, a2) : -CcwDelta(a2, a1);
                    double sweepAbs = Math.Abs(sweep);

                    // Endpoint full circles (de-duped)
                    AddCandidateEndpointCircle(s.X1, s.Y1, toolRad, stroke, thk, circleKeys, DEDUPE_SCALE, CLIPPER_CHORD_TOL, ref primitives);
                    AddCandidateEndpointCircle(s.X2, s.Y2, toolRad, stroke, thk, circleKeys, DEDUPE_SCALE, CLIPPER_CHORD_TOL, ref primitives);

                    // Case A: r > toolRad  => outer + inner offset arcs only
                    if (r > toolRad + 1e-9)
                    {
                        double outerR = r + toolRad;
                        double innerR = r - toolRad;

                        AddCandidateArcPolyline(cx, cy, outerR, a1, sweep, sweepAbs, CLIPPER_CHORD_TOL, stroke, thk, ref primitives);
                        AddCandidateArcPolyline(cx, cy, innerR, a1, sweep, sweepAbs, CLIPPER_CHORD_TOL, stroke, thk, ref primitives);
                    }
                    // Case B: r <= toolRad => ONLY outer offset arc (NO pie radial lines)
                    else
                    {
                        double outerR = r + toolRad;
                        AddCandidateArcPolyline(cx, cy, outerR, a1, sweep, sweepAbs, CLIPPER_CHORD_TOL, stroke, thk, ref primitives);
                    }
                }
            }

            var info = new TextBlock
            {
                Text = $"CANDIDATES: segs={_segs.Count}  primitives={primitives}  chordTol={CLIPPER_CHORD_TOL}",
                Foreground = _graphicText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            };
            Canvas.SetLeft(info, 10);
            Canvas.SetTop(info, 10);
            TopCanvas.Children.Add(info);
        }

        private void AddCandidateLineOffsetSides(PathSeg s, double toolRad, Brush stroke, double thk, ref long primitives)
        {
            double dx = s.X2 - s.X1;
            double dy = s.Y2 - s.Y1;
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            if (segLen < 1e-12)
                return;

            double nx = -(dy / segLen);
            double ny = (dx / segLen);

            // Left offset line
            double x1L = s.X1 + nx * toolRad;
            double y1L = s.Y1 + ny * toolRad;
            double x2L = s.X2 + nx * toolRad;
            double y2L = s.Y2 + ny * toolRad;

            // Right offset line
            double x1R = s.X1 - nx * toolRad;
            double y1R = s.Y1 - ny * toolRad;
            double x2R = s.X2 - nx * toolRad;
            double y2R = s.Y2 - ny * toolRad;

            AddCandidatePolyline2(x1L, y1L, x2L, y2L, stroke, thk);
            primitives++;

            AddCandidatePolyline2(x1R, y1R, x2R, y2R, stroke, thk);
            primitives++;
        }



        private void AddCandidateArcPolyline(
            double cx, double cy,
            double radius,
            double aStart,
            double sweep,
            double sweepAbs,
            double chordTol,
            Brush stroke,
            double thk,
            ref long primitives)
        {
            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(radius) || radius <= 1e-12)
                return;

            // use your existing helper (in this file) to pick a div count from chordTol
            int divs = DivsForArcByChordTol(radius, sweepAbs, chordTol, minDivs: 48, maxDivs: 20000);
            if (divs < 2) divs = 2;

            var pl = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = thk
                // NO Fill (Polyline has no Fill)
            };

            for (int i = 0; i <= divs; i++)
            {
                double ang = aStart + (sweep * i / divs);
                double x = cx + Math.Cos(ang) * radius;
                double y = cy + Math.Sin(ang) * radius;
                pl.Points.Add(W2S(x, y));
            }

            TopCanvas.Children.Add(pl);
            primitives++;
        }

        private void AddCandidateEndpointCircle(
            double cxW,
            double cyW,
            double rW,
            Brush stroke,
            double thk,
            HashSet<(long cx, long cy, long r)> circleKeys,
            double dedupeScale,
            double chordTol,
            ref long primitives)
        {
            if (!double.IsFinite(cxW) || !double.IsFinite(cyW) || !double.IsFinite(rW) || rW <= 0.0)
                return;

            long icx = (long)Math.Round(cxW * dedupeScale);
            long icy = (long)Math.Round(cyW * dedupeScale);
            long ir = (long)Math.Round(rW * dedupeScale);
            if (ir <= 0) return;

            // De-dupe exact same cap circle (shared endpoints)
            if (!circleKeys.Add((icx, icy, ir)))
                return;

            const double sweepAbs = 2.0 * Math.PI;
            int divs = DivsForArcByChordTol(rW, sweepAbs, chordTol, minDivs: 64, maxDivs: 20000);
            if (divs < 12) divs = 12;

            var pl = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = thk
            };

            for (int i = 0; i <= divs; i++)
            {
                double ang = (2.0 * Math.PI) * i / divs;
                double x = cxW + Math.Cos(ang) * rW;
                double y = cyW + Math.Sin(ang) * rW;
                pl.Points.Add(W2S(x, y));
            }

            TopCanvas.Children.Add(pl);
            primitives++;
        }

        private void AddCandidatePolyline2(double x1, double y1, double x2, double y2, Brush stroke, double thk)
        {
            var pl = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = thk
            };

            pl.Points.Add(W2S(x1, y1));
            pl.Points.Add(W2S(x2, y2));

            TopCanvas.Children.Add(pl);
        }




























        // ======================================================================
        // CANDIDATE SNAP POINTS (analytic) -> LOG WINDOW
        // Builds a space-delimited (x y) list of ALL possible snap points from the
        // SAME candidate geometry you draw in RenderCandidateBoundary().
        //
        // Candidates considered:
        //   - Line offset sides (finite segments)
        //   - Endpoint cap circles (full circles; de-duped same as display)
        //   - Arc offset boundaries as CIRCULAR ARCS (outer/inner where applicable)
        // Intersections computed:
        //   - line-line (segment-segment)
        //   - line-circle (segment vs full circle / arc circle)
        //   - circle-circle (full/arc circles)
        // Intersections are filtered to lie on the finite segment(s) and (if arc)
        // within the arc sweep.
        // Dedup: 5dp keying (same style as your arc-fit logic)
        // ======================================================================

        private const int SNAP_KEY_DECIMALS = 5;

        private enum CandEntityType
        {
            LineSeg,
            CircleFull,
            CircleArc
        }

        private sealed class CandEntity
        {
            public int Eid;
            public CandEntityType Type;

            // for traceability / future use
            public int SourceSegIndex;
            public string SourceKind = ""; // e.g. "LINE_L", "LINE_R", "CAP", "ARC_OUTER", "ARC_INNER", "ARC_OUTER_SMALL"

            // Line
            public double X1, Y1, X2, Y2;

            // Circle / Arc
            public double Cx, Cy, R;

            // Arc only (CircleArc): start angle (rad) and signed sweep (rad)
            // sweep > 0 => CCW, sweep < 0 => CW
            public double AStart;
            public double Sweep;
        }

        private sealed class CandSnapPoint
        {
            public double X;
            public double Y;

            // Keep the entity IDs that generated this snap (future use)
            public HashSet<int> Eids = new HashSet<int>();
        }

        private void ShowCandidateSnapPointsLog()
        {
            if (_segs == null || _segs.Count == 0)
            {
                var empty = new System.Text.StringBuilder();
                empty.AppendLine("=== CANDIDATE SNAP POINTS (x y) ===");
                empty.AppendLine("(no segments)");
                var winEmpty = new LogWindow("Candidate Snap Points", empty.ToString()) { Owner = this };
                winEmpty.Show();
                return;
            }

            // Build analytic candidates
            int entityCount;
            var ents = BuildCandidateEntitiesForSnap(out entityCount);

            // Intersect + dedup
            var snaps = BuildCandidateSnapPointsFromEntities(ents);

            // Format: space-delimited x y (ONE per line)
            var inv = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(256 * 1024);

            sb.AppendLine("=== CANDIDATE SNAP POINTS (x y) ===");
            sb.AppendLine($"segs={_segs.Count}  entities={ents.Count}  snaps={snaps.Count}");
            sb.AppendLine($"dedup=Key{SNAP_KEY_DECIMALS}dp");
            sb.AppendLine();

            // Optional: sort for stability
            var ordered = snaps
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                sb.Append(ordered[i].X.ToString($"0.{new string('0', SNAP_KEY_DECIMALS)}", inv));
                sb.Append(' ');
                sb.Append(ordered[i].Y.ToString($"0.{new string('0', SNAP_KEY_DECIMALS)}", inv));
                sb.Append(' ');
                sb.Append(0.0.ToString($"0.{new string('0', SNAP_KEY_DECIMALS)}", inv));
                sb.AppendLine();
            }


            var win = new LogWindow("Candidate Snap Points", sb.ToString())
            {
                Owner = this
            };
            win.Show(); // Show() already gated by Settings.Default.LogWindowShow (your pattern)
        }

        private List<CandEntity> BuildCandidateEntitiesForSnap(out int entityCount)
        {
            entityCount = 0;

            var ents = new List<CandEntity>(4096);

            // De-dupe cap circles EXACTLY like your display does
            var circleKeys = new HashSet<(long cx, long cy, long r)>();
            int eid = 0;

            // ------------------------------------------------------------
            // 1) Build ONLY the real candidate boundary entities:
            //    - line offset side segments
            //    - arc offset boundary arcs (outer/inner as appropriate)
            //    - NO per-segment caps here
            // ------------------------------------------------------------
            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];
                if (s == null)
                    continue;

                double toolRad = ToolRadWorldForSeg(s);

                // ---------- LINE: 2 offset side segments ----------
                if (s.Type == "LINE")
                {
                    double dx = s.X2 - s.X1;
                    double dy = s.Y2 - s.Y1;
                    double segLen = Math.Sqrt(dx * dx + dy * dy);
                    if (segLen >= 1e-12)
                    {
                        double nx = -(dy / segLen);
                        double ny = (dx / segLen);

                        // Left offset segment
                        double x1L = s.X1 + nx * toolRad;
                        double y1L = s.Y1 + ny * toolRad;
                        double x2L = s.X2 + nx * toolRad;
                        double y2L = s.Y2 + ny * toolRad;

                        ents.Add(new CandEntity
                        {
                            Eid = eid++,
                            Type = CandEntityType.LineSeg,
                            SourceSegIndex = s.Index,
                            SourceKind = "LINE_L",
                            X1 = x1L,
                            Y1 = y1L,
                            X2 = x2L,
                            Y2 = y2L
                        });

                        // Right offset segment
                        double x1R = s.X1 - nx * toolRad;
                        double y1R = s.Y1 - ny * toolRad;
                        double x2R = s.X2 - nx * toolRad;
                        double y2R = s.Y2 - ny * toolRad;

                        ents.Add(new CandEntity
                        {
                            Eid = eid++,
                            Type = CandEntityType.LineSeg,
                            SourceSegIndex = s.Index,
                            SourceKind = "LINE_R",
                            X1 = x1R,
                            Y1 = y1R,
                            X2 = x2R,
                            Y2 = y2R
                        });
                    }

                    continue;
                }

                // ---------- ARCS ----------
                if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                {
                    if (!TryCircleFrom3Points(s.X1, s.Y1, s.Xm, s.Ym, s.X2, s.Y2, out double cx, out double cy, out double rBase))
                        continue;

                    bool ccw = (s.Type == "ARC3_CCW");

                    double a1 = Math.Atan2(s.Y1 - cy, s.X1 - cx);
                    double a2 = Math.Atan2(s.Y2 - cy, s.X2 - cx);
                    double sweep = ccw ? CcwDelta(a1, a2) : -CcwDelta(a2, a1); // signed
                    double sweepAbs = Math.Abs(sweep);
                    if (!double.IsFinite(sweepAbs) || sweepAbs <= 1e-12)
                        continue;

                    // Candidate outer arc always exists
                    double rOuter = rBase + toolRad;

                    if (rBase > toolRad + 1e-9)
                    {
                        // band case: outer + inner arcs
                        double rInner = rBase - toolRad;

                        ents.Add(new CandEntity
                        {
                            Eid = eid++,
                            Type = CandEntityType.CircleArc,
                            SourceSegIndex = s.Index,
                            SourceKind = "ARC_OUTER",
                            Cx = cx,
                            Cy = cy,
                            R = rOuter,
                            AStart = a1,
                            Sweep = sweep
                        });

                        ents.Add(new CandEntity
                        {
                            Eid = eid++,
                            Type = CandEntityType.CircleArc,
                            SourceSegIndex = s.Index,
                            SourceKind = "ARC_INNER",
                            Cx = cx,
                            Cy = cy,
                            R = rInner,
                            AStart = a1,
                            Sweep = sweep
                        });
                    }
                    else
                    {
                        // pie case: ONLY outer arc boundary counts
                        ents.Add(new CandEntity
                        {
                            Eid = eid++,
                            Type = CandEntityType.CircleArc,
                            SourceSegIndex = s.Index,
                            SourceKind = "ARC_OUTER_SMALL",
                            Cx = cx,
                            Cy = cy,
                            R = rOuter,
                            AStart = a1,
                            Sweep = sweep
                        });
                    }

                    continue;
                }
            }

            // ------------------------------------------------------------
            // 2) Add ONLY the first and last end caps as full circles
            // ------------------------------------------------------------
            if (_segs.Count > 0)
            {
                var first = _segs[0];
                var last = _segs[_segs.Count - 1];

                // Start cap at FIRST segment start point, using FIRST segment toolRad
                if (first != null)
                {
                    double toolRad0 = ToolRadWorldForSeg(first);
                    AddCandidateCapCircleEntity(ents, ref eid, circleKeys, first.Index, first.X1, first.Y1, toolRad0);
                }

                // End cap at LAST segment end point, using LAST segment toolRad
                if (last != null)
                {
                    double toolRadN = ToolRadWorldForSeg(last);
                    AddCandidateCapCircleEntity(ents, ref eid, circleKeys, last.Index, last.X2, last.Y2, toolRadN);
                }
            }

            entityCount = ents.Count;
            return ents;
        }


        private static void AddCandidateCapCircleEntity(
            List<CandEntity> ents,
            ref int eid,
            HashSet<(long cx, long cy, long r)> circleKeys,
            int sourceSegIndex,
            double cxW,
            double cyW,
            double rW)
        {
            if (!double.IsFinite(cxW) || !double.IsFinite(cyW) || !double.IsFinite(rW) || rW <= 0.0)
                return;

            // use same de-dupe basis as display (DEDUPE_SCALE)
            long icx = (long)Math.Round(cxW * DEDUPE_SCALE);
            long icy = (long)Math.Round(cyW * DEDUPE_SCALE);
            long ir = (long)Math.Round(rW * DEDUPE_SCALE);
            if (ir <= 0) return;

            if (!circleKeys.Add((icx, icy, ir)))
                return;

            ents.Add(new CandEntity
            {
                Eid = eid++,
                Type = CandEntityType.CircleFull,
                SourceSegIndex = sourceSegIndex,
                SourceKind = "CAP",
                Cx = cxW,
                Cy = cyW,
                R = rW
            });
        }

        private List<CandSnapPoint> BuildCandidateSnapPointsFromEntities(List<CandEntity> ents)
        {
            var inv = CultureInfo.InvariantCulture;

            // Dedup by 5dp key
            var byKey = new Dictionary<string, CandSnapPoint>(4096);

            string FmtKey(double v) => v.ToString($"0.{new string('0', SNAP_KEY_DECIMALS)}", inv);

            void AddSnap(double x, double y, int e1, int e2)
            {
                if (!double.IsFinite(x) || !double.IsFinite(y))
                    return;

                string sx = FmtKey(x);
                string sy = FmtKey(y);
                string key = sx + "|" + sy;

                if (!byKey.TryGetValue(key, out var sp))
                {
                    double qx = double.Parse(sx, inv);
                    double qy = double.Parse(sy, inv);

                    sp = new CandSnapPoint { X = qx, Y = qy };
                    byKey[key] = sp;
                }

                sp.Eids.Add(e1);
                sp.Eids.Add(e2);
            }

            // ------------------------------------------------------------
            // NEW: add ALL candidate endpoints as snap points (your rule set)
            // ------------------------------------------------------------
            for (int i = 0; i < ents.Count; i++)
                AddEndpointSnapsForEntity(ents[i], AddSnap);

            // ------------------------------------------------------------
            // Pairwise intersections (line-line, line-arc, arc-arc; plus cap circle if present)
            // ------------------------------------------------------------
            for (int i = 0; i < ents.Count; i++)
            {
                var a = ents[i];
                for (int j = i + 1; j < ents.Count; j++)
                {
                    var b = ents[j];

                    if (a.Type == CandEntityType.LineSeg && b.Type == CandEntityType.LineSeg)
                    {
                        if (TryIntersectSegSeg(a.X1, a.Y1, a.X2, a.Y2, b.X1, b.Y1, b.X2, b.Y2, out double ix, out double iy))
                            AddSnap(ix, iy, a.Eid, b.Eid);

                        continue;
                    }

                    if (a.Type == CandEntityType.LineSeg && (b.Type == CandEntityType.CircleFull || b.Type == CandEntityType.CircleArc))
                    {
                        AddSegCircleIntersections(a, b, AddSnap);
                        continue;
                    }

                    if (b.Type == CandEntityType.LineSeg && (a.Type == CandEntityType.CircleFull || a.Type == CandEntityType.CircleArc))
                    {
                        AddSegCircleIntersections(b, a, AddSnap);
                        continue;
                    }

                    if ((a.Type == CandEntityType.CircleFull || a.Type == CandEntityType.CircleArc) &&
                        (b.Type == CandEntityType.CircleFull || b.Type == CandEntityType.CircleArc))
                    {
                        if (TryIntersectCircleCircle(a.Cx, a.Cy, a.R, b.Cx, b.Cy, b.R, out var p1, out var p2, out bool two))
                        {
                            if (PointPassesArcFilters(a, p1.X, p1.Y) && PointPassesArcFilters(b, p1.X, p1.Y))
                                AddSnap(p1.X, p1.Y, a.Eid, b.Eid);

                            if (two)
                            {
                                if (PointPassesArcFilters(a, p2.X, p2.Y) && PointPassesArcFilters(b, p2.X, p2.Y))
                                    AddSnap(p2.X, p2.Y, a.Eid, b.Eid);
                            }
                        }

                        continue;
                    }
                }
            }

            return byKey.Values.ToList();
        }


        private void AddSegCircleIntersections(CandEntity seg, CandEntity cir, Action<double, double, int, int> addSnap)
        {
            // seg is line segment, cir is circle (full or arc)
            if (TryIntersectSegCircle(seg.X1, seg.Y1, seg.X2, seg.Y2, cir.Cx, cir.Cy, cir.R, out var p1, out var p2, out bool two))
            {
                if (PointPassesArcFilters(cir, p1.X, p1.Y))
                    addSnap(p1.X, p1.Y, seg.Eid, cir.Eid);

                if (two)
                {
                    if (PointPassesArcFilters(cir, p2.X, p2.Y))
                        addSnap(p2.X, p2.Y, seg.Eid, cir.Eid);
                }
            }
        }

        private static bool PointPassesArcFilters(CandEntity e, double x, double y)
        {
            if (e.Type != CandEntityType.CircleArc)
                return true;

            // Check that the point angle is within the signed sweep from AStart
            double ang = Math.Atan2(y - e.Cy, x - e.Cx);
            double sweepAbs = Math.Abs(e.Sweep);
            if (sweepAbs <= 1e-12)
                return false;

            const double ANG_EPS = 1e-8;

            if (e.Sweep >= 0.0)
            {
                // CCW: ang must be within CCW delta from start
                double d = CcwDelta(e.AStart, ang);
                return d <= sweepAbs + ANG_EPS;
            }
            else
            {
                // CW: distance CW from start to ang == CCW from ang to start
                double d = CcwDelta(ang, e.AStart);
                return d <= sweepAbs + ANG_EPS;
            }
        }

        private static bool TryIntersectSegSeg(
            double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy,
            out double ix, out double iy)
        {
            ix = iy = double.NaN;

            // Solve intersection of segments AB and CD using parametric form:
            // A + t(B-A) = C + u(D-C)
            double rX = bx - ax;
            double rY = by - ay;
            double sX = dx - cx;
            double sY = dy - cy;

            double denom = rX * sY - rY * sX;
            if (Math.Abs(denom) < 1e-12)
                return false; // parallel (or collinear) => ignore here

            double cax = cx - ax;
            double cay = cy - ay;

            double t = (cax * sY - cay * sX) / denom;
            double u = (cax * rY - cay * rX) / denom;

            const double EPS = 1e-9;

            if (t < -EPS || t > 1.0 + EPS || u < -EPS || u > 1.0 + EPS)
                return false;

            ix = ax + t * rX;
            iy = ay + t * rY;
            return double.IsFinite(ix) && double.IsFinite(iy);
        }

        private static bool TryIntersectSegCircle(
            double x1, double y1, double x2, double y2,
            double cx, double cy, double r,
            out Point p1, out Point p2, out bool two)
        {
            p1 = new Point(double.NaN, double.NaN);
            p2 = new Point(double.NaN, double.NaN);
            two = false;

            double dx = x2 - x1;
            double dy = y2 - y1;

            double fx = x1 - cx;
            double fy = y1 - cy;

            double a = dx * dx + dy * dy;
            if (a < 1e-18)
                return false;

            double b = 2.0 * (fx * dx + fy * dy);
            double c = (fx * fx + fy * fy) - r * r;

            double disc = b * b - 4.0 * a * c;
            if (disc < -1e-12)
                return false;

            // clamp tiny negatives
            if (disc < 0.0) disc = 0.0;

            double sqrt = Math.Sqrt(disc);

            double t1 = (-b - sqrt) / (2.0 * a);
            double t2 = (-b + sqrt) / (2.0 * a);

            const double EPS = 1e-9;

            bool hit1 = (t1 >= -EPS && t1 <= 1.0 + EPS);
            bool hit2 = (t2 >= -EPS && t2 <= 1.0 + EPS);

            if (!hit1 && !hit2)
                return false;

            if (hit1)
                p1 = new Point(x1 + t1 * dx, y1 + t1 * dy);

            if (hit2)
            {
                if (!hit1)
                {
                    p1 = new Point(x1 + t2 * dx, y1 + t2 * dy);
                }
                else
                {
                    p2 = new Point(x1 + t2 * dx, y1 + t2 * dy);
                    two = (Math.Abs(sqrt) > 0.0); // disc > 0 => two points
                }
            }

            // If tangency (disc==0), treat as single point
            if (disc == 0.0)
                two = false;

            return true;
        }

        private static bool TryIntersectCircleCircle(
            double x0, double y0, double r0,
            double x1, double y1, double r1,
            out Point p1, out Point p2, out bool two)
        {
            p1 = new Point(double.NaN, double.NaN);
            p2 = new Point(double.NaN, double.NaN);
            two = false;

            double dx = x1 - x0;
            double dy = y1 - y0;
            double d = Math.Sqrt(dx * dx + dy * dy);

            if (d < 1e-12)
                return false; // concentric (or identical) => ignore

            // no intersection
            if (d > r0 + r1 + 1e-12) return false;
            if (d < Math.Abs(r0 - r1) - 1e-12) return false;

            // a = distance from (x0,y0) to line between intersection points
            double a = (r0 * r0 - r1 * r1 + d * d) / (2.0 * d);

            double h2 = r0 * r0 - a * a;
            if (h2 < -1e-12) return false;
            if (h2 < 0.0) h2 = 0.0;

            double h = Math.Sqrt(h2);

            double xm = x0 + a * dx / d;
            double ym = y0 + a * dy / d;

            double rx = -dy * (h / d);
            double ry = dx * (h / d);

            p1 = new Point(xm + rx, ym + ry);

            if (h <= 1e-12)
            {
                // tangent (one point)
                two = false;
                return true;
            }

            p2 = new Point(xm - rx, ym - ry);
            two = true;
            return true;
        }




        private static void AddEndpointSnapsForEntity(CandEntity e, Action<double, double, int, int> addSnap)
        {
            if (e.Type == CandEntityType.LineSeg)
            {
                // lines always add endpoints
                addSnap(e.X1, e.Y1, e.Eid, e.Eid);
                addSnap(e.X2, e.Y2, e.Eid, e.Eid);
                return;
            }

            if (e.Type == CandEntityType.CircleArc)
            {
                // arcs add endpoints (outer/inner/pie arcs are represented as CircleArc entities)
                double a0 = e.AStart;
                double a1 = e.AStart + e.Sweep;

                double x0 = e.Cx + Math.Cos(a0) * e.R;
                double y0 = e.Cy + Math.Sin(a0) * e.R;

                double x1p = e.Cx + Math.Cos(a1) * e.R;
                double y1p = e.Cy + Math.Sin(a1) * e.R;

                addSnap(x0, y0, e.Eid, e.Eid);
                addSnap(x1p, y1p, e.Eid, e.Eid);
                return;
            }

            // CircleFull has no endpoints
        }

















































        private static Dictionary<(int gx, int gy), List<int>> BuildCandidateGrid(List<CandSnapPoint> candidates, double cell)
        {
            var grid = new Dictionary<(int gx, int gy), List<int>>(4096);

            if (candidates == null || candidates.Count == 0)
                return grid;

            if (!double.IsFinite(cell) || cell <= 0.0)
                cell = 0.1;

            for (int i = 0; i < candidates.Count; i++)
            {
                var p = candidates[i];
                int gx = (int)Math.Floor(p.X / cell);
                int gy = (int)Math.Floor(p.Y / cell);

                var key = (gx, gy);
                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<int>(8);
                    grid[key] = list;
                }
                list.Add(i);
            }

            return grid;
        }

        private List<CandSnapPoint> CollectBoundarySnapsByClipperVertexOrder(
            List<Point> loopPts,
            List<CandSnapPoint> candidates,
            Dictionary<(int gx, int gy), List<int>> grid,
            int[] usedMark,
            int curMark,
            double tol)
        {
            var outList = new List<CandSnapPoint>(loopPts.Count);
            if (loopPts == null || loopPts.Count == 0 || candidates == null || candidates.Count == 0)
                return outList;

            double tol2 = tol * tol;
            double cell = tol;

            // Walk clipper vertices in order.
            for (int i = 0; i < loopPts.Count; i++)
            {
                double x = loopPts[i].X;
                double y = loopPts[i].Y;

                int gx = (int)Math.Floor(x / cell);
                int gy = (int)Math.Floor(y / cell);

                int bestIdx = -1;
                double bestD2 = double.PositiveInfinity;

                // Check this cell + neighbors (3x3)
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        var key = (gx + ox, gy + oy);
                        if (!grid.TryGetValue(key, out var list))
                            continue;

                        for (int k = 0; k < list.Count; k++)
                        {
                            int idx = list[k];

                            // "removed from potential list so we dont find it again"
                            if (idx < 0 || idx >= usedMark.Length)
                                continue;
                            if (usedMark[idx] == curMark)
                                continue;

                            double dx = candidates[idx].X - x;
                            double dy = candidates[idx].Y - y;
                            double d2 = dx * dx + dy * dy;

                            if (d2 <= tol2 && d2 < bestD2)
                            {
                                bestD2 = d2;
                                bestIdx = idx;
                            }
                        }
                    }
                }

                if (bestIdx >= 0)
                {
                    usedMark[bestIdx] = curMark;
                    outList.Add(candidates[bestIdx]);
                }
            }

            return outList;
        }



        private static void AppendSnapListNumericOnly(System.Text.StringBuilder sb, List<CandSnapPoint> snaps)
        {
            if (sb == null)
                return;

            if (snaps == null || snaps.Count == 0)
                return;

            var inv = CultureInfo.InvariantCulture;
            string fmt = $"0.{new string('0', SNAP_KEY_DECIMALS)}";

            for (int i = 0; i < snaps.Count; i++)
            {
                sb.Append(snaps[i].X.ToString(fmt, inv));
                sb.Append(' ');
                sb.Append(snaps[i].Y.ToString(fmt, inv));
                sb.Append(' ');
                sb.Append(0.0.ToString(fmt, inv));   // z
                sb.AppendLine();
            }
        }

        private static double DistSq(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// Post-pass cleanup:
        /// - If consecutive points are within SNAP_TOL, delete the later one.
        /// - After any deletion, restart from the top (per your rule).
        /// - Finally, close the loop by repeating the first point at the end (if not already).
        /// </summary>
        private void ReduceAndCloseBoundarySnapListInPlace(List<CandSnapPoint> ordered)
        {
            if (ordered == null || ordered.Count == 0)
                return;

            double tol = SNAP_TOL;
            double tolSq = tol * tol;

            // 1) Kill consecutive near-duplicates (restart from top after any removal)
            bool removed;
            do
            {
                removed = false;

                for (int i = 1; i < ordered.Count; i++)
                {
                    var a = ordered[i - 1];
                    var b = ordered[i];

                    if (DistSq(a.X, a.Y, b.X, b.Y) <= tolSq)
                    {
                        ordered.RemoveAt(i);
                        removed = true;
                        break; // restart from top
                    }
                }
            }
            while (removed);

            if (ordered.Count == 0)
                return;

            // 2) Close shape: append first point to end (only if not already essentially closed)
            var first = ordered[0];
            var last = ordered[ordered.Count - 1];

            // if last is already basically the first, force it to exact first (for NX cleanliness)
            if (DistSq(first.X, first.Y, last.X, last.Y) <= tolSq)
            {
                last.X = first.X;
                last.Y = first.Y;
                return;
            }

            ordered.Add(new CandSnapPoint
            {
                X = first.X,
                Y = first.Y
                // Eids intentionally not carried; this is just closure for point export
            });
        }



        // ============================================================
        // NEXT STAGE: build per-segment clipper point runs BETWEEN snaps
        // - We must keep the clipper vertex index for each found snap
        // - Then for each consecutive snap pair, collect interior clipper
        //   points (excluding those within SNAP_TOL of either endpoint).
        // - If interiorCount < MIN_ARC_POINTS => LINE run => [snapA, snapB]
        // - Else => ARC run => [snapA, ...interior..., snapB]
        // ============================================================

        private sealed class BoundarySnapHit
        {
            public CandSnapPoint Snap = new CandSnapPoint();
            public int ClipperIndex; // index into loop.WorldPts (0..N-1)
        }

        private List<BoundarySnapHit> CollectBoundarySnapHitsByClipperVertexOrder(
            List<Point> loopPts,
            List<CandSnapPoint> candidates,
            Dictionary<(int gx, int gy), List<int>> grid,
            int[] usedMark,
            int curMark,
            double tol)
        {
            var outList = new List<BoundarySnapHit>(Math.Max(16, loopPts?.Count ?? 0));
            if (loopPts == null || loopPts.Count == 0 || candidates == null || candidates.Count == 0)
                return outList;

            double tol2 = tol * tol;
            double cell = tol;

            // Walk clipper vertices in order.
            for (int i = 0; i < loopPts.Count; i++)
            {
                double x = loopPts[i].X;
                double y = loopPts[i].Y;

                int gx = (int)Math.Floor(x / cell);
                int gy = (int)Math.Floor(y / cell);

                int bestIdx = -1;
                double bestD2 = double.PositiveInfinity;

                // Check this cell + neighbors (3x3)
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        var key = (gx + ox, gy + oy);
                        if (!grid.TryGetValue(key, out var list))
                            continue;

                        for (int k = 0; k < list.Count; k++)
                        {
                            int idx = list[k];

                            // "removed from potential list so we dont find it again"
                            if (idx < 0 || idx >= usedMark.Length)
                                continue;
                            if (usedMark[idx] == curMark)
                                continue;

                            double dx = candidates[idx].X - x;
                            double dy = candidates[idx].Y - y;
                            double d2 = dx * dx + dy * dy;

                            if (d2 <= tol2 && d2 < bestD2)
                            {
                                bestD2 = d2;
                                bestIdx = idx;
                            }
                        }
                    }
                }

                if (bestIdx >= 0)
                {
                    usedMark[bestIdx] = curMark;

                    outList.Add(new BoundarySnapHit
                    {
                        Snap = candidates[bestIdx],
                        ClipperIndex = i
                    });
                }
            }

            return outList;
        }

        /// <summary>
        /// Same logic as ReduceAndCloseBoundarySnapListInPlace, but keeps ClipperIndex too.
        /// - Remove consecutive hits whose snap points are within SNAP_TOL (restart after any removal)
        /// - Close loop by repeating first hit at end (if not already essentially closed)
        /// </summary>
        private void ReduceAndCloseBoundarySnapHitListInPlace(List<BoundarySnapHit> orderedHits)
        {
            if (orderedHits == null || orderedHits.Count == 0)
                return;

            double tol = SNAP_TOL;
            double tolSq = tol * tol;

            bool removed;
            do
            {
                removed = false;

                for (int i = 1; i < orderedHits.Count; i++)
                {
                    var a = orderedHits[i - 1].Snap;
                    var b = orderedHits[i].Snap;

                    if (DistSq(a.X, a.Y, b.X, b.Y) <= tolSq)
                    {
                        orderedHits.RemoveAt(i);
                        removed = true;
                        break; // restart
                    }
                }
            }
            while (removed);

            if (orderedHits.Count == 0)
                return;

            // Close shape by repeating first at end (or force last==first)
            var first = orderedHits[0].Snap;
            var last = orderedHits[orderedHits.Count - 1].Snap;

            if (DistSq(first.X, first.Y, last.X, last.Y) <= tolSq)
            {
                // force exact closure for cleanliness
                last.X = first.X;
                last.Y = first.Y;
                return;
            }

            orderedHits.Add(new BoundarySnapHit
            {
                Snap = new CandSnapPoint
                {
                    X = first.X,
                    Y = first.Y
                },
                ClipperIndex = orderedHits[0].ClipperIndex
            });
        }

        private static void AppendPointListNumericOnly(System.Text.StringBuilder sb, List<Point> pts)
        {
            if (sb == null || pts == null || pts.Count == 0)
                return;

            var inv = CultureInfo.InvariantCulture;
            string fmt = $"0.{new string('0', SNAP_KEY_DECIMALS)}";

            for (int i = 0; i < pts.Count; i++)
            {
                sb.Append(pts[i].X.ToString(fmt, inv));
                sb.Append(' ');
                sb.Append(pts[i].Y.ToString(fmt, inv));
                sb.Append(' ');
                sb.Append(0.0.ToString(fmt, inv)); // z
                sb.AppendLine();
            }
        }

        private static Point SnapToPoint(CandSnapPoint s) => new Point(s.X, s.Y);

        private List<List<Point>> BuildRunsBetweenSnapsForLoop(
            List<Point> loopPts,
            List<BoundarySnapHit> orderedClosedHits)
        {
            var runs = new List<List<Point>>();
            if (loopPts == null || loopPts.Count == 0)
                return runs;

            if (orderedClosedHits == null || orderedClosedHits.Count < 2)
                return runs;

            int n = loopPts.Count;
            double tol = SNAP_TOL;
            double tolSq = tol * tol;

            for (int si = 0; si < orderedClosedHits.Count - 1; si++)
            {
                var aHit = orderedClosedHits[si];
                var bHit = orderedClosedHits[si + 1];

                var aSnap = aHit.Snap;
                var bSnap = bHit.Snap;

                var aPt = SnapToPoint(aSnap);
                var bPt = SnapToPoint(bSnap);

                // Collect interior clipper vertices along the loop from aIdx -> bIdx (wrapping)
                int aIdx = aHit.ClipperIndex;
                int bIdx = bHit.ClipperIndex;

                // Start building this run with snapA
                var interior = new List<Point>(64);

                int idx = (aIdx + 1) % n;
                int guard = 0;

                while (idx != bIdx && guard < n + 5)
                {
                    var p = loopPts[idx];

                    // Exclude points that are "inside SNAP_TOL" of either endpoint snap
                    if (DistSq(p.X, p.Y, aPt.X, aPt.Y) > tolSq &&
                        DistSq(p.X, p.Y, bPt.X, bPt.Y) > tolSq)
                    {
                        interior.Add(p);
                    }

                    idx = (idx + 1) % n;
                    guard++;
                }

                // Build final run list
                // Rule: if interiorCount < MIN_ARC_POINTS => line => [snapA, snapB]
                if (interior.Count < MIN_ARC_POINTS)
                {
                    runs.Add(new List<Point>(2) { aPt, bPt });
                }
                else
                {
                    var run = new List<Point>(interior.Count + 2);
                    run.Add(aPt);
                    run.AddRange(interior);
                    run.Add(bPt);
                    runs.Add(run);
                }
            }

            return runs;
        }

        // ======================================================================
        // FREECAD-FRIENDLY PRIMITIVE FORMAT (NO CW/CCW)
        // LINE x1 y1   x2 y2
        // ARC  x1 y1   xm ym   x2 y2    (3-point arc, direction NOT encoded)
        //
        // For ARC runs: xm,ym comes from the "near mid" index of the run list.
        // ======================================================================

        private static void AppendRunAsFreecadPrimitive(System.Text.StringBuilder sb, List<Point> run)
        {
            if (sb == null || run == null || run.Count < 2)
                return;

            var inv = CultureInfo.InvariantCulture;
            string fmt = $"0.{new string('0', SNAP_KEY_DECIMALS)}";

            // LINE: exactly two points
            if (run.Count < MIN_ARC_POINTS + 2)
            {
                var a = run[0];
                var b = run[run.Count - 1];

                sb.Append("LINE ");
                sb.Append(a.X.ToString(fmt, inv)); sb.Append(' ');
                sb.Append(a.Y.ToString(fmt, inv)); sb.Append("   ");
                sb.Append(b.X.ToString(fmt, inv)); sb.Append(' ');
                sb.Append(b.Y.ToString(fmt, inv));
                sb.AppendLine();
                return;
            }

            // ARC: start, mid(nearest middle), end
            int midIdx = run.Count / 2; // "near mid" index
            if (midIdx <= 0) midIdx = 1;
            if (midIdx >= run.Count - 1) midIdx = run.Count - 2;

            var p1 = run[0];
            var pm = run[midIdx];
            var p2 = run[run.Count - 1];

            sb.Append("ARC ");
            sb.Append(p1.X.ToString(fmt, inv)); sb.Append(' ');
            sb.Append(p1.Y.ToString(fmt, inv)); sb.Append("   ");
            sb.Append(pm.X.ToString(fmt, inv)); sb.Append(' ');
            sb.Append(pm.Y.ToString(fmt, inv)); sb.Append("   ");
            sb.Append(p2.X.ToString(fmt, inv)); sb.Append(' ');
            sb.Append(p2.Y.ToString(fmt, inv));
            sb.AppendLine();
        }

        private static void AppendRunsAsFreecadPrimitives(System.Text.StringBuilder sb, List<List<Point>> runs)
        {
            if (sb == null || runs == null || runs.Count == 0)
                return;

            for (int i = 0; i < runs.Count; i++)
                AppendRunAsFreecadPrimitive(sb, runs[i]);
        }

        // ======================================================================
        // TRUE-SHAPE DISPLAY (reverse-engineered from the python-friendly runs)
        // Outer:  Blue stroke, Orange fill
        // Islands: White stroke, DarkGrey fill
        //
        // Uses the SAME "runs between snaps" you already built.
        // ARC direction is inferred from (p1, pm, p2) via GetSweep().
        // ======================================================================

        private sealed class TruePrim
        {
            public string Type = ""; // "LINE" or "ARC"
            public Point P1;
            public Point Pm; // arc only
            public Point P2;
        }









        // ======================================================================
        // STAGE 2: Build Python-friendly primitives (LINE / ARC) from
        //          (a) ordered boundary snaps and (b) the Clipper union vertices,
        //          then DISPLAY those primitives on TopCanvas (current zoom/pan).
        //
        // Rules:
        //  - For each boundary segment (Snap[i] -> Snap[i+1]) gather clipper points
        //    between them (excluding points within SNAP_TOL of either snap).
        //  - If the gathered list has < MIN_ARC_POINTS => LINE
        //    else => ARC using midpoint index for Xm,Ym.
        //  - ARC text output uses "ARC" only (no CW/CCW).
        //  - Display colors:
        //      OUTER   stroke BLUE,  fill ORANGE
        //      ISLANDS stroke WHITE, fill DARK GREY
        // ======================================================================

        private enum PyPrimType { Line, Arc }

        private sealed class PyPrim
        {
            public PyPrimType Type;
            public double X1, Y1;
            public double Xm, Ym; // arc only (mid point on arc)
            public double X2, Y2;

            public override string ToString()
            {
                // debug
                return (Type == PyPrimType.Line)
                    ? $"LINE {X1} {Y1}   {X2} {Y2}"
                    : $"ARC {X1} {Y1}   {Xm} {Ym}   {X2} {Y2}";
            }
        }

        private static bool NearSnapPoint(Point p, CandSnapPoint s, double tolSq)
        {
            double dx = p.X - s.X;
            double dy = p.Y - s.Y;
            return (dx * dx + dy * dy) <= tolSq;
        }

        private static bool NearSnapPoint(CandSnapPoint a, CandSnapPoint b, double tolSq)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return (dx * dx + dy * dy) <= tolSq;
        }

        private static int FindStartIndexNearSnap(List<Point> loopPts, CandSnapPoint snap0, double tolSq)
        {
            if (loopPts == null || loopPts.Count == 0)
                return 0;

            int best = -1;
            double bestD2 = double.PositiveInfinity;

            for (int i = 0; i < loopPts.Count; i++)
            {
                double dx = loopPts[i].X - snap0.X;
                double dy = loopPts[i].Y - snap0.Y;
                double d2 = dx * dx + dy * dy;

                if (d2 <= tolSq && d2 < bestD2)
                {
                    bestD2 = d2;
                    best = i;
                }
            }

            return (best >= 0) ? best : 0;
        }

        /// <summary>
        /// Build per-segment point runs between each consecutive snap pair.
        /// Each run includes: start snap, (0..n) internal clipper pts, end snap.
        /// For true lines, you *expect* no internal points.
        /// </summary>
        private List<List<Point>> BuildPointRunsBetweenSnapsInClipperOrder(
            List<Point> loopPtsOpen,
            List<CandSnapPoint> snapsClosed,
            double tol)
        {
            var runs = new List<List<Point>>();

            if (loopPtsOpen == null || loopPtsOpen.Count < 2)
                return runs;

            if (snapsClosed == null || snapsClosed.Count < 2)
                return runs;

            // snapsClosed must be closed: last == first (within tol). We treat segment count = snapsClosed.Count - 1.
            int segCount = snapsClosed.Count - 1;
            if (segCount < 1)
                return runs;

            double tolSq = tol * tol;

            // Find clipper index near first snap
            int startIdx = FindStartIndexNearSnap(loopPtsOpen, snapsClosed[0], tolSq);

            int snapIdx = 0; // current snap start = snapsClosed[snapIdx], target end = snapsClosed[snapIdx+1]
            var cur = new List<Point>(64);

            // start with exact snap point (not the noisy clipper vertex)
            cur.Add(new Point(snapsClosed[0].X, snapsClosed[0].Y));

            int guard = 0;
            int maxIter = loopPtsOpen.Count + 8; // enough to wrap once

            for (int step = 0; step < maxIter; step++)
            {
                int i = (startIdx + step) % loopPtsOpen.Count;
                Point p = loopPtsOpen[i];

                // If we already finished all segments, stop.
                if (snapIdx >= segCount)
                    break;

                var sA = snapsClosed[snapIdx];
                var sB = snapsClosed[snapIdx + 1];

                // If we reached next snap (within tol), close this run and start next.
                if (NearSnapPoint(p, sB, tolSq))
                {
                    // close run with exact snap B
                    cur.Add(new Point(sB.X, sB.Y));
                    runs.Add(cur);

                    snapIdx++;

                    if (snapIdx >= segCount)
                        break;

                    // start next run with exact snap B
                    cur = new List<Point>(64);
                    cur.Add(new Point(sB.X, sB.Y));

                    continue;
                }

                // Otherwise, this is an internal point ONLY if it's not within tol of either endpoint snap.
                if (!NearSnapPoint(p, sA, tolSq) && !NearSnapPoint(p, sB, tolSq))
                {
                    // keep the raw clipper vertex (world)
                    cur.Add(p);
                }

                guard++;
                if (guard > loopPtsOpen.Count * 2)
                    break; // safety
            }

            return runs;
        }

        private List<PyPrim> BuildPythonFriendlyPrimsFromRuns(List<List<Point>> runs)
        {
            var prims = new List<PyPrim>(runs?.Count ?? 0);

            if (runs == null || runs.Count == 0)
                return prims;

            for (int i = 0; i < runs.Count; i++)
            {
                var pts = runs[i];
                if (pts == null || pts.Count < 2)
                    continue;

                var p1 = pts[0];
                var p2 = pts[pts.Count - 1];

                if (pts.Count < MIN_ARC_POINTS)
                {
                    prims.Add(new PyPrim
                    {
                        Type = PyPrimType.Line,
                        X1 = p1.X,
                        Y1 = p1.Y,
                        X2 = p2.X,
                        Y2 = p2.Y
                    });
                }
                else
                {
                    int midIdx = pts.Count / 2; // "near mid index"
                    var pm = pts[midIdx];

                    prims.Add(new PyPrim
                    {
                        Type = PyPrimType.Arc,
                        X1 = p1.X,
                        Y1 = p1.Y,
                        Xm = pm.X,
                        Ym = pm.Y,
                        X2 = p2.X,
                        Y2 = p2.Y
                    });
                }
            }

            return prims;
        }

        private static void AppendPythonFriendlyPrimsToSb(System.Text.StringBuilder sb, List<PyPrim> prims)
        {
            if (sb == null || prims == null || prims.Count == 0)
                return;

            var inv = CultureInfo.InvariantCulture;
            string fmt = $"0.{new string('0', SNAP_KEY_DECIMALS)}";

            for (int i = 0; i < prims.Count; i++)
            {
                var p = prims[i];

                if (p.Type == PyPrimType.Line)
                {
                    sb.Append("LINE ");
                    sb.Append(p.X1.ToString(fmt, inv)); sb.Append(' ');
                    sb.Append(p.Y1.ToString(fmt, inv)); sb.Append("   ");
                    sb.Append(p.X2.ToString(fmt, inv)); sb.Append(' ');
                    sb.Append(p.Y2.ToString(fmt, inv));
                    sb.AppendLine();
                }
                else
                {
                    sb.Append("ARC ");
                    sb.Append(p.X1.ToString(fmt, inv)); sb.Append(' ');
                    sb.Append(p.Y1.ToString(fmt, inv)); sb.Append("   ");
                    sb.Append(p.Xm.ToString(fmt, inv)); sb.Append(' ');
                    sb.Append(p.Ym.ToString(fmt, inv)); sb.Append("   ");
                    sb.Append(p.X2.ToString(fmt, inv)); sb.Append(' ');
                    sb.Append(p.Y2.ToString(fmt, inv));
                    sb.AppendLine();
                }
            }
        }

        /// <summary>
        /// Render the Python-friendly primitives as CLOSED filled loops on TopCanvas,
        /// using the CURRENT viewport (no Fit/Reset).
        /// </summary>
        private void RenderPythonFriendlyPrimitiveLoops(
            List<List<PyPrim>> outerLoops,
            List<List<PyPrim>> islandLoops)
        {
            TopCanvas.Children.Clear();

            // Ensure settings-driven widths/colors are up to date
            ApplyViewerStylesFromSettings();

            // Requested display colors
            Brush outerStroke = DebugPalette.WireOuterStroke;
            Brush outerFill = DebugPalette.WireOuterFill;

            Brush islandStroke = DebugPalette.WireIslandStroke;
            Brush islandFill = DebugPalette.WireIslandFill;

            double thk = Math.Max(1.0, _profileWidth + 0.5);

            void DrawLoop(List<PyPrim> prims, Brush stroke, Brush fill)
            {
                if (prims == null || prims.Count == 0)
                    return;

                // Build a PathGeometry (screen coords)
                var fig = new PathFigure();
                fig.IsClosed = true;
                fig.IsFilled = true;

                // start
                var p0 = prims[0];
                fig.StartPoint = W2S(p0.X1, p0.Y1);

                for (int i = 0; i < prims.Count; i++)
                {
                    var pr = prims[i];

                    if (pr.Type == PyPrimType.Line)
                    {
                        fig.Segments.Add(new LineSegment
                        {
                            Point = W2S(pr.X2, pr.Y2),
                            IsStroked = true
                        });
                    }
                    else
                    {
                        // For drawing: compute circle from 3 points so the WPF ArcSegment matches the true arc.
                        if (!TryCircleFrom3Points(pr.X1, pr.Y1, pr.Xm, pr.Ym, pr.X2, pr.Y2, out double cx, out double cy, out double r))
                        {
                            // fallback
                            fig.Segments.Add(new LineSegment { Point = W2S(pr.X2, pr.Y2), IsStroked = true });
                        }
                        else
                        {
                            // Screen-space points
                            Point cS = W2S(cx, cy);
                            Point p1S = W2S(pr.X1, pr.Y1);
                            Point pmS = W2S(pr.Xm, pr.Ym);
                            Point p2S = W2S(pr.X2, pr.Y2);

                            // radius in screen units (uniform scale)
                            double rS = WR2SR(r);

                            // Determine sweep that passes through mid
                            GetSweep(cS, p1S, pmS, p2S, out SweepDirection sd, out bool isLargeArc);

                            fig.Segments.Add(new ArcSegment
                            {
                                Point = p2S,
                                Size = new Size(rS, rS),
                                RotationAngle = 0.0,
                                IsLargeArc = isLargeArc,
                                SweepDirection = sd,
                                IsStroked = true
                            });
                        }
                    }
                }

                var geo = new PathGeometry(new[] { fig })
                {
                    FillRule = (System.Windows.Media.FillRule)1
                };
                geo.Freeze();

                var path = new Path
                {
                    Data = geo,
                    Stroke = stroke,
                    Fill = fill,
                    StrokeThickness = thk
                };

                TopCanvas.Children.Add(path);
            }

            // OUTER first, then islands
            if (outerLoops != null)
            {
                for (int i = 0; i < outerLoops.Count; i++)
                    DrawLoop(outerLoops[i], outerStroke, outerFill);
            }

            if (islandLoops != null)
            {
                for (int i = 0; i < islandLoops.Count; i++)
                    DrawLoop(islandLoops[i], islandStroke, islandFill);
            }

            var info = new TextBlock
            {
                Text = $"PY-PRIMS DISPLAY: outerLoops={outerLoops?.Count ?? 0}  islandLoops={islandLoops?.Count ?? 0}  MIN_ARC_POINTS={MIN_ARC_POINTS}  SNAP_TOL={SNAP_TOL}",
                Foreground = _graphicText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            };
            Canvas.SetLeft(info, 10);
            Canvas.SetTop(info, 10);
            TopCanvas.Children.Add(info);
        }





        // ============================================================
        // CLIPPER EXPORT API
        // - Builds ONLY the python-friendly loop headers + LINE/ARC prims.
        // - No log window. No rendering. No viewport changes.
        // - Result is cached so MillPage can fetch it after any build.
        // ============================================================

        private string _lastClipperExportText = "";

        /// <summary>
        /// Last built clipper export text (python-friendly loops).
        /// Empty if it hasn't been built yet.
        /// </summary>
        public string LastClipperExportText => _lastClipperExportText ?? "";

        /// <summary>
        /// Build the clipper-based boundary as python-friendly primitives text:
        ///   --- LOOP i  OUTER ---
        ///   LINE ...
        ///   ARC  ...
        ///   --- LOOP j  ISLAND ---
        ///   ...
        /// This is the string your FreeCAD clipper script should consume.
        /// </summary>
        public string BuildClipperExportText(bool includeIslands = true)
        {
            // Closed CL Wire mode uses a completely separate export format:
            //  - INNER:  one OUTER loop (the closed wire)
            //  - OUTER:  one OUTER loop (bounding box) + one ISLAND loop (the closed wire)
            // It does NOT use the toolpath/clipper cleanup pipeline.
            if (HasAnyClosedWire())
            {
                _lastClipperExportText = BuildClosedWireExportText();
                return _lastClipperExportText;
            }

            if (_segs == null || _segs.Count == 0)
            {
                _lastClipperExportText = "";
                return _lastClipperExportText;
            }

            // 1) Build ALL candidate snap points (analytic)
            int entityCount;
            var ents = BuildCandidateEntitiesForSnap(out entityCount);
            var candSnaps = BuildCandidateSnapPointsFromEntities(ents);

            // 2) Build Clipper union loops (outer + islands)
            int subjCount, resultCount;
            long totalVerts;
            var loops = BuildClipperUnionLoopsWorld(out subjCount, out resultCount, out totalVerts);

            // 3) Build spatial grid for candidates (fast "within SNAP_TOL" lookup)
            var grid = BuildCandidateGrid(candSnaps, SNAP_TOL);

            // "Used" marking per-loop without allocating bool[] each loop
            int[] usedMark = new int[Math.Max(1, candSnaps.Count)];
            int curMark = 1;

            // Keep pairing between a specific loop and its ordered snap list
            var loopData = new List<(ClipperUnionLoop Loop, List<CandSnapPoint> OrderedSnaps)>();

            foreach (var loop in loops)
            {
                if (loop == null || loop.WorldPts == null || loop.WorldPts.Count < 3)
                    continue;

                curMark++;
                if (curMark == int.MaxValue)
                {
                    Array.Clear(usedMark, 0, usedMark.Length);
                    curMark = 1;
                }

                var ordered = CollectBoundarySnapsByClipperVertexOrder(loop.WorldPts, candSnaps, grid, usedMark, curMark, SNAP_TOL);
                if (ordered.Count == 0)
                    continue;

                // Your cleanup + close (kills consecutive near-duplicates, restart-on-delete, then closes)
                ReduceAndCloseBoundarySnapListInPlace(ordered);
                if (ordered.Count < 3)
                    continue;

                loopData.Add((loop, ordered));
            }

            // 4) Build python-friendly LINE/ARC prim loops
            var outerPyLoops = new List<List<PyPrim>>();
            var islandPyLoops = new List<List<PyPrim>>();

            for (int i = 0; i < loopData.Count; i++)
            {
                var lp = loopData[i].Loop;
                var snapsClosed = loopData[i].OrderedSnaps;

                // Build point runs between snaps (snap, [clipper..], snap)
                var runs = BuildPointRunsBetweenSnapsInClipperOrder(lp.WorldPts, snapsClosed, SNAP_TOL);

                // Convert runs -> LINE/ARC prims
                var prims = BuildPythonFriendlyPrimsFromRuns(runs);
                if (prims.Count == 0)
                    continue;

                if (lp.IsHole)
                {
                    if (includeIslands)
                        islandPyLoops.Add(prims);
                }
                else
                {
                    outerPyLoops.Add(prims);
                }
            }

            // 5) Emit ONLY what python should parse (no "===" banners, no snap dumps)
            var sb = new System.Text.StringBuilder(256 * 1024);

            for (int i = 0; i < outerPyLoops.Count; i++)
            {
                sb.AppendLine($"--- LOOP {i}  OUTER ---");
                AppendPythonFriendlyPrimsToSb(sb, outerPyLoops[i]);
                sb.AppendLine();
            }

            if (includeIslands)
            {
                for (int i = 0; i < islandPyLoops.Count; i++)
                {
                    sb.AppendLine($"--- LOOP {i}  ISLAND ---");
                    AppendPythonFriendlyPrimsToSb(sb, islandPyLoops[i]);
                    sb.AppendLine();
                }
            }

            _lastClipperExportText = sb.ToString();
            return _lastClipperExportText;
        }



        private string BuildClosedWireExportText()
        {
            if (_segs == null || _segs.Count == 0)
                return "";

            // ClosedWire export expects exactly one region (one closed wire).
            // If the viewer somehow contains mixed regions, fail fast.
            string regionName = _segs[0].RegionName ?? "";
            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];
                if (s == null) continue;

                if (!s.IsClosedWire)
                    throw new Exception("Closed CL Wire export called with GuidedTool segments present.");

                if (!string.Equals(regionName, s.RegionName ?? "", StringComparison.Ordinal))
                    throw new Exception("Closed CL Wire export supports a single MILL set at a time.");
            }

            bool inner = _segs[0].ClosedWireInner;
            bool outer = _segs[0].ClosedWireOuter;

            // Profile prim loop (must be a single closed chain)
            if (!TryBuildClosedWirePrimLoop(_segs, out var profilePrims, out string failReason))
            {
                // Must not silently succeed
                MessageBox.Show(
                    $"MILL set \"{regionName}\": Closed CL Wire selected but loop is not closed.\n{failReason}\n\nThis path can only use GuidedTool.",
                    "Closed CL Wire : Not Closed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return "";
            }

            var sb = new System.Text.StringBuilder(64 * 1024);

            if (outer)
            {
                // OUTER = bounding box, ISLAND = profile loop
                var boxPrims = BuildClosedWireBoundingBoxPrimLoop(_segs, padMm: 100.0);

                sb.AppendLine("--- LOOP 0  OUTER ---");
                AppendPythonFriendlyPrimsToSb(sb, boxPrims);
                sb.AppendLine();

                sb.AppendLine("--- LOOP 0  ISLAND ---");
                AppendPythonFriendlyPrimsToSb(sb, profilePrims);
                sb.AppendLine();
            }
            else
            {
                // INNER default (also used if neither inner/outer flagged): OUTER = profile only
                sb.AppendLine("--- LOOP 0  OUTER ---");
                AppendPythonFriendlyPrimsToSb(sb, profilePrims);
                sb.AppendLine();
            }

            return sb.ToString();
        }


        private bool TryBuildClosedWirePrimLoop(List<PathSeg> segs, out List<PyPrim> prims, out string failReason)
        {
            prims = new List<PyPrim>();
            failReason = "";

            if (segs == null || segs.Count == 0)
            {
                failReason = "Empty path.";
                return false;
            }

            double curX = segs[0].X1;
            double curY = segs[0].Y1;
            double startX = curX;
            double startY = curY;

            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];

                if (!WorldNear(curX, curY, s.X1, s.Y1, SNAP_TOL))
                {
                    failReason = $"Path is not a single chained wire (break at seg {s.Index}).";
                    return false;
                }

                if (s.Type == "LINE")
                {
                    prims.Add(new PyPrim
                    {
                        Type = PyPrimType.Line,
                        X1 = s.X1,
                        Y1 = s.Y1,
                        X2 = s.X2,
                        Y2 = s.Y2
                    });
                }
                else if (s.Type == "ARC3_CW" || s.Type == "ARC3_CCW")
                {
                    prims.Add(new PyPrim
                    {
                        Type = PyPrimType.Arc,
                        X1 = s.X1,
                        Y1 = s.Y1,
                        Xm = s.Xm,
                        Ym = s.Ym,
                        X2 = s.X2,
                        Y2 = s.Y2
                    });
                }
                else
                {
                    failReason = $"Unsupported seg type: {s.Type} (seg {s.Index}).";
                    return false;
                }

                curX = s.X2;
                curY = s.Y2;
            }

            if (!WorldNear(curX, curY, startX, startY, SNAP_TOL))
            {
                failReason = "Loop not closed.";
                return false;
            }

            return true;
        }


        private List<PyPrim> BuildClosedWireBoundingBoxPrimLoop(List<PathSeg> segs, double padMm)
        {
            // Use sampled boundary points for accurate bounds (arcs included)
            if (!TryBuildWireDisplayPointsWorld(segs, requireClosedLoop: true, out var ptsWorld, out _))
                ptsWorld = new List<Point>();

            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;

            if (ptsWorld.Count > 0)
            {
                for (int i = 0; i < ptsWorld.Count; i++)
                {
                    var p = ptsWorld[i];
                    minX = Math.Min(minX, p.X);
                    maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxY = Math.Max(maxY, p.Y);
                }
            }
            else
            {
                // Fallback to endpoints only
                for (int i = 0; i < segs.Count; i++)
                {
                    var s = segs[i];
                    minX = Math.Min(minX, Math.Min(s.X1, s.X2));
                    maxX = Math.Max(maxX, Math.Max(s.X1, s.X2));
                    minY = Math.Min(minY, Math.Min(s.Y1, s.Y2));
                    maxY = Math.Max(maxY, Math.Max(s.Y1, s.Y2));
                }
            }

            if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
            {
                minX = -1; maxX = 1; minY = -1; maxY = 1;
            }

            minX -= padMm;
            maxX += padMm;
            minY -= padMm;
            maxY += padMm;

            // Match the example ordering:
            // (maxX,maxY)->(maxX,minY)->(minX,minY)->(minX,maxY)->(maxX,maxY)
            var prims = new List<PyPrim>(4)
            {
                new PyPrim { Type = PyPrimType.Line, X1 = maxX, Y1 = maxY, X2 = maxX, Y2 = minY },
                new PyPrim { Type = PyPrimType.Line, X1 = maxX, Y1 = minY, X2 = minX, Y2 = minY },
                new PyPrim { Type = PyPrimType.Line, X1 = minX, Y1 = minY, X2 = minX, Y2 = maxY },
                new PyPrim { Type = PyPrimType.Line, X1 = minX, Y1 = maxY, X2 = maxX, Y2 = maxY }
            };

            return prims;
        }






        public void LoadSegmentsForClipper(List<PathSeg> segs)
        {
            // Clear cached export so next BuildClipperExportText recomputes
            _lastClipperExportText = "";

            _segsRaw = segs ?? new List<PathSeg>();
            _segs = TransformSegmentsForView(_segsRaw, true); // keep viewer rule: apply view-only transform here
        }









        private void BtnTrueShape_Click(object sender, RoutedEventArgs e)
        {
            // Closed CL Wire mode: TrueShape / Clipper pipeline is not used.
            if (HasAnyClosedWire())
            {
                ShowClosedWireButtonsNotUsedMessage();
                return;
            }

            if (_segs == null || _segs.Count == 0)
                return;

            // Build and cache the export text (this is the same text export will use)
            // (Must NOT show any windows here.)
            string exportText = BuildClipperExportText(includeIslands: true);

            // IMPORTANT: Keep current zoom/pan. Do NOT Fit/Reset here.

            // 1) Build ALL candidate snap points (analytic)
            int entityCount;
            var ents = BuildCandidateEntitiesForSnap(out entityCount);
            var candSnaps = BuildCandidateSnapPointsFromEntities(ents);

            // 2) Build Clipper union loops (outer + islands)
            int subjCount, resultCount;
            long totalVerts;
            var loops = BuildClipperUnionLoopsWorld(out subjCount, out resultCount, out totalVerts);

            // 3) Build spatial grid for candidates (fast "within SNAP_TOL" lookup)
            var grid = BuildCandidateGrid(candSnaps, SNAP_TOL);

            // "Used" marking per-loop without allocating bool[] each loop
            int[] usedMark = new int[Math.Max(1, candSnaps.Count)];
            int curMark = 1;

            // 4) Collect ordered boundary snaps (clipper traversal order), then reduce+close
            var outerLoopsOrdered = new List<List<CandSnapPoint>>();
            var islandLoopsOrdered = new List<List<CandSnapPoint>>();

            // Keep pairing between a specific loop and its ordered snap list
            var loopData = new List<(ClipperUnionLoop Loop, List<CandSnapPoint> OrderedSnaps)>();

            foreach (var loop in loops)
            {
                if (loop == null || loop.WorldPts == null || loop.WorldPts.Count < 3)
                    continue;

                curMark++;
                if (curMark == int.MaxValue)
                {
                    Array.Clear(usedMark, 0, usedMark.Length);
                    curMark = 1;
                }

                var ordered = CollectBoundarySnapsByClipperVertexOrder(loop.WorldPts, candSnaps, grid, usedMark, curMark, SNAP_TOL);
                if (ordered.Count == 0)
                    continue;

                // Your cleanup + close (kills consecutive near-duplicates, restarts after any removal, then closes)
                ReduceAndCloseBoundarySnapListInPlace(ordered);
                if (ordered.Count < 3)
                    continue;

                loopData.Add((loop, ordered));

                if (loop.IsHole)
                    islandLoopsOrdered.Add(ordered);
                else
                    outerLoopsOrdered.Add(ordered);
            }

            // 5) Build Python-friendly LINE/ARC primitives from the SAME loops + SAME reduced snap lists
            var outerPyLoops = new List<List<PyPrim>>();
            var islandPyLoops = new List<List<PyPrim>>();

            for (int i = 0; i < loopData.Count; i++)
            {
                var lp = loopData[i].Loop;
                var snapsClosed = loopData[i].OrderedSnaps;

                // Build point runs between snaps (snap, [clipper..], snap)
                var runs = BuildPointRunsBetweenSnapsInClipperOrder(lp.WorldPts, snapsClosed, SNAP_TOL);

                // Convert runs -> LINE/ARC prims
                var prims = BuildPythonFriendlyPrimsFromRuns(runs);
                if (prims.Count == 0)
                    continue;

                if (lp.IsHole)
                    islandPyLoops.Add(prims);
                else
                    outerPyLoops.Add(prims);
            }

            // 6) SINGLE COMBINED LOG WINDOW (ONLY when LogWindowShow is enabled)
            // Order requested:
            //   (0) export text
            //   (1) python-friendly first (outer then islands, with easy parse headers)
            //   (2) all candidate snaps
            //   (3) ordered reduced sets (outer then islands)
            if (Settings.Default.LogWindowShow)
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder(512 * 1024);

                // ------------------------------------------------------------
                // (0) EXPORT TEXT (what FreeCAD export uses)
                // ------------------------------------------------------------
                sb.AppendLine("=== CLIPPER EXPORT TEXT (USED BY EXPORT) ===");
                sb.AppendLine($"includeIslands=true");
                sb.AppendLine();
                sb.AppendLine(exportText ?? "");
                sb.AppendLine();

                // ------------------------------------------------------------
                // (1) PYTHON-FRIENDLY FIRST
                // ------------------------------------------------------------
                sb.AppendLine("=== PYTHON-FRIENDLY (FREECAD) BOUNDARY PRIMS ===");
                sb.AppendLine($"MIN_ARC_POINTS={MIN_ARC_POINTS}  SNAP_TOL={SNAP_TOL.ToString("0.###", inv)}  keyDp={SNAP_KEY_DECIMALS}");
                sb.AppendLine();

                // OUTER loops
                for (int i = 0; i < outerPyLoops.Count; i++)
                {
                    sb.AppendLine($"--- LOOP {i}  OUTER ---");
                    AppendPythonFriendlyPrimsToSb(sb, outerPyLoops[i]);
                    sb.AppendLine();
                }

                // ISLAND loops
                for (int i = 0; i < islandPyLoops.Count; i++)
                {
                    sb.AppendLine($"--- LOOP {i}  ISLAND ---");
                    AppendPythonFriendlyPrimsToSb(sb, islandPyLoops[i]);
                    sb.AppendLine();
                }

                // ------------------------------------------------------------
                // (2) ALL CANDIDATE SNAPS
                // ------------------------------------------------------------
                sb.AppendLine("=== ALL CANDIDATE SNAP POINTS (x y z) ===");
                sb.AppendLine($"segs={_segs.Count}  entities={ents.Count}  snaps={candSnaps.Count}");
                sb.AppendLine();

                // keep stable ordering for sanity
                var candOrdered = candSnaps
                    .OrderBy(p => p.X)
                    .ThenBy(p => p.Y)
                    .ToList();

                AppendSnapListNumericOnly(sb, candOrdered);
                sb.AppendLine();

                // ------------------------------------------------------------
                // (3) ORDERED REDUCED SETS (OUTER then ISLANDS)
                // ------------------------------------------------------------
                sb.AppendLine("=== CLIPPER-ORDERED REDUCED SNAP POINTS (x y z) ===");
                sb.AppendLine($"OUTER={outerLoopsOrdered.Count}  ISLANDS={islandLoopsOrdered.Count}  SNAP_TOL={SNAP_TOL.ToString("0.###", inv)}");
                sb.AppendLine();

                for (int i = 0; i < outerLoopsOrdered.Count; i++)
                {
                    sb.AppendLine($"--- LOOP {i}  OUTER ---");
                    AppendSnapListNumericOnly(sb, outerLoopsOrdered[i]);
                    sb.AppendLine();
                }

                for (int i = 0; i < islandLoopsOrdered.Count; i++)
                {
                    sb.AppendLine($"--- LOOP {i}  ISLAND ---");
                    AppendSnapListNumericOnly(sb, islandLoopsOrdered[i]);
                    sb.AppendLine();
                }

                var win = new LogWindow("TrueShape Combined Logs", sb.ToString())
                {
                    Owner = this
                };
                win.Show();
            }

            // 7) Display: render EXACTLY from the Python-friendly primitives (no Fit/Reset)
            RenderPythonFriendlyPrimitiveLoops(outerPyLoops, islandPyLoops);
        }


    }
}
