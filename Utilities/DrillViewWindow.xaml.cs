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

namespace CNC_Improvements_gcode_solids.Utilities
{
    public partial class DrillViewWindow : Window
    {
        // -----------------------------
        // Public model used by DrillPage
        // -----------------------------
        public sealed class HoleCenter
        {
            public int Index { get; set; }
            public int LineIndex { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
        }

        // -----------------------------
        // Per-canvas zoom/pan container
        // -----------------------------
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

                // Keep point under cursor stationary:
                // T' = (S - S') * P + T
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

        // -----------------------------
        // Stored drill data
        // -----------------------------
        private readonly double _holeDia;
        private readonly double _zHoleTop;
        private readonly double _pointAngle;
        private readonly double _chamferLen;
        private readonly double _zPlusExt;
        private readonly double _drillZApex;
        private readonly List<HoleCenter> _holes;

        // Render state (base screen coords before zoom)
        private readonly Viewport _topVp = new Viewport();
        private readonly Viewport _secVp = new Viewport();

        private readonly List<(HoleCenter hole, Point screenCenter, double screenR)> _topHit =
            new List<(HoleCenter hole, Point screenCenter, double screenR)>();

        private int _selectedHoleIndex = -1;
        private bool _didInitialFit = false;

        // Toolbar toggles
        private bool _showChamfer = true;
        private bool _showLabels = true;

        // Brighter "supplementary detail" greys
        private static readonly Brush DetailGrey = new SolidColorBrush(Color.FromRgb(190, 190, 190));
        private static readonly Brush SecondaryGrey = new SolidColorBrush(Color.FromRgb(150, 150, 150));

        // -----------------------------
        // Brushes/widths driven by Settings
        // -----------------------------
        private Brush _profileStroke = Brushes.Lime;     // Settings.Default.ProfileColor
        private Brush _offsetStroke = Brushes.Orange;    // Settings.Default.OffsetColor
        private Brush _graphicText = Brushes.Yellow;     // Settings.Default.GraphicTextColor

        private double _profileWidth = 1.5;              // Settings.Default.ProfileWidth
        private double _offsetWidth = 1.2;               // Settings.Default.OffsetWidth

        // Marker label font
        private static readonly FontFamily MarkerFontFamily = new FontFamily("Consolas");
        private const double MarkerFontSize = 11.0;
        private const double MarkerLabelPad = 6.0;
        private const double MarkerRowStep = 14.0;

        public DrillViewWindow(
            double holeDia,
            double zHoleTop,
            double pointAngle,
            double chamferLen,
            double zPlusExt,
            double drillZApex,
            List<HoleCenter> holes)
        {
            InitializeComponent();

            _holeDia = holeDia;
            _zHoleTop = zHoleTop;
            _pointAngle = pointAngle;
            _chamferLen = chamferLen;
            _zPlusExt = zPlusExt;
            _drillZApex = drillZApex;
            _holes = holes ?? new List<HoleCenter>();

            ApplyViewerColorsFromSettings();

            TopCanvas.RenderTransform = _topVp.Group;
            TopCanvas.RenderTransformOrigin = new Point(0, 0);

            SectionCanvas.RenderTransform = _secVp.Group;
            SectionCanvas.RenderTransformOrigin = new Point(0, 0);

            Loaded += DrillViewWindow_Loaded;
            SizeChanged += DrillViewWindow_SizeChanged;
            TxtSummary.Foreground = _graphicText;
            LHLable.Foreground = _graphicText;
            TopInfoText.Foreground = _graphicText;
            TopInfo.Foreground = _graphicText;
            SectionInfoText.Foreground = _graphicText;
            SyncToggleButtonText();
            UpdateSummaryText();
            UpdateTopOverlay();
            UpdateSectionOverlay();
        }

        private void ApplyViewerColorsFromSettings()
        {
            // Profile stroke
            try { _profileStroke = UiUtilities.HexBrush(Settings.Default.ProfileColor); }
            catch { _profileStroke = Brushes.Lime; }

            // Offset stroke (chamfer circle)
            try { _offsetStroke = UiUtilities.HexBrush(Settings.Default.OffsetColor); }
            catch { _offsetStroke = Brushes.Orange; }

            // Graphic/info text
            try { _graphicText = UiUtilities.HexBrush(Settings.Default.GraphicTextColor); }
            catch { _graphicText = Brushes.Yellow; }

            // Widths
            try { _profileWidth = Settings.Default.ProfileWidth; }
            catch { _profileWidth = 1.5; }

            try { _offsetWidth = Settings.Default.OffsetWidth; }
            catch { _offsetWidth = 1.2; }

            if (!double.IsFinite(_profileWidth) || _profileWidth <= 0) _profileWidth = 1.5;
            if (!double.IsFinite(_offsetWidth) || _offsetWidth <= 0) _offsetWidth = 1.2;
        }

        private void DrillViewWindow_Loaded(object sender, RoutedEventArgs e)
        {
            FitAll();
        }

        private void DrillViewWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // first load: layout settles; one more fit is useful
            if (!_didInitialFit)
                FitAll();
        }

        // ============================================================
        // Toolbar handlers (match XAML)
        // ============================================================
        private void BtnFitAll_Click(object sender, RoutedEventArgs e)
        {
            FitAll();
        }

        private void BtnResetAll_Click(object sender, RoutedEventArgs e)
        {
            _topVp.Reset();
            _secVp.Reset();
        }

        private void BtnToggleChamfer_Click(object sender, RoutedEventArgs e)
        {
            _showChamfer = !_showChamfer;
            SyncToggleButtonText();

            RenderTopViewFit();
            RenderSectionFit();

            UpdateTopOverlay();
            UpdateSectionOverlay();
        }

        private void BtnToggleLabels_Click(object sender, RoutedEventArgs e)
        {
            _showLabels = !_showLabels;
            SyncToggleButtonText();

            RenderTopViewFit();
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
            RenderSectionFit();

            _topVp.Reset();
            _secVp.Reset();

            _didInitialFit = true;

            UpdateTopOverlay();
            UpdateSectionOverlay();
        }

        private void UpdateSummaryText()
        {
            var inv = CultureInfo.InvariantCulture;

            string sel = "";
            if (_selectedHoleIndex >= 0)
            {
                var h = _holes.FirstOrDefault(x => x.Index == _selectedHoleIndex);
                if (h != null)
                    sel = $"  Selected={h.Index} (Gcode line {h.LineIndex + 1})";
            }

            TxtSummary.Text =
                $"HoleDia={_holeDia.ToString("0.###", inv)}  ZTop={_zHoleTop.ToString("0.###", inv)}  " +
                $"PtAng={_pointAngle.ToString("0.###", inv)}  Chamfer={_chamferLen.ToString("0.###", inv)}  " +
                $"Z+Ext={_zPlusExt.ToString("0.###", inv)}  DrillZ(Apex)={_drillZApex.ToString("0.###", inv)}  " +
                $"Holes={_holes.Count}{sel}";
        }

        // ============================================================
        // Overlay text (NOT zoomed) - from settings
        // ============================================================
        private void UpdateTopOverlay()
        {
            if (TopInfoText == null || TopInfoBorder == null)
                return;

            TopInfoText.Foreground = _graphicText;

            if (_selectedHoleIndex < 0)
            {
                TopInfoText.Text = "Click a hole to show its XY location.";
                TopInfoBorder.Visibility = Visibility.Visible;
                return;
            }

            var h = _holes.FirstOrDefault(x => x.Index == _selectedHoleIndex);
            if (h == null)
            {
                TopInfoText.Text = "Click a hole to show its XY location.";
                TopInfoBorder.Visibility = Visibility.Visible;
                return;
            }

            TopInfoText.Text =
                $"HOLE {h.Index}\n" +
                $"X={h.X:0.###}   Y={h.Y:0.###}\n" +
                $"Gcode line {h.LineIndex + 1}";
            TopInfoBorder.Visibility = Visibility.Visible;
        }

        private void UpdateSectionOverlay()
        {
            if (SectionInfoText == null || SectionInfoBorder == null)
                return;

            var inv = CultureInfo.InvariantCulture;

            double r = _holeDia / 2.0;
            double rTop = (_showChamfer && _chamferLen > 0.0) ? (r + _chamferLen) : r;

            double tipHeight = double.NaN;
            double coneBaseZ = double.NaN;

            if (_holeDia > 0 && _pointAngle > 0 && _pointAngle <= 180)
            {
                double halfAngleRad = (_pointAngle * 0.5) * Math.PI / 180.0;
                double tan = Math.Tan(halfAngleRad);
                if (Math.Abs(tan) > 1e-12)
                {
                    tipHeight = r / tan;
                    coneBaseZ = _drillZApex + tipHeight;
                }
            }

            double zChamferBot = _zHoleTop - _chamferLen;
            double zExtTop = _zHoleTop + _zPlusExt;

            string s =
                $"SECTION DIMS\n" +
                $"Dia={_holeDia.ToString("0.###", inv)}   R={r.ToString("0.###", inv)}\n" +
                $"RTop={rTop.ToString("0.###", inv)}   PtAng={_pointAngle.ToString("0.###", inv)}\n";

            if (!double.IsNaN(tipHeight) && !double.IsNaN(coneBaseZ))
                s += $"TipH={tipHeight.ToString("0.###", inv)}   ConeBaseZ={coneBaseZ.ToString("0.###", inv)}\n";

            s +=
                $"ZApex={_drillZApex.ToString("0.###", inv)}\n" +
                (_showChamfer && _chamferLen > 0.0
                    ? $"ChamferLen={_chamferLen.ToString("0.###", inv)}   ChamferBotZ={zChamferBot.ToString("0.###", inv)}\n"
                    : $"ChamferLen=0\n") +
                $"ZTop={_zHoleTop.ToString("0.###", inv)}   ExtTopZ={zExtTop.ToString("0.###", inv)}";

            SectionInfoText.Foreground = _graphicText;
            SectionInfoText.Text = s;
            SectionInfoBorder.Visibility = Visibility.Visible;
        }

        // ============================================================
        // LEFT PANEL: TOP VIEW (X,Y)
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

            double r = _holeDia / 2.0;
            double rChamferTop = r + _chamferLen;
            double rVis = r;
            if (_showChamfer && _chamferLen > 0.0)
                rVis = Math.Max(rVis, rChamferTop);

            double minX = _holes.Min(h => h.X - rVis);
            double maxX = _holes.Max(h => h.X + rVis);
            double minY = _holes.Min(h => h.Y - rVis);
            double maxY = _holes.Max(h => h.Y + rVis);

            double rangeX = maxX - minX; if (rangeX <= 0) rangeX = 1;
            double rangeY = maxY - minY; if (rangeY <= 0) rangeY = 1;

            double scaleX = (canvasW - 2 * margin) / rangeX;
            double scaleY = (canvasH - 2 * margin) / rangeY;
            double scale = Math.Min(scaleX, scaleY);

            DrawTopOriginAxes(minX, maxX, minY, maxY, scale, margin);

            // Selected thicknesses (kept similar to your previous look)
            double boreThk = Math.Max(0.5, _profileWidth);
            double boreThkSel = Math.Max(boreThk + 1.0, boreThk * 1.6);

            double chamferThk = Math.Max(0.5, _offsetWidth);
            double chamferThkSel = Math.Max(chamferThk + 1.0, chamferThk * 1.6);

            for (int i = 0; i < _holes.Count; i++)
            {
                var h = _holes[i];

                double sx = (h.X - minX) * scale + margin;
                double sy = (maxY - h.Y) * scale + margin;

                double sr = r * scale;
                double srChamfer = rChamferTop * scale;

                bool isSelected = (h.Index == _selectedHoleIndex);

                // Bore circle = PROFILE color/width
                var bore = new Ellipse
                {
                    Width = sr * 2,
                    Height = sr * 2,
                    Stroke = isSelected ? Brushes.Orange : _profileStroke,
                    StrokeThickness = isSelected ? boreThkSel : boreThk
                };
                Canvas.SetLeft(bore, sx - sr);
                Canvas.SetTop(bore, sy - sr);
                TopCanvas.Children.Add(bore);

                // Chamfer circle = OFFSET color/width (was grey before)
                if (_showChamfer && _chamferLen > 0.0)
                {
                    var ch = new Ellipse
                    {
                        Width = srChamfer * 2,
                        Height = srChamfer * 2,
                        Stroke = isSelected ? Brushes.Orange : _offsetStroke,
                        StrokeThickness = isSelected ? chamferThkSel : chamferThk
                    };
                    Canvas.SetLeft(ch, sx - srChamfer);
                    Canvas.SetTop(ch, sy - srChamfer);
                    TopCanvas.Children.Add(ch);
                }

                double cross = Math.Max(6.0, sr * 0.25);
                var cx1 = new Line { X1 = sx - cross, Y1 = sy, X2 = sx + cross, Y2 = sy, Stroke = DetailGrey, StrokeThickness = 1.2 };
                var cy1 = new Line { X1 = sx, Y1 = sy - cross, X2 = sx, Y2 = sy + cross, Stroke = DetailGrey, StrokeThickness = 1.2 };
                TopCanvas.Children.Add(cx1);
                TopCanvas.Children.Add(cy1);

                if (_showLabels)
                {
                    var label = new System.Windows.Controls.TextBlock
                    {
                        Text = $"{h.Index}",
                        Foreground = _graphicText,
                        FontFamily = MarkerFontFamily,
                        FontSize = 12
                    };
                    Canvas.SetLeft(label, sx + 6);
                    Canvas.SetTop(label, sy + 6);
                    TopCanvas.Children.Add(label);
                }

                _topHit.Add((h, new Point(sx, sy), sr));
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
            var xAxis = new Line { X1 = sx0 - len, Y1 = sy0, X2 = sx0 + len, Y2 = sy0, Stroke = DetailGrey, StrokeThickness = 1.2 };
            var yAxis = new Line { X1 = sx0, Y1 = sy0 - len, X2 = sx0, Y2 = sy0 + len, Stroke = DetailGrey, StrokeThickness = 1.2 };
            TopCanvas.Children.Add(xAxis);
            TopCanvas.Children.Add(yAxis);

            var t = new System.Windows.Controls.TextBlock
            {
                Text = "0,0",
                Foreground = _graphicText,
                FontFamily = MarkerFontFamily,
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
            HoleCenter best = null;

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
                _selectedHoleIndex = best.Index;

                RenderTopViewFit();
                RenderSectionFit();

                UpdateSummaryText();
                UpdateTopOverlay();
                UpdateSectionOverlay();
            }
        }

        private void BtnFitTop_Click(object sender, RoutedEventArgs e)
        {
            RenderTopViewFit();
            _topVp.Reset();
        }

        private void BtnResetTop_Click(object sender, RoutedEventArgs e)
        {
            _topVp.Reset();
        }

        // ============================================================
        // RIGHT PANEL: SECTION VIEW (R vs Z)
        // ============================================================
        private void RenderSectionFit()
        {
            SectionCanvas.Children.Clear();

            double canvasW = SectionCanvas.ActualWidth;
            double canvasH = SectionCanvas.ActualHeight;
            if (canvasW < 10) canvasW = 600;
            if (canvasH < 10) canvasH = 500;

            // +50% margin around the cross section
            double margin = 25.0 * 1.5; // 37.5

            var pts = BuildHoleSectionPoints_ZR();
            if (pts.Count < 2)
                return;

            double minZ = pts.Min(p => p.X);
            double maxZ = pts.Max(p => p.X);
            double minR = 0.0;
            double maxR = pts.Max(p => p.Y);

            double rangeZ = maxZ - minZ; if (rangeZ <= 0) rangeZ = 1;
            double rangeR = maxR - minR; if (rangeR <= 0) rangeR = 1;

            double scaleX = (canvasW - 2 * margin) / rangeZ;
            double scaleY = (canvasH - 2 * margin) / rangeR;
            double scale = Math.Min(scaleX, scaleY);

            double yCenter = (maxR - 0.0) * scale + margin;

            {
                var cl = new Line
                {
                    X1 = margin,
                    X2 = (maxZ - minZ) * scale + margin,
                    Y1 = yCenter,
                    Y2 = yCenter,
                    Stroke = DetailGrey,
                    StrokeThickness = 1.2
                };
                SectionCanvas.Children.Add(cl);
            }

            // Section outline = PROFILE color/width
            var poly = new Polyline
            {
                Stroke = _profileStroke,
                StrokeThickness = Math.Max(0.5, _profileWidth)
            };

            foreach (var wp in pts)
            {
                double sx = (wp.X - minZ) * scale + margin;
                double sy = (maxR - wp.Y) * scale + margin;
                poly.Points.Add(new Point(sx, sy));
            }
            SectionCanvas.Children.Add(poly);

            // Z markers (LEFT of the line if possible, stagger 1 down / 1 up)
            int slot = 0;
            DrawSectionZMarker(minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, slot++, _drillZApex, "Apex");
            DrawSectionZMarker(minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, slot++, GetConeBaseZ(), "ConeBase");

            if (_showChamfer && _chamferLen > 0.0)
                DrawSectionZMarker(minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, slot++, _zHoleTop - _chamferLen, "ChamferBot");

            DrawSectionZMarker(minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, slot++, _zHoleTop, "ZTop");

            if (_zPlusExt > 0.0)
                DrawSectionZMarker(minZ, maxZ, maxR, scale, margin, canvasW, canvasH, yCenter, slot++, _zHoleTop + _zPlusExt, "ExtTop");
        }

        private void DrawSectionZMarker(
            double minZ,
            double maxZ,
            double maxR,
            double scale,
            double margin,
            double canvasW,
            double canvasH,
            double yCenter,
            int labelSlot,
            double z,
            string label)
        {
            if (z < minZ - 1e-6 || z > maxZ + 1e-6)
                return;

            double x = (z - minZ) * scale + margin;
            double yTop = margin;
            double yBot = (maxR - 0.0) * scale + margin;

            var ln = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = yTop,
                Y2 = yBot,
                Stroke = SecondaryGrey,
                StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            SectionCanvas.Children.Add(ln);

            // --- stagger: slot 0 down, 1 up, 2 down, 3 up ...
            int level = labelSlot / 2;
            bool down = (labelSlot % 2 == 0);

            double yLabel;
            if (down)
                yLabel = yCenter + MarkerLabelPad + (level * MarkerRowStep);
            else
                yLabel = yCenter - MarkerLabelPad - 14.0 - (level * MarkerRowStep);

            // clamp inside view
            double minY = margin;
            double maxY = Math.Max(margin, canvasH - margin - 14.0);
            if (yLabel < minY) yLabel = minY;
            if (yLabel > maxY) yLabel = maxY;

            // --- left-justify: try to place text to the LEFT of the marker line
            double textW = MeasureTextWidth(label, MarkerFontFamily, MarkerFontSize);
            double xLeft = x - 4.0 - textW;

            // if it would go off the left side, flip to the right
            if (xLeft < margin)
                xLeft = x + 4.0;

            // and clamp on right side too (so we never lose "ExtTop")
            double maxLeft = Math.Max(margin, canvasW - margin - textW);
            if (xLeft > maxLeft) xLeft = maxLeft;

            var t = new System.Windows.Controls.TextBlock
            {
                Text = label,
                Foreground = _graphicText,
                FontFamily = MarkerFontFamily,
                FontSize = MarkerFontSize,
                TextAlignment = TextAlignment.Left
            };
            Canvas.SetLeft(t, xLeft);
            Canvas.SetTop(t, yLabel);
            SectionCanvas.Children.Add(t);
        }

        private double MeasureTextWidth(string text, FontFamily family, double size)
        {
            // WPF text measurement (prevents clipping on the right)
            double dip = 1.0;
            try { dip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { dip = 1.0; }

            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size,
                Brushes.Black,
                dip);

            return ft.WidthIncludingTrailingWhitespace;
        }

        private List<Point> BuildHoleSectionPoints_ZR()
        {
            var pts = new List<Point>();

            if (_holeDia <= 0)
                return pts;

            if (_pointAngle <= 0 || _pointAngle > 180)
                return pts;

            double r = _holeDia / 2.0;

            double halfAngleRad = (_pointAngle * 0.5) * Math.PI / 180.0;
            double tan = Math.Tan(halfAngleRad);
            if (Math.Abs(tan) < 1e-12)
                return pts;

            double tipHeight = r / tan;
            double coneBaseZ = _drillZApex + tipHeight;

            double zChamferBot = _zHoleTop - _chamferLen;
            double zChamferTop = _zHoleTop;
            double zExtTop = _zHoleTop + _zPlusExt;

            double rAtTop = r;
            if (_showChamfer && _chamferLen > 0.0)
                rAtTop = r + _chamferLen;

            pts.Add(new Point(_drillZApex, 0.0));
            pts.Add(new Point(coneBaseZ, r));

            if (_showChamfer && _chamferLen > 0.0)
                pts.Add(new Point(zChamferBot, r));

            pts.Add(new Point(zChamferTop, rAtTop));

            if (_zPlusExt > 0.0)
                pts.Add(new Point(zExtTop, rAtTop));

            return pts;
        }

        private double GetConeBaseZ()
        {
            double r = _holeDia / 2.0;
            double halfAngleRad = (_pointAngle * 0.5) * Math.PI / 180.0;
            double tan = Math.Tan(halfAngleRad);
            if (Math.Abs(tan) < 1e-12)
                return _drillZApex;

            double tipHeight = r / tan;
            return _drillZApex + tipHeight;
        }

        private void SectionCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Point p = e.GetPosition(SectionCanvas);
            _secVp.ZoomAtPoint(p, e.Delta);
            e.Handled = true;
        }

        private void SectionCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SectionCanvas.Focus();
        }

        private void BtnFitSection_Click(object sender, RoutedEventArgs e)
        {
            RenderSectionFit();
            _secVp.Reset();
        }

        private void BtnResetSection_Click(object sender, RoutedEventArgs e)
        {
            _secVp.Reset();
        }
    }
}
