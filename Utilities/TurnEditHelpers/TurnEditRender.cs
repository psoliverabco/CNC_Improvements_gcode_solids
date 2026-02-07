// File: Utilities/TurnEditHelpers/TurnEditRender.cs
using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CNC_Improvements_gcode_solids.Utilities.TurnEditHelpers
{
    /// <summary>
    /// Rendering-only helpers for TurnEditWindow.
    /// No geometry decisions here; just drawing primitives onto a Canvas.
    ///
    /// Contract:
    /// - screen X = world Z
    /// - screen Y = inverted world X
    /// - arc center is NEVER recomputed; rendering uses A/M/B + C with MID containment to pick sweep
    /// </summary>
    public sealed class TurnEditRender
    {
        // -----------------------------
        // Public contracts / adapters
        // -----------------------------

        public enum SegKind { Line, Arc }

        /// <summary>
        /// Minimal render-view of a segment (adapter). Keep your EditSeg model wherever you want;
        /// TurnEditWindow can project its segments into these views (or implement these interfaces later).
        /// </summary>
        public interface IEditSegView
        {
            int Index { get; }
            SegKind Kind { get; }

            // World coordinates: Point.X = world X, Point.Y = world Z
            Point A { get; }
            Point B { get; }

            // Optional style (if null, renderer uses defaults)
            Brush? Stroke { get; }
            double? Thickness { get; }
        }

        public interface IEditArcSegView : IEditSegView
        {
            // World coordinates: Point.X = world X, Point.Y = world Z
            Point M { get; }      // MID point on intended sweep
            Point C { get; }      // Center (authoritative)
        }

        private static double Norm2Pi(double a) => TurnEditMath.Norm2Pi(a);
        private static double DeltaCCW(double angFrom, double angTo) => TurnEditMath.DeltaCCW(angFrom, angTo);
        private static double DeltaCW(double angFrom, double angTo) => TurnEditMath.DeltaCW(angFrom, angTo);
        private static double Dist(Point a, Point b) => TurnEditMath.Dist(a, b);

        // ============================================================
        // PICK (selection) helper: choose nearest segment by pick-debug points
        // ============================================================

        public int PickClosestSegByPickPoints(Point clickCanvas, out double bestDist)
        {
            bestDist = double.PositiveInfinity;

            if (_pickDebugPtsCanvasBySeg.Count == 0)
                return -1;

            int bestIdx = -1;
            double bestD2 = double.PositiveInfinity;

            foreach (var kv in _pickDebugPtsCanvasBySeg)
            {
                int segIndex = kv.Key;
                var pts = kv.Value;
                if (pts == null || pts.Count == 0)
                    continue;

                for (int i = 0; i < pts.Count; i++)
                {
                    double dx = pts[i].X - clickCanvas.X;
                    double dy = pts[i].Y - clickCanvas.Y;
                    double d2 = dx * dx + dy * dy;

                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        bestIdx = segIndex;
                    }
                }
            }

            if (bestIdx >= 0)
                bestDist = Math.Sqrt(bestD2);

            return bestIdx;
        }

        // -----------------------------
        // Options
        // -----------------------------

        public sealed class Options
        {
            // Tags used for removal
            public object TagGeom { get; set; } = "TAG_GEOM";
            public object TagPick { get; set; } = "TAG_PICK";
            public object TagPickDebug { get; set; } = "TAG_PICK_DEBUG";
            public object TagPreview { get; set; } = "PREVIEW";

            // Fit + canvas sizing
            public double FitPaddingPx { get; set; } = 30.0;
            public bool SetCanvasTo3xViewport { get; set; } = true;

            // Arc draw sampling
            public int ArcSampleCount { get; set; } = 96;

            // Pick debug density + visuals
            public double PickDebugOpacity { get; set; } = 0.9;
            public double PickDebugPointDiamPx { get; set; } = 5.0; // base (will be divided by zoom)
            public double PickDebugSpacingWorld { get; set; } = 2.0; // world units (your mm)
            public int PickDebugMinPoints { get; set; } = 2;
            public int PickDebugMaxPoints { get; set; } = 200;

            // Default geometry style (if seg doesn't provide)
            public Brush DefaultStroke { get; set; } = Brushes.White;
            public double DefaultThickness { get; set; } = 1.5;
        }

        // -----------------------------
        // External references
        // -----------------------------

        private readonly Canvas _editCanvas;
        private readonly FrameworkElement _viewportHost;
        private readonly ScaleTransform _scale;
        private readonly TranslateTransform _translate;
        private readonly TransformGroup _transformGroup;

        private double _padX;
        private double _padY;

        private readonly Options _opt;

        // -----------------------------
        // Map state (moved from window)
        // -----------------------------

        private bool _mapValid;

        private double _mapMinX, _mapMaxX;
        private double _mapMinZ, _mapMaxZ;

        private double _mapScale;
        private double _mapBaseX, _mapBaseY;

        // -----------------------------
        // Render-owned dictionaries (moved from window)
        // -----------------------------

        private readonly Dictionary<int, Shape> _shapeBySegIndex = new Dictionary<int, Shape>();
        private readonly Dictionary<int, (Brush stroke, double thick)> _baseStyleBySegIndex = new Dictionary<int, (Brush, double)>();

        // -----------------------------
        // Pick debug infrastructure (moved from window)
        // -----------------------------

        // SegIndex -> list of points in CANVAS coords
        private readonly Dictionary<int, List<Point>> _pickDebugPtsCanvasBySeg = new Dictionary<int, List<Point>>();

        private Point _pickDebugHitCanvas;
        private bool _pickDebugHitValid;
        private Shape? _pickDebugHitShape;

        // -----------------------------
        // Public exposure (read-only)
        // -----------------------------

        public bool MapValid => _mapValid;

        public IReadOnlyDictionary<int, Shape> ShapeBySegIndex => _shapeBySegIndex;
        public IReadOnlyDictionary<int, (Brush stroke, double thick)> BaseStyleBySegIndex => _baseStyleBySegIndex;

        public bool PickDebugHitValid => _pickDebugHitValid;
        public Point PickDebugHitCanvas => _pickDebugHitCanvas;

        public TurnEditRender(
            Canvas editCanvas,
            FrameworkElement viewportHost,
            ScaleTransform scale,
            TranslateTransform translate,
            TransformGroup transformGroup,
            double padX,
            double padY,
            Options? options = null)
        {
            _editCanvas = editCanvas ?? throw new ArgumentNullException(nameof(editCanvas));
            _viewportHost = viewportHost ?? throw new ArgumentNullException(nameof(viewportHost));
            _scale = scale ?? throw new ArgumentNullException(nameof(scale));
            _translate = translate ?? throw new ArgumentNullException(nameof(translate));
            _transformGroup = transformGroup ?? throw new ArgumentNullException(nameof(transformGroup));

            _padX = padX;
            _padY = padY;

            _opt = options ?? new Options();
        }

        public void UpdatePad(double padX, double padY)
        {
            _padX = padX;
            _padY = padY;
        }

        // ============================================================
        // Core pipeline
        // ============================================================

        public void RenderEditGeometry(IReadOnlyList<IEditSegView> segs)
        {
            if (segs == null) throw new ArgumentNullException(nameof(segs));

            // Fit-to-view mapping + reset zoom/pan
            FitMapToViewport(segs);

            // Clear geom+pick+pickdebug (preview untouched by tags)
            ClearGeometryOnly();



            // Draw grid (renderer-owned) BEFORE geometry so it's behind it
            DrawBackgroundGridWorld();

            // NOTE: grid is NOT cleared by ClearGeometryOnly().
            // It is owned separately and can be redrawn on zoom/pan.
            // (TurnEditWindow should call DrawBackgroundGridWorld() as needed.)

            // Draw geometry
            DrawSegs(segs);

            // Rebuild + draw pick debug points
            RebuildPickDebugPoints(segs);
            RenderPickDebugPoints();
        }

        public void ClearGeometryOnly()
        {
            // Remove only tags: GEOM, PICK, PICK_DEBUG
            var toRemove = new List<UIElement>();

            foreach (UIElement el in _editCanvas.Children)
            {
                if (el is FrameworkElement fe)
                {
                    var t = fe.Tag;
                    if (Equals(t, _opt.TagGeom) || Equals(t, _opt.TagPick) || Equals(t, _opt.TagPickDebug))
                        toRemove.Add(el);
                }
            }

            foreach (var el in toRemove)
                _editCanvas.Children.Remove(el);

            _shapeBySegIndex.Clear();
            _baseStyleBySegIndex.Clear();

            _pickDebugPtsCanvasBySeg.Clear();
            _pickDebugHitValid = false;
            _pickDebugHitCanvas = new Point();
            _pickDebugHitShape = null;
        }

        // ============================================================
        // World <-> Screen mapping
        // ============================================================

        /// <summary>
        /// Map world (X,Z) to screen (canvas) with:
        /// screenX = baseX + (worldZ - minZ) * scale
        /// screenY = baseY + (maxX - worldX) * scale   (inverted X)
        /// </summary>
        public Point WorldToScreen(Point worldXZ)
        {
            if (!_mapValid)
                return new Point(0, 0);

            double worldX = worldXZ.X;
            double worldZ = worldXZ.Y;

            double sx = _mapBaseX + (worldZ - _mapMinZ) * _mapScale;
            double sy = _mapBaseY + (_mapMaxX - worldX) * _mapScale;

            return new Point(sx, sy);
        }

        /// <summary>
        /// Inverse of WorldToScreen.
        /// </summary>
        public Point ScreenToWorld(Point screen)
        {
            if (!_mapValid || _mapScale == 0)
                return new Point(0, 0);

            double worldZ = _mapMinZ + (screen.X - _mapBaseX) / _mapScale;
            double worldX = _mapMaxX - (screen.Y - _mapBaseY) / _mapScale;

            return new Point(worldX, worldZ);
        }

        /// <summary>
        /// Visible rectangle in CANVAS coordinates of the drawn geometry (accounts for current pan/zoom)
        /// because pan/zoom is a RenderTransform on EditCanvas children.
        /// </summary>
        public Rect GetVisibleCanvasRectInCanvasCoords()
        {
            double w = Math.Max(0, _viewportHost.ActualWidth);
            double h = Math.Max(0, _viewportHost.ActualHeight);

            if (w <= 0 || h <= 0)
                return Rect.Empty;

            // If the transform isn't invertible (or isn't the one actually applied),
            // fall back to treating viewport coords as canvas coords so grid still draws.
            var m = _transformGroup?.Value ?? Matrix.Identity;
            if (!m.HasInverse)
            {
                return new Rect(new Point(0, 0), new Size(w, h));
            }

            m.Invert();

            var p0 = m.Transform(new Point(0, 0));
            var p1 = m.Transform(new Point(w, 0));
            var p2 = m.Transform(new Point(0, h));
            var p3 = m.Transform(new Point(w, h));

            double minX = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
            double maxX = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
            double minY = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
            double maxY = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));

            // Safety: if something went NaN/Inf, fallback so grid still draws.
            if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
                return new Rect(new Point(0, 0), new Size(w, h));

            return new Rect(new Point(minX, minY), new Point(maxX, maxY));
        }


        // ============================================================
        // Fit-to-view mapping
        // ============================================================

        private void FitMapToViewport(IReadOnlyList<IEditSegView> segs)
        {
            _mapValid = false;

            double vw = Math.Max(0, _viewportHost.ActualWidth);
            double vh = Math.Max(0, _viewportHost.ActualHeight);

            if (vw < 5 || vh < 5)
                return;

            if (_opt.SetCanvasTo3xViewport)
            {
                _editCanvas.Width = vw * 3.0;
                _editCanvas.Height = vh * 3.0;
            }

            // Bounds in world
            if (!TryComputeWorldBounds(segs, out double minX, out double maxX, out double minZ, out double maxZ))
                return;

            // Guard zero extents
            double spanX = maxX - minX;
            double spanZ = maxZ - minZ;

            if (spanX < 1e-9) spanX = 1.0;
            if (spanZ < 1e-9) spanZ = 1.0;

            double pad = Math.Max(0, _opt.FitPaddingPx);

            double usableW = Math.Max(1.0, vw - 2 * pad);
            double usableH = Math.Max(1.0, vh - 2 * pad);

            // screenX maps Z-span, screenY maps X-span
            double sX = usableH / spanX; // height for X-span (inverted X)
            double sZ = usableW / spanZ; // width for Z-span
            double s = Math.Min(sX, sZ);

            if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0)
                s = 1.0;

            _mapMinX = minX;
            _mapMaxX = maxX;
            _mapMinZ = minZ;
            _mapMaxZ = maxZ;

            _mapScale = s;

            // “3x canvas center shift” (draw into center third)
            _mapBaseX = _padX + pad;
            _mapBaseY = _padY + pad;

            // Reset zoom/pan on fit
            _scale.ScaleX = 1.0;
            _scale.ScaleY = 1.0;

            _translate.X = -_padX;
            _translate.Y = -_padY;

            _mapValid = true;
        }

        private bool TryComputeWorldBounds(IReadOnlyList<IEditSegView> segs, out double minX, out double maxX, out double minZ, out double maxZ)
        {
            minX = double.PositiveInfinity;
            maxX = double.NegativeInfinity;
            minZ = double.PositiveInfinity;
            maxZ = double.NegativeInfinity;

            bool any = false;

            foreach (var s in segs)
            {
                if (s == null) continue;

                ExpandWorldBounds(ref minX, ref maxX, ref minZ, ref maxZ, s.A);
                ExpandWorldBounds(ref minX, ref maxX, ref minZ, ref maxZ, s.B);
                any = true;

                if (s.Kind == SegKind.Arc && s is IEditArcSegView a)
                {
                    ExpandWorldBounds(ref minX, ref maxX, ref minZ, ref maxZ, a.M);

                    // For fit: include quadrant extrema that lie on the chosen sweep
                    IncludeArcExtremaForBounds(a, ref minX, ref maxX, ref minZ, ref maxZ);
                }
            }

            return any && minX < maxX && minZ < maxZ;
        }

        private static void ExpandWorldBounds(ref double minX, ref double maxX, ref double minZ, ref double maxZ, Point worldXZ)
        {
            double x = worldXZ.X;
            double z = worldXZ.Y;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        private void IncludeArcExtremaForBounds(IEditArcSegView a, ref double minX, ref double maxX, ref double minZ, ref double maxZ)
        {
            Point A = a.A;
            Point B = a.B;
            Point M = a.M;
            Point C = a.C;

            double r = Dist(C, A);
            if (r <= 1e-9)
                return;

            // pick sweep using MID containment (authoritative)
            double angA = Math.Atan2(A.Y - C.Y, A.X - C.X);
            double angB = Math.Atan2(B.Y - C.Y, B.X - C.X);
            double angM = Math.Atan2(M.Y - C.Y, M.X - C.X);

            bool useCCW = IsMidOnCCWSweep(angA, angB, angM);
            double sweep = useCCW ? DeltaCCW(angA, angB) : -DeltaCW(angA, angB);

            // candidate extrema angles
            double[] extrema = { 0, Math.PI * 0.5, Math.PI, Math.PI * 1.5 };

            foreach (double angE in extrema)
            {
                if (AngleOnDirectedSweep(angA, sweep, angE))
                {
                    double x = C.X + r * Math.Cos(angE);
                    double z = C.Y + r * Math.Sin(angE);
                    ExpandWorldBounds(ref minX, ref maxX, ref minZ, ref maxZ, new Point(x, z));
                }
            }
        }

        // ============================================================
        // Drawing primitives (GEOM)
        // ============================================================

        private void DrawSegs(IReadOnlyList<IEditSegView> segs)
        {
            foreach (var s in segs)
            {
                if (s == null) continue;

                Brush stroke = s.Stroke ?? _opt.DefaultStroke;
                double thick = s.Thickness ?? _opt.DefaultThickness;

                if (s.Kind == SegKind.Line)
                {
                    DrawLineSeg(s.Index, s.A, s.B, stroke, thick);
                }
                else if (s.Kind == SegKind.Arc && s is IEditArcSegView a)
                {
                    DrawArcSeg(a, stroke, thick);
                }
            }
        }

        private void DrawLineSeg(int segIndex, Point A, Point B, Brush stroke, double thick)
        {
            Point p1 = WorldToScreen(A);
            Point p2 = WorldToScreen(B);

            var ln = new Line
            {
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y,
                Stroke = stroke,
                StrokeThickness = thick,
                SnapsToDevicePixels = true,
                Tag = _opt.TagGeom
            };

            _editCanvas.Children.Add(ln);

            _shapeBySegIndex[segIndex] = ln;
            _baseStyleBySegIndex[segIndex] = (stroke, thick);
        }

        private void DrawArcSeg(IEditArcSegView a, Brush stroke, double thick)
        {
            Point A = a.A;
            Point B = a.B;
            Point M = a.M;
            Point C = a.C;

            double r = Dist(C, A);
            if (r <= 1e-9)
                return;

            double angA = Math.Atan2(A.Y - C.Y, A.X - C.X);
            double angB = Math.Atan2(B.Y - C.Y, B.X - C.X);
            double angM = Math.Atan2(M.Y - C.Y, M.X - C.X);

            bool useCCW = IsMidOnCCWSweep(angA, angB, angM);
            double sweep = useCCW ? DeltaCCW(angA, angB) : -DeltaCW(angA, angB);

            int n = Math.Max(3, _opt.ArcSampleCount);
            var pl = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = thick,
                Tag = _opt.TagGeom,
                SnapsToDevicePixels = true
            };

            for (int i = 0; i < n; i++)
            {
                double t = (n == 1) ? 0 : (double)i / (n - 1);
                double ang = angA + t * sweep;

                double x = C.X + r * Math.Cos(ang);
                double z = C.Y + r * Math.Sin(ang);

                pl.Points.Add(WorldToScreen(new Point(x, z)));
            }

            _editCanvas.Children.Add(pl);

            _shapeBySegIndex[a.Index] = pl;
            _baseStyleBySegIndex[a.Index] = (stroke, thick);
        }

        /// <summary>
        /// MID containment rule (authoritative):
        /// If M lies on CCW sweep A->B, choose CCW; else choose CW.
        /// </summary>
        private static bool IsMidOnCCWSweep(double angA, double angB, double angM)
        {
            double ab = DeltaCCW(angA, angB);
            double am = DeltaCCW(angA, angM);
            // allow tiny epsilon
            return am <= ab + 1e-10;
        }

        /// <summary>
        /// True if angTest lies on directed sweep that starts at angStart and runs by "sweep" radians (can be negative).
        /// </summary>
        private static bool AngleOnDirectedSweep(double angStart, double sweep, double angTest)
        {
            // normalize to [0,2pi)
            angStart = Norm2Pi(angStart);
            angTest = Norm2Pi(angTest);

            if (Math.Abs(sweep) < 1e-12)
                return Math.Abs(Norm2Pi(angTest - angStart)) < 1e-12;

            if (sweep > 0)
            {
                // CCW: test if deltaCCW(start->test) <= sweep
                double dt = DeltaCCW(angStart, angTest);
                return dt <= sweep + 1e-10;
            }
            else
            {
                // CW: sweep negative; test if deltaCW(start->test) <= -sweep
                double dt = DeltaCW(angStart, angTest);
                return dt <= (-sweep) + 1e-10;
            }
        }

        // ============================================================
        // PREVIEW LAYER (TagPreview) - renderer-owned
        // ============================================================

        public void ClearPreviewOnly()
        {
            var toRemove = new List<UIElement>();

            foreach (UIElement el in _editCanvas.Children)
            {
                if (el is FrameworkElement fe && Equals(fe.Tag, _opt.TagPreview))
                    toRemove.Add(el);
            }

            foreach (var el in toRemove)
                _editCanvas.Children.Remove(el);
        }

        public void DrawPreviewPolylineWorld(IReadOnlyList<Point> worldPts, Brush stroke, double thickness, double opacity)
        {
            if (!_mapValid) return;
            if (worldPts == null || worldPts.Count < 2) return;

            var poly = new Polyline
            {
                Stroke = stroke ?? Brushes.Yellow,
                StrokeThickness = Math.Max(1.0, thickness),
                Opacity = opacity,
                Tag = _opt.TagPreview,
                IsHitTestVisible = false
            };

            for (int i = 0; i < worldPts.Count; i++)
                poly.Points.Add(WorldToScreen(worldPts[i]));

            _editCanvas.Children.Add(poly);
        }

        public void DrawPreviewPointWorld(Point worldPt, Brush fill, double diamPx, double opacity)
        {
            if (!_mapValid) return;

            // keep point size zoom-invariant
            double scale = Math.Max(1e-9, _scale.ScaleX);
            double invScale = 1.0 / scale;

            double d = Math.Max(2.0, diamPx) * invScale;

            Point sp = WorldToScreen(worldPt);

            var e = new Ellipse
            {
                Width = d,
                Height = d,
                Fill = fill ?? Brushes.Orange,
                Stroke = Brushes.Transparent,
                StrokeThickness = 0,
                Opacity = opacity,
                Tag = _opt.TagPreview,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(e, sp.X - d * 0.5);
            Canvas.SetTop(e, sp.Y - d * 0.5);

            _editCanvas.Children.Add(e);
        }

        // ============================================================
        // Pick debug (TAG_PICK_DEBUG)
        // ============================================================

        public void ClearPickDebugOnly()
        {
            var toRemove = new List<UIElement>();

            foreach (UIElement el in _editCanvas.Children)
            {
                if (el is FrameworkElement fe && Equals(fe.Tag, _opt.TagPickDebug))
                    toRemove.Add(el);
            }

            foreach (var el in toRemove)
                _editCanvas.Children.Remove(el);

            _pickDebugPtsCanvasBySeg.Clear();
            _pickDebugHitValid = false;
            _pickDebugHitCanvas = new Point();
            _pickDebugHitShape = null;
        }

        public void RebuildPickDebugPoints(IReadOnlyList<IEditSegView> segs)
        {
            _pickDebugPtsCanvasBySeg.Clear();

            foreach (var s in segs)
            {
                if (s == null) continue;
                var pts = BuildPickDebugPointsWorld(s);
                var ptsCanvas = pts.Select(WorldToScreen).ToList();
                _pickDebugPtsCanvasBySeg[s.Index] = ptsCanvas;
            }
        }

        private List<Point> BuildPickDebugPointsWorld(IEditSegView seg)
        {
            var pts = new List<Point>();

            if (seg.Kind == SegKind.Line)
            {
                Point A = seg.A;
                Point B = seg.B;
                double len = Dist(A, B);

                int n = ComputePickCount(len);
                if (n < 2) n = 2;

                for (int i = 0; i < n; i++)
                {
                    double t = (n == 1) ? 0 : (double)i / (n - 1);
                    pts.Add(new Point(
                        A.X + (B.X - A.X) * t,
                        A.Y + (B.Y - A.Y) * t));
                }

                return pts;
            }

            if (seg.Kind == SegKind.Arc && seg is IEditArcSegView a)
            {
                Point A = a.A;
                Point B = a.B;
                Point M = a.M;
                Point C = a.C;

                double r = Dist(C, A);
                if (r <= 1e-9)
                {
                    pts.Add(A);
                    pts.Add(B);
                    return pts;
                }

                double angA = Math.Atan2(A.Y - C.Y, A.X - C.X);
                double angB = Math.Atan2(B.Y - C.Y, B.X - C.X);
                double angM = Math.Atan2(M.Y - C.Y, M.X - C.X);

                bool useCCW = IsMidOnCCWSweep(angA, angB, angM);
                double sweep = useCCW ? DeltaCCW(angA, angB) : -DeltaCW(angA, angB);

                double arcLen = r * Math.Abs(sweep);
                int n = ComputePickCount(arcLen);
                if (n < 2) n = 2;

                for (int i = 0; i < n; i++)
                {
                    double t = (n == 1) ? 0 : (double)i / (n - 1);
                    double ang = angA + t * sweep;

                    double x = C.X + r * Math.Cos(ang);
                    double z = C.Y + r * Math.Sin(ang);

                    pts.Add(new Point(x, z));
                }

                return pts;
            }

            // fallback
            pts.Add(seg.A);
            pts.Add(seg.B);
            return pts;
        }

        private int ComputePickCount(double lengthWorld)
        {
            double spacing = Math.Max(1e-9, _opt.PickDebugSpacingWorld);

            int n = (int)Math.Ceiling(lengthWorld / spacing) + 1;
            n = Math.Max(_opt.PickDebugMinPoints, n);
            n = Math.Min(_opt.PickDebugMaxPoints, n);
            return n;
        }

        public void RenderPickDebugPoints()
        {
            // Clear any existing pick debug shapes (but keep GEOM)
            var toRemove = new List<UIElement>();

            foreach (UIElement el in _editCanvas.Children)
            {
                if (el is FrameworkElement fe && Equals(fe.Tag, _opt.TagPickDebug))
                    toRemove.Add(el);
            }

            foreach (var el in toRemove)
                _editCanvas.Children.Remove(el);

            _pickDebugHitShape = null;

            double zoom = Math.Max(1e-9, _scale.ScaleX);
            double diam = _opt.PickDebugPointDiamPx / zoom;

            foreach (var kv in _pickDebugPtsCanvasBySeg)
            {
                var pts = kv.Value;
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = pts[i];

                    var e = new Ellipse
                    {
                        Width = diam,
                        Height = diam,
                        Fill = Brushes.Lime,
                        Stroke = Brushes.Transparent,
                        Opacity = _opt.PickDebugOpacity,
                        Tag = _opt.TagPickDebug,
                        IsHitTestVisible = false
                    };

                    Canvas.SetLeft(e, p.X - diam / 2.0);
                    Canvas.SetTop(e, p.Y - diam / 2.0);

                    _editCanvas.Children.Add(e);
                }
            }

            if (_pickDebugHitValid)
                DrawPickDebugHitMarker();
        }

        public void UpdatePickDebugHitFromSeg(int segIndex, Point clickCanvas)
        {
            _pickDebugHitValid = false;
            _pickDebugHitCanvas = new Point();

            if (!_pickDebugPtsCanvasBySeg.TryGetValue(segIndex, out var pts) || pts.Count == 0)
                return;

            double bestD2 = double.PositiveInfinity;
            Point best = new Point();

            for (int i = 0; i < pts.Count; i++)
            {
                double dx = pts[i].X - clickCanvas.X;
                double dy = pts[i].Y - clickCanvas.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = pts[i];
                }
            }

            _pickDebugHitCanvas = best;
            _pickDebugHitValid = true;
        }

        public void DrawPickDebugHitMarker()
        {
            // Remove old marker only (do not rebuild points here)
            if (_pickDebugHitShape != null)
                _editCanvas.Children.Remove(_pickDebugHitShape);

            _pickDebugHitShape = null;

            if (!_pickDebugHitValid)
                return;

            double zoom = Math.Max(1e-9, _scale.ScaleX);
            double diam = (_opt.PickDebugPointDiamPx * 2.2) / zoom;

            var e = new Ellipse
            {
                Width = diam,
                Height = diam,
                Fill = Brushes.Orange,
                Stroke = Brushes.Transparent,
                Opacity = 0.95,
                Tag = _opt.TagPickDebug,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(e, _pickDebugHitCanvas.X - diam / 2.0);
            Canvas.SetTop(e, _pickDebugHitCanvas.Y - diam / 2.0);

            _editCanvas.Children.Add(e);
            _pickDebugHitShape = e;
        }

        // ============================================================
        // Failure marker (optional render helper)
        // ============================================================

        public void DrawFailureStarAtWorldPoint(Point worldXZ)
        {
            if (!_mapValid)
                return;

            var p = WorldToScreen(worldXZ);

            var tb = new TextBlock
            {
                Text = "*",
                Foreground = Brushes.Red,
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Tag = _opt.TagGeom,
                IsHitTestVisible = false
            };

            // Center-ish
            Canvas.SetLeft(tb, p.X - 8);
            Canvas.SetTop(tb, p.Y - 18);

            _editCanvas.Children.Add(tb);
        }

        // ============================================================
        // GRID (background) - ClosingColor + dynamic step to keep ~60..70 lines across view
        // - Grid is owned by renderer only
        // - Grid is drawn BEHIND geometry (we Insert at 0)
        // - Step only recalculates when count leaves [60..70] band (hysteresis)
        // - X axis labels on RIGHT are DIAMETER (2*X) but spacing is in radius-world
        // ============================================================

        private const string TAG_GRID = "TAG_GRID";

        private const int GRID_MIN_COUNT = 60;
        private const int GRID_MAX_COUNT = 70;
        private const int GRID_MAJOR_EVERY = 5;

        private double _gridStepWorld = 20.0; // cached (world mm)
        private double _gridLastBaseStep = 20.0;

        public void ClearGridOnly()
        {
            var toRemove = new List<UIElement>();

            foreach (UIElement el in _editCanvas.Children)
            {
                if (el is FrameworkElement fe && fe.Tag is string s && s == TAG_GRID)
                    toRemove.Add(el);
            }

            foreach (var el in toRemove)
                _editCanvas.Children.Remove(el);
        }

        public void DrawBackgroundGridWorld(double baseStepMm = 20.0)
        {
            if (!_mapValid)
                return;

            if (!double.IsFinite(baseStepMm) || baseStepMm <= 1e-9)
                baseStepMm = 20.0;

            if (!double.IsFinite(_gridStepWorld) || _gridStepWorld <= 1e-9 || Math.Abs(_gridLastBaseStep - baseStepMm) > 1e-9)
            {
                _gridStepWorld = baseStepMm;
                _gridLastBaseStep = baseStepMm;
            }

            // remove old grid
            ClearGridOnly();

            Brush gridBrush = BrushFromHex(Settings.Default.GridColor, Brushes.Gray);

            // subtle grid
            double opacityMinor = 0.30;
            double opacityMajor = 0.55;

            // ---- thickness should be constant in *screen pixels* (not scaled by zoom) ----
            double zoom = Math.Max(1e-9, _scale.ScaleX);
            double invZoom = 1.0 / zoom;

            // Treat GridWidth as "px at zoom=1"
            double minorPx = Math.Max(0.5, Settings.Default.GridWidth * 0.50);
            double majorPx = Math.Max(minorPx + 0.3, Settings.Default.GridWidth * 0.80);

            // Convert px -> canvas units so after ScaleTransform it stays px on screen
            double thkMinor = minorPx * invZoom;
            double thkMajor = majorPx * invZoom;


            // Visible world rect based on current pan/zoom
            Rect wr = GetVisibleWorldRect();
            if (wr.Width <= 0 || wr.Height <= 0)
                return;

            // NOTE: wr.Width is worldX span, wr.Height is worldZ span (Rect.Y is worldZ)
            double spanZ = wr.Height;
            if (!double.IsFinite(spanZ) || spanZ <= 1e-9)
                spanZ = 1.0;

            // Hysteresis: only update step when count leaves band
            double count = spanZ / _gridStepWorld;
            if (count < GRID_MIN_COUNT)
            {
                _gridStepWorld = spanZ / GRID_MAX_COUNT;
                _gridStepWorld = QuantizeNiceStep(_gridStepWorld);
            }
            else if (count > GRID_MAX_COUNT)
            {
                _gridStepWorld = spanZ / GRID_MIN_COUNT;
                _gridStepWorld = QuantizeNiceStep(_gridStepWorld);
            }

            double step = Math.Max(1e-6, _gridStepWorld);

            // Expand slightly so edges are covered
            wr.Inflate(step * 2.0, step * 2.0);

            // Snap starting values
            double minX = wr.X;
            double maxX = wr.X + wr.Width;

            double minZ = wr.Y;
            double maxZ = wr.Y + wr.Height;

            // Snap using integer grid indices so 0 is always representable.
            // (Prevents floating drift like z += step missing 0.)
            const double originX = 0.0;
            const double originZ = 0.0;

            int kZ0 = (int)Math.Floor(((minZ - originZ) / step) - 1e-9);
            int kZ1 = (int)Math.Ceiling(((maxZ - originZ) / step) + 1e-9);

            int kX0 = (int)Math.Floor(((minX - originX) / step) - 1e-9);
            int kX1 = (int)Math.Ceiling(((maxX - originX) / step) + 1e-9);


            // Visible canvas rect (for label placement)
            Rect visCanvas = GetVisibleCanvasRectInCanvasCoords();
            if (visCanvas.IsEmpty)
                return;

             invZoom = 1.0 / Math.Max(1e-9, _scale.ScaleX);
            double fontPx = 12.0 * invZoom;

            // ---- vertical lines (constant Z) across screen width ----
            int iz = 0;
            for (int k = kZ0; k <= kZ1; k++)
            {
                double z = originZ + k * step;
                bool major = (Math.Abs(k) % GRID_MAJOR_EVERY) == 0;

                Point wA = new Point(minX, z);
                Point wB = new Point(maxX, z);

                Point cA = WorldToScreen(wA);
                Point cB = WorldToScreen(wB);

                var ln = new System.Windows.Shapes.Line
                {
                    X1 = cA.X,
                    Y1 = cA.Y,
                    X2 = cB.X,
                    Y2 = cB.Y,
                    Stroke = gridBrush,
                    StrokeThickness = major ? thkMajor : thkMinor,
                    Opacity = major ? opacityMajor : opacityMinor,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };

                AddGridElement(ln);

                if (major)
                {
                    string txt = z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    AddGridText(
                        txt,
                        xCanvas: cA.X + 2 * invZoom,
                        yCanvas: visCanvas.Bottom - (16.0 * invZoom),
                        gridBrush: gridBrush,
                        fontSize: fontPx);
                }
            }


            // ---- horizontal lines (constant X) across screen height ----
            int ix = 0;
            for (int k = kX0; k <= kX1; k++)
            {
                double x = originX + k * step;
                bool major = (Math.Abs(k) % GRID_MAJOR_EVERY) == 0;

                Point wA = new Point(x, minZ);
                Point wB = new Point(x, maxZ);

                Point cA = WorldToScreen(wA);
                Point cB = WorldToScreen(wB);

                var ln = new System.Windows.Shapes.Line
                {
                    X1 = cA.X,
                    Y1 = cA.Y,
                    X2 = cB.X,
                    Y2 = cB.Y,
                    Stroke = gridBrush,
                    StrokeThickness = major ? thkMajor : thkMinor,
                    Opacity = major ? opacityMajor : opacityMinor,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };

                AddGridElement(ln);

                if (major)
                {
                    double dia = x * 2.0;
                    string txt = dia.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    AddGridText(
                        txt,
                        xCanvas: visCanvas.Right - (48.0 * invZoom),
                        yCanvas: cA.Y - (8.0 * invZoom),
                        gridBrush: gridBrush,
                        fontSize: fontPx);
                }
            }

        }

        private Rect GetVisibleWorldRect()
        {
            Rect visCanvas = GetVisibleCanvasRectInCanvasCoords();
            if (visCanvas.IsEmpty)
                return Rect.Empty;

            // Convert visible canvas rect corners into world
            Point w0 = ScreenToWorld(new Point(visCanvas.Left, visCanvas.Top));
            Point w1 = ScreenToWorld(new Point(visCanvas.Right, visCanvas.Bottom));

            double minX = Math.Min(w0.X, w1.X);
            double maxX = Math.Max(w0.X, w1.X);
            double minZ = Math.Min(w0.Y, w1.Y);
            double maxZ = Math.Max(w0.Y, w1.Y);

            double wX = Math.Max(1e-6, maxX - minX);
            double wZ = Math.Max(1e-6, maxZ - minZ);

            return new Rect(minX, minZ, wX, wZ);
        }

        private static double QuantizeNiceStep(double raw)
        {
            // Quantize to 1,2,5 * 10^n (stable jumps)
            if (!double.IsFinite(raw) || raw <= 0) return 1.0;

            double exp = Math.Floor(Math.Log10(raw));
            double pow = Math.Pow(10.0, exp);
            double m = raw / pow;

            double q;
            if (m <= 1.0) q = 1.0;
            else if (m <= 2.0) q = 2.0;
            else if (m <= 5.0) q = 5.0;
            else q = 10.0;

            double step = q * pow;

            // hard floor so we never go to 0
            if (step < 0.1) step = 0.1;
            return step;
        }

        private static Brush BrushFromHex(string? hex, Brush fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex))
                    return fallback;

                string t = hex.Trim();
                if (!t.StartsWith("#", StringComparison.Ordinal))
                    t = "#" + t;

                // allow "#RRGGBB"
                if (t.Length == 7)
                    t = "#FF" + t.Substring(1);

                var bc = new BrushConverter();
                var b = (Brush)bc.ConvertFromString(t);
                if (b != null && b.CanFreeze) b.Freeze();
                return b ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private void AddGridElement(UIElement el)
        {
            if (el is FrameworkElement fe)
                fe.Tag = TAG_GRID;

            // Insert at back so it stays BEHIND geometry/picks
            _editCanvas.Children.Insert(0, el);
        }

        private void AddGridText(string text, double xCanvas, double yCanvas, Brush gridBrush, double fontSize)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = gridBrush,
                Opacity = 0.85,
                FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Max(6.0, fontSize),
                Tag = TAG_GRID,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(tb, xCanvas);
            Canvas.SetTop(tb, yCanvas);

            // Insert at back as well (still readable over faint lines)
            _editCanvas.Children.Insert(0, tb);
        }
    }
}
