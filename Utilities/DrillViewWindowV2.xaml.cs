using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;




namespace CNC_Improvements_gcode_solids.Utilities
{
    public partial class DrillViewWindowV2 : Window
    {
        // ============================================================
        // Public model (supports grouping)
        // ============================================================
        public sealed class HoleCenter
        {

            // Unique across ALL groups (used for selection)
            public int Uid { get; set; }
            public int Index { get; set; }
            public int LineIndex { get; set; }

            public double X { get; set; }
            public double Y { get; set; }

            // Group identity (THIS drives colors and the "one section per group")
            public string GroupName { get; set; } = "(unnamed)";
            public int GroupIndex { get; set; } = 0; // optional, but handy

            // Per-group drill params (so groups can differ if needed)
            public double HoleDia { get; set; }
            public double ZHoleTop { get; set; }
            public double PointAngle { get; set; }
            public double ChamferLen { get; set; }
            public double ZPlusExt { get; set; }
            public double DrillZApex { get; set; }
        }

        public sealed class HoleGroup
        {
            public string GroupName { get; set; } = "(unnamed)";

            // Per-group drill params
            public double HoleDia { get; set; }
            public double ZHoleTop { get; set; }
            public double PointAngle { get; set; }
            public double ChamferLen { get; set; }
            public double ZPlusExt { get; set; }
            public double DrillZApex { get; set; }

            // ------------------------------------------------------------
            // viewer-only transform metadata (passed in from DrillPage)
            // Convention: RotZ is CW-positive (same rule as Mill viewer)
            // RotY only used if 180 => mirror X
            // Tx/Ty/Tz are carried for logging/future (NOT applied in viewer)
            // ------------------------------------------------------------
            public string RegionName { get; set; } = "";
            public string MatrixName { get; set; } = "";

            public double RotZDeg { get; set; } = 0.0;
            public double RotYDeg { get; set; } = 0.0;

            public double Tx { get; set; } = 0.0;
            public double Ty { get; set; } = 0.0;
            public double Tz { get; set; } = 0.0;

            public List<(double X, double Y, int LineIndex)> Holes { get; set; } = new();
        }


        // ============================================================
        // Per-canvas zoom/pan container
        // ============================================================
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

        // ============================================================
        // Viewer state
        // ============================================================
        private readonly List<HoleCenter> _holes = new();

        private readonly Viewport _topVp = new Viewport();
        private readonly Viewport _secVp = new Viewport(); // applied to SectionZoomRoot (Ctrl+Wheel)

        private readonly List<(HoleCenter hole, Point screenCenter, double screenR)> _topHit =
            new List<(HoleCenter hole, Point screenCenter, double screenR)>();

        private int _selectedHoleUid = -1; // unique selection id
        private bool _didInitialFit = false;

        private bool _showChamfer = true;
        private bool _showLabels = true;

        // Greys for axes/details
        private static readonly Brush DetailGrey = new SolidColorBrush(Color.FromRgb(190, 190, 190));
        private static readonly Brush SecondaryGrey = new SolidColorBrush(Color.FromRgb(150, 150, 150));

        // Text driven by Settings
        private Brush _graphicText = Brushes.Yellow;
        private double _profileWidth = 1.5;
        private double _offsetWidth = 1.2;

        // ============================================================
        // GROUP COLOUR SYSTEM (your requirement)
        // ============================================================
        private readonly Dictionary<string, Brush> _groupBrushMap = new(StringComparer.Ordinal);

        private readonly List<Color> _groupPalette = new()
        {
            Color.FromRgb( 80, 200, 120),
            Color.FromRgb( 70, 130, 180),
            Color.FromRgb(255, 165,   0),
            Color.FromRgb(200,  80,  80),
            Color.FromRgb(160,  90, 220),
            Color.FromRgb( 60, 200, 200),
            Color.FromRgb(240, 220,  90),
            Color.FromRgb(255, 105, 180),
            Color.FromRgb(180, 180, 180),
            Color.FromRgb(140, 200,  90),
        };

        private Brush GetGroupBrush(string? groupName)
        {
            string key = string.IsNullOrWhiteSpace(groupName) ? "(unnamed)" : groupName.Trim();

            if (_groupBrushMap.TryGetValue(key, out Brush b))
                return b;

            int idx = _groupBrushMap.Count % _groupPalette.Count;
            var brush = new SolidColorBrush(_groupPalette[idx]);
            brush.Freeze();

            _groupBrushMap[key] = brush;
            return brush;
        }

        // “Hue shift similar”: implemented as a brightness lift so it reads as the same family colour.
        private Brush MakeChamferBrush(Brush baseBrush)
        {
            if (baseBrush is not SolidColorBrush scb)
                return baseBrush;

            Color c = scb.Color;
            Color brighter = Brighten(c, 0.18);
            var b = new SolidColorBrush(brighter);
            b.Freeze();
            return b;
        }

        private static Color Brighten(Color c, double amount01)
        {
            if (amount01 < 0) amount01 = 0;
            if (amount01 > 1) amount01 = 1;

            byte br(byte v)
            {
                double dv = v;
                dv = dv + (255.0 - dv) * amount01;
                if (dv < 0) dv = 0;
                if (dv > 255) dv = 255;
                return (byte)dv;
            }

            return Color.FromArgb(c.A, br(c.R), br(c.G), br(c.B));
        }

        // ============================================================
        // Section list VM (ONE ITEM PER GROUP)
        // ============================================================
        private sealed class GroupSectionVm
        {
            public string GroupName { get; set; } = "(unnamed)";
            public int HoleCount { get; set; }

            public Brush GroupBrush { get; set; } = Brushes.White;
            public Brush ChamferBrush { get; set; } = Brushes.White;

            public double HoleDia { get; set; }
            public double ZHoleTop { get; set; }
            public double PointAngle { get; set; }
            public double ChamferLen { get; set; }
            public double ZPlusExt { get; set; }
            public double DrillZApex { get; set; }

            public double CanvasHeight { get; set; } = 220.0;

            public string HeaderText =>
                $"{GroupName}  |  Holes={HoleCount}  |  Dia={HoleDia:0.###}  ZTop={ZHoleTop:0.###}  PtAng={PointAngle:0.###}";
        }

        private readonly List<Canvas> _sectionCanvases = new();
        private readonly Dictionary<Canvas, GroupSectionVm> _sectionCanvasToVm = new();

        // ============================================================
        // Constructors
        // ============================================================
        public DrillViewWindowV2(List<HoleGroup> groups)
        {
            InitializeComponent();

            static double Norm360(double deg)
            {
                if (!double.IsFinite(deg))
                    return 0.0;

                deg %= 360.0;
                if (deg < 0.0) deg += 360.0;
                return deg;
            }

            static bool IsRotY180(double rotYDeg)
            {
                double a = Norm360(rotYDeg);
                return Math.Abs(a - 180.0) < 1e-3;
            }

            static void TransformPoint2D(double x, double y, double rotZDegCw, bool mirrorX, out double xo, out double yo)
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

            int uid = 1;

            if (groups != null)
            {
                int gi = 0;

                foreach (var g in groups)
                {
                    string gname = string.IsNullOrWhiteSpace(g.GroupName) ? $"Group {gi + 1}" : g.GroupName.Trim();

                    // Per-group view transform (same rule as MillViewWindow display)
                    double zRot = g?.RotZDeg ?? 0.0;
                    double yRot = g?.RotYDeg ?? 0.0;
                    bool mirrorX = IsRotY180(yRot);

                    int idx = 1;

                    foreach (var p in g.Holes)
                    {
                        TransformPoint2D(p.X, p.Y, zRot, mirrorX, out double xt, out double yt);

                        _holes.Add(new HoleCenter
                        {
                            Uid = uid++,

                            Index = idx++,
                            LineIndex = p.LineIndex,

                            // TRANSFORMED for display
                            X = xt,
                            Y = yt,

                            GroupName = gname,
                            GroupIndex = gi,

                            HoleDia = g.HoleDia,
                            ZHoleTop = g.ZHoleTop,
                            PointAngle = g.PointAngle,
                            ChamferLen = g.ChamferLen,
                            ZPlusExt = g.ZPlusExt,
                            DrillZApex = g.DrillZApex
                        });
                    }

                    gi++;
                }
            }

            InitViewer();
        }


        public DrillViewWindowV2(List<HoleCenter> holes)
        {
            InitializeComponent();

            if (holes != null)
                _holes = holes;

            int uid = 1;
            foreach (var h in _holes)
            {
                if (h.Uid <= 0)
                    h.Uid = uid;
                uid++;
            }




            InitViewer();




        }


        public class SectionItemVm : INotifyPropertyChanged
        {
            public string HeaderText { get; set; }
            public double CanvasHeight { get; set; }

            private Brush _headerForeground = Brushes.LightGray;
            public Brush HeaderForeground
            {
                get => _headerForeground;
                set { _headerForeground = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void InitViewer()
        {
            ApplyViewerTextAndWidthsFromSettings(); // pushes GraphicTextBrush into Window.Resources


            TopCanvas.RenderTransform = _topVp.Group;
            TopCanvas.RenderTransformOrigin = new Point(0, 0);

            SectionZoomRoot.RenderTransform = _secVp.Group;
            SectionZoomRoot.RenderTransformOrigin = new Point(0, 0);

            Loaded += DrillViewWindowV2_Loaded;
            SizeChanged += DrillViewWindowV2_SizeChanged;

            SyncToggleButtonText();
            RebuildSectionsItemsSource();   // ONE item per group
            UpdateSummaryText();
            UpdateTopOverlay();
            UpdateSectionOverlay();
        }

        private void ApplyViewerTextAndWidthsFromSettings()
        {
            // Graphic/info text colour
            Brush b;
            try { b = UiUtilities.HexBrush(Settings.Default.GraphicTextColor); }
            catch { b = Brushes.Yellow; }

            _graphicText = b;

            // One brush drives ALL text (TextBlocks/TextBoxes/Buttons/etc.)
            Resources["GraphicTextBrush"] = b;

            // Widths
            try { _profileWidth = Settings.Default.ProfileWidth; }
            catch { _profileWidth = 1.5; }

            try { _offsetWidth = Settings.Default.OffsetWidth; }
            catch { _offsetWidth = 1.2; }

            if (!double.IsFinite(_profileWidth) || _profileWidth <= 0) _profileWidth = 1.5;
            if (!double.IsFinite(_offsetWidth) || _offsetWidth <= 0) _offsetWidth = 1.2;
        }


        // ============================================================
        // Window events
        // ============================================================
        private void DrillViewWindowV2_Loaded(object sender, RoutedEventArgs e)
        {
            FitAll();
        }

        private void DrillViewWindowV2_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_didInitialFit)
                FitAll();
        }

        // ============================================================
        // Toolbar
        // ============================================================
        private void BtnFitAll_Click(object sender, RoutedEventArgs e) => FitAll();

        private void BtnResetAll_Click(object sender, RoutedEventArgs e)
        {
            _topVp.Reset();
            _secVp.Reset();
        }

        private void BtnFitTop_Click(object sender, RoutedEventArgs e)
        {
            RenderTopViewFit();
            _topVp.Reset();
            UpdateTopOverlay();
        }

        private void BtnResetTop_Click(object sender, RoutedEventArgs e) => _topVp.Reset();

        private void BtnFitSections_Click(object sender, RoutedEventArgs e)
        {
            RenderAllSectionCanvasesFit();
            _secVp.Reset();
            UpdateSectionOverlay();
        }

        private void BtnResetSections_Click(object sender, RoutedEventArgs e) => _secVp.Reset();

        private void BtnToggleChamfer_Click(object sender, RoutedEventArgs e)
        {
            _showChamfer = !_showChamfer;
            SyncToggleButtonText();

            RenderTopViewFit();
            RenderAllSectionCanvasesFit();

            UpdateTopOverlay();
            UpdateSectionOverlay();
        }

        private void BtnToggleLabels_Click(object sender, RoutedEventArgs e)
        {
            _showLabels = !_showLabels;
            SyncToggleButtonText();

            RenderTopViewFit();
            UpdateTopOverlay();
        }

        private void SyncToggleButtonText()
        {
            if (BtnToggleChamfer != null)
                BtnToggleChamfer.Content = _showChamfer ? "Chamfer: ON" : "Chamfer: OFF";

            if (BtnToggleLabels != null)
                BtnToggleLabels.Content = _showLabels ? "Labels: ON" : "Labels: OFF";
        }

        private void FitAll()
        {
            RenderTopViewFit();
            RebuildSectionsItemsSource();
            RenderAllSectionCanvasesFit();

            _topVp.Reset();
            _secVp.Reset();

            _didInitialFit = true;

            UpdateSummaryText();
            UpdateTopOverlay();
            UpdateSectionOverlay();
        }

        // ============================================================
        // Summary / overlays
        // ============================================================
        private void UpdateSummaryText()
        {
            int groupCount = _holes.Select(h => NormGroupName(h.GroupName)).Distinct(StringComparer.Ordinal).Count();

            string sel = "";
            if (_selectedHoleUid >= 0)
            {
                var h = _holes.FirstOrDefault(x => x.Uid == _selectedHoleUid);

                if (h != null)
                    sel = $"  Selected Hole={h.Index}  Group={NormGroupName(h.GroupName)}  (Gcode line {h.LineIndex + 1})";
            }

            TxtSummary.Text = $"Groups={groupCount}  Holes={_holes.Count}{sel}";
        }

        private void UpdateTopOverlay()
        {
            if (TopInfoText == null || TopInfoBorder == null)
                return;

            TopInfoText.Foreground = _graphicText;

            if (_selectedHoleUid < 0)

            {
                TopInfoText.Text = "Click a hole to get info (fit before selection). Colours are per GROUP.";
                TopInfoBorder.Visibility = Visibility.Visible;
                return;
            }

            var h = _holes.FirstOrDefault(x => x.Uid == _selectedHoleUid);

            if (h == null)
            {
                TopInfoText.Text = "Click a hole to select. Colours are per GROUP.";
                TopInfoBorder.Visibility = Visibility.Visible;
                return;
            }

            TopInfoText.Text =
                $"HOLE {h.Index}\n" +
                $"GROUP: {NormGroupName(h.GroupName)}\n" +
                $"X={h.X:0.###}   Y={h.Y:0.###}\n" +
                $"Gcode line {h.LineIndex + 1}";
            TopInfoBorder.Visibility = Visibility.Visible;
        }

        private void UpdateSectionOverlay()
        {
            if (SectionInfoText == null || SectionInfoBorder == null)
                return;

            SectionInfoText.Foreground = _graphicText;
            SectionInfoText.Text = "Wheel = Scroll. Ctrl+Wheel = Zoom sections.";
            SectionInfoBorder.Visibility = Visibility.Visible;
        }

        // ============================================================
        // LEFT: TOP VIEW (XY)
        // ============================================================
        private void RenderTopViewFit()
        {
            TopCanvas.Children.Clear();
            _topHit.Clear();

            if (_holes.Count == 0)
                return;

            double canvasW = TopCanvas.ActualWidth;
            double canvasH = TopCanvas.ActualHeight;
            if (canvasW < 10) canvasW = 600;
            if (canvasH < 10) canvasH = 500;

            double margin = 25.0;

            // Bounds using each hole’s “visible radius” (bore or chamfer)
            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;

            foreach (var h in _holes)
            {
                double r = Math.Max(0.0, h.HoleDia * 0.5);
                double rVis = r;

                if (_showChamfer && h.ChamferLen > 0.0)
                    rVis = Math.Max(rVis, r + h.ChamferLen);

                minX = Math.Min(minX, h.X - rVis);
                maxX = Math.Max(maxX, h.X + rVis);
                minY = Math.Min(minY, h.Y - rVis);
                maxY = Math.Max(maxY, h.Y + rVis);
            }

            if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
                return;

            double rangeX = maxX - minX; if (rangeX <= 0) rangeX = 1;
            double rangeY = maxY - minY; if (rangeY <= 0) rangeY = 1;

            double scaleX = (canvasW - 2 * margin) / rangeX;
            double scaleY = (canvasH - 2 * margin) / rangeY;
            double scale = Math.Min(scaleX, scaleY);

            DrawTopOriginAxes(minX, maxX, minY, maxY, scale, margin);

            double boreThk = Math.Max(0.5, _profileWidth);
            double boreThkSel = Math.Max(boreThk + 1.0, boreThk * 1.6);

            double chamferThk = Math.Max(0.5, _offsetWidth);
            double chamferThkSel = Math.Max(chamferThk + 1.0, chamferThk * 1.6);

            foreach (var h in _holes)
            {
                Brush groupBrush = GetGroupBrush(h.GroupName);
                Brush chamferBrush = MakeChamferBrush(groupBrush);

                double r = Math.Max(0.0, h.HoleDia * 0.5);
                double rChamferTop = r + Math.Max(0.0, h.ChamferLen);

                double sx = (h.X - minX) * scale + margin;
                double sy = (maxY - h.Y) * scale + margin;

                double sr = r * scale;
                double srChamfer = rChamferTop * scale;

                bool isSelected = (h.Uid == _selectedHoleUid);


                // Bore (GROUP colour)
                var bore = new Ellipse
                {
                    Width = sr * 2,
                    Height = sr * 2,
                    Stroke = isSelected ? Brushes.Orange : groupBrush,
                    StrokeThickness = isSelected ? boreThkSel : boreThk
                };
                Canvas.SetLeft(bore, sx - sr);
                Canvas.SetTop(bore, sy - sr);
                TopCanvas.Children.Add(bore);

                // Chamfer ring (same group, visually different)
                if (_showChamfer && h.ChamferLen > 0.0)
                {
                    var ch = new Ellipse
                    {
                        Width = srChamfer * 2,
                        Height = srChamfer * 2,
                        Stroke = isSelected ? Brushes.Orange : chamferBrush,
                        StrokeThickness = isSelected ? chamferThkSel : chamferThk
                    };
                    Canvas.SetLeft(ch, sx - srChamfer);
                    Canvas.SetTop(ch, sy - srChamfer);
                    TopCanvas.Children.Add(ch);
                }

                // Cross hair
                double cross = Math.Max(6.0, sr * 0.25);
                TopCanvas.Children.Add(new Line { X1 = sx - cross, Y1 = sy, X2 = sx + cross, Y2 = sy, Stroke = DetailGrey, StrokeThickness = 1.2 });
                TopCanvas.Children.Add(new Line { X1 = sx, Y1 = sy - cross, X2 = sx, Y2 = sy + cross, Stroke = DetailGrey, StrokeThickness = 1.2 });

                if (_showLabels)
                {
                    var label = new TextBlock
                    {
                        Text = $"{h.Index}",
                        Foreground = _graphicText,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12
                    };
                    Canvas.SetLeft(label, sx + 6);
                    Canvas.SetTop(label, sy + 6);
                    TopCanvas.Children.Add(label);
                }

                _topHit.Add((h, new Point(sx, sy), Math.Max(1.0, sr)));
            }
        }

        private void DrawTopOriginAxes(double minX, double maxX, double minY, double maxY, double scale, double margin)
        {
            double pad = 50.0;
            if (0 < minX - pad || 0 > maxX + pad || 0 < minY - pad || 0 > maxY + pad)
                return;

            double sx0 = (0 - minX) * scale + margin;
            double sy0 = (maxY - 0) * scale + margin;

            double len = 30.0;
            TopCanvas.Children.Add(new Line { X1 = sx0 - len, Y1 = sy0, X2 = sx0 + len, Y2 = sy0, Stroke = DetailGrey, StrokeThickness = 1.2 });
            TopCanvas.Children.Add(new Line { X1 = sx0, Y1 = sy0 - len, X2 = sx0, Y2 = sy0 + len, Stroke = DetailGrey, StrokeThickness = 1.2 });

            var t = new TextBlock
            {
                Text = "0,0",
                Foreground = _graphicText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11
            };
            Canvas.SetLeft(t, sx0 + 6);
            Canvas.SetTop(t, sy0 + 6);
            TopCanvas.Children.Add(t);
        }

        private void TopCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Point p = e.GetPosition(TopCanvas);
            _topVp.ZoomAtPoint(p, e.Delta);
            e.Handled = true;
        }

        private void TopCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            TopCanvas.Focus();

            if (_topHit.Count == 0)
                return;

            Point pCanvas = e.GetPosition(TopCanvas);
            Point pBase = _topVp.InverseTransformPoint(pCanvas);

            double bestD2 = double.MaxValue;
            HoleCenter? best = null;

            foreach (var item in _topHit)
            {
                double dx = pBase.X - item.screenCenter.X;
                double dy = pBase.Y - item.screenCenter.Y;
                double d2 = dx * dx + dy * dy;

                if (d2 <= item.screenR * item.screenR && d2 < bestD2)
                {
                    bestD2 = d2;
                    best = item.hole;
                }
            }

            if (best != null)
            {
                _selectedHoleUid = best.Uid;


                RenderTopViewFit();

                UpdateSummaryText();
                UpdateTopOverlay();
            }
        }

        // ============================================================
        // RIGHT: SECTIONS (one per group, stacked)
        // ============================================================
        private void RebuildSectionsItemsSource()
        {
            _sectionCanvases.Clear();
            _sectionCanvasToVm.Clear();

            if (_holes.Count == 0)
            {
                SectionItems.ItemsSource = null;
                return;
            }

            // Preserve first-seen order (IMPORTANT for your “6 holes then 4 holes” colouring)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var vms = new List<GroupSectionVm>();

            foreach (var h in _holes)
            {
                string gname = NormGroupName(h.GroupName);
                if (!seen.Add(gname))
                    continue;

                int holeCount = _holes.Count(x => NormGroupName(x.GroupName) == gname);

                Brush gb = GetGroupBrush(gname);

                vms.Add(new GroupSectionVm
                {
                    GroupName = gname,
                    HoleCount = holeCount,
                    GroupBrush = gb,
                    ChamferBrush = MakeChamferBrush(gb),

                    HoleDia = h.HoleDia,
                    ZHoleTop = h.ZHoleTop,
                    PointAngle = h.PointAngle,
                    ChamferLen = h.ChamferLen,
                    ZPlusExt = h.ZPlusExt,
                    DrillZApex = h.DrillZApex,

                    CanvasHeight = 220.0
                });
            }

            SectionItems.ItemsSource = vms;
        }

        private void SectionItemCanvas_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Canvas c)
                return;

            if (c.Tag is not GroupSectionVm vm)
                return;

            if (!_sectionCanvasToVm.ContainsKey(c))
            {
                _sectionCanvasToVm[c] = vm;
                _sectionCanvases.Add(c);
            }

            RenderSectionCanvasFit(c, vm);
        }

        private void SectionItemCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Canvas c)
                return;

            if (!_sectionCanvasToVm.TryGetValue(c, out var vm))
                return;

            RenderSectionCanvasFit(c, vm);
        }

        private void RenderAllSectionCanvasesFit()
        {
            foreach (var c in _sectionCanvases)
            {
                if (_sectionCanvasToVm.TryGetValue(c, out var vm))
                    RenderSectionCanvasFit(c, vm);
            }
        }

        private void RenderSectionCanvasFit(Canvas canvas, GroupSectionVm vm)
        {
            canvas.Children.Clear();

            double canvasW = canvas.ActualWidth;
            double canvasH = canvas.ActualHeight;
            if (canvasW < 10) canvasW = 600;
            if (canvasH < 10) canvasH = vm.CanvasHeight > 0 ? vm.CanvasHeight : 220;

            double margin = 37.5;

            var pts = BuildHoleSectionPoints_ZR(vm);
            if (pts.Count < 2)
                return;

            double minZ = pts.Min(p => p.X);
            double maxZ = pts.Max(p => p.X);
            double maxR = pts.Max(p => p.Y);

            double rangeZ = maxZ - minZ; if (rangeZ <= 0) rangeZ = 1;
            double rangeR = maxR - 0.0; if (rangeR <= 0) rangeR = 1;

            double scaleX = (canvasW - 2 * margin) / rangeZ;
            double scaleY = (canvasH - 2 * margin) / rangeR;
            double scale = Math.Min(scaleX, scaleY);

            double yCenter = (maxR - 0.0) * scale + margin;

            // center line
            canvas.Children.Add(new Line
            {
                X1 = margin,
                X2 = (maxZ - minZ) * scale + margin,
                Y1 = yCenter,
                Y2 = yCenter,
                Stroke = DetailGrey,
                StrokeThickness = 1.2
            });

            // We draw MAIN outline in group colour
            // and the TOP chamfer part (if enabled) in chamferBrush.

            double thkMain = Math.Max(0.5, _profileWidth);
            double thkCham = Math.Max(0.5, _offsetWidth);

            // Convert to screen coords
            List<Point> screenPts = new();
            foreach (var wp in pts)
            {
                double sx = (wp.X - minZ) * scale + margin;
                double sy = (maxR - wp.Y) * scale + margin;
                screenPts.Add(new Point(sx, sy));
            }


            // ---- Group name label on the cross section canvas (top-left) ----
            {
                string title = $"SET: {vm.GroupName}";

                var t = new TextBlock
                {
                    Text = title,
                    Foreground = _graphicText,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold
                };

                // small dark background so it stays readable over lines
                var bg = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 0, 3, 0),
                    Child = t
                };

                Canvas.SetLeft(bg, margin);
                Canvas.SetTop(bg, 3.0);
                Panel.SetZIndex(bg, 10000);

                canvas.Children.Add(bg);
            }



            // Segment split index: chamfer starts at (optional) ChamferBot -> ZTop,
            // which in our pts list is:
            // [Apex, ConeBase, (ChamferBot?), ZTop, (ExtTop?)]
            // If chamfer disabled, all is main.

            int idxChamferStart = -1;
            int idxChamferEnd = -1;

            bool hasChamfer = _showChamfer && vm.ChamferLen > 0.0;

            if (hasChamfer)
            {
                // pts layout:
                // 0 Apex
                // 1 ConeBase
                // 2 ChamferBot   (present)
                // 3 ZTop
                // 4 ExtTop (optional)
                idxChamferStart = 2;
                idxChamferEnd = 3; // inclusive end for chamfer segment polyline
            }

            // MAIN polyline (everything)
            var main = new Polyline
            {
                Stroke = vm.GroupBrush,
                StrokeThickness = thkMain
            };

            for (int i = 0; i < screenPts.Count; i++)
                main.Points.Add(screenPts[i]);

            canvas.Children.Add(main);

            // CHAMFER highlight (just a short polyline over the chamfer leg)
            if (hasChamfer && idxChamferStart >= 0 && idxChamferEnd >= idxChamferStart && idxChamferEnd < screenPts.Count)
            {
                var ch = new Polyline
                {
                    Stroke = vm.ChamferBrush,
                    StrokeThickness = thkCham
                };

                for (int i = idxChamferStart; i <= idxChamferEnd; i++)
                    ch.Points.Add(screenPts[i]);

                canvas.Children.Add(ch);
            }

            // Z markers (basic)
            DrawSectionZMarker(canvas, minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, vm.DrillZApex, "Apex", 0);
            DrawSectionZMarker(canvas, minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, GetConeBaseZ(vm), "ConeBase", 1);

            if (hasChamfer)
                DrawSectionZMarker(canvas, minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, vm.ZHoleTop - vm.ChamferLen, "ChamferBot", 2);

            DrawSectionZMarker(canvas, minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, vm.ZHoleTop, "ZTop", 3);

            if (vm.ZPlusExt > 0.0)
                DrawSectionZMarker(canvas, minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, vm.ZHoleTop + vm.ZPlusExt, "ExtTop", 4);
        }

        private void DrawSectionZMarker(
            Canvas canvas,
            double minZ,
            double maxZ,
            double maxR,
            double scale,
            double margin,
            double canvasW,
            double canvasH,
            double yCenter,
            double z,
            string label,
            int slot)
        {
            if (z < minZ - 1e-6 || z > maxZ + 1e-6)
                return;

            double x = (z - minZ) * scale + margin;
            double yTop = margin;
            double yBot = (maxR - 0.0) * scale + margin;

            canvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = yTop,
                Y2 = yBot,
                Stroke = SecondaryGrey,
                StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });

            // Stagger labels
            int level = slot / 2;
            bool down = (slot % 2 == 0);

            double yLabel = down
                ? yCenter + 6.0 + (level * 14.0)
                : yCenter - 6.0 - 14.0 - (level * 14.0);

            double minY = margin;
            double maxY = Math.Max(margin, canvasH - margin - 14.0);
            if (yLabel < minY) yLabel = minY;
            if (yLabel > maxY) yLabel = maxY;

            var t = new TextBlock
            {
                Text = label,
                Foreground = _graphicText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextAlignment = TextAlignment.Left
            };

            Canvas.SetLeft(t, x + 4.0);
            Canvas.SetTop(t, yLabel);
            canvas.Children.Add(t);
        }

        private List<Point> BuildHoleSectionPoints_ZR(GroupSectionVm vm)
        {
            var pts = new List<Point>();

            if (vm.HoleDia <= 0) return pts;
            if (vm.PointAngle <= 0 || vm.PointAngle > 180) return pts;

            double r = vm.HoleDia / 2.0;

            double halfAngleRad = (vm.PointAngle * 0.5) * Math.PI / 180.0;
            double tan = Math.Tan(halfAngleRad);
            if (Math.Abs(tan) < 1e-12) return pts;

            double tipHeight = r / tan;
            double coneBaseZ = vm.DrillZApex + tipHeight;

            double zChamferBot = vm.ZHoleTop - vm.ChamferLen;
            double zChamferTop = vm.ZHoleTop;
            double zExtTop = vm.ZHoleTop + vm.ZPlusExt;

            double rAtTop = r;
            if (_showChamfer && vm.ChamferLen > 0.0)
                rAtTop = r + vm.ChamferLen;

            // Apex -> ConeBase
            pts.Add(new Point(vm.DrillZApex, 0.0));
            pts.Add(new Point(coneBaseZ, r));

            // ConeBase -> (ChamferBot) -> ZTop
            if (_showChamfer && vm.ChamferLen > 0.0)
                pts.Add(new Point(zChamferBot, r));

            pts.Add(new Point(zChamferTop, rAtTop));

            // Z+ extension
            if (vm.ZPlusExt > 0.0)
                pts.Add(new Point(zExtTop, rAtTop));

            return pts;
        }

        private double GetConeBaseZ(GroupSectionVm vm)
        {
            double r = vm.HoleDia / 2.0;
            double halfAngleRad = (vm.PointAngle * 0.5) * Math.PI / 180.0;
            double tan = Math.Tan(halfAngleRad);
            if (Math.Abs(tan) < 1e-12)
                return vm.DrillZApex;

            double tipHeight = r / tan;
            return vm.DrillZApex + tipHeight;
        }

        private static string NormGroupName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "(unnamed)";
            return name.Trim();
        }

        // ============================================================
        // Sections scroll / zoom handling
        // ============================================================
        private void SectionsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Ctrl+Wheel => zoom the whole section stack.
            // Wheel alone => let ScrollViewer scroll (do not handle).
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Point p = e.GetPosition(SectionZoomRoot);
                _secVp.ZoomAtPoint(p, e.Delta);
                e.Handled = true;
            }
        }
    }
}
