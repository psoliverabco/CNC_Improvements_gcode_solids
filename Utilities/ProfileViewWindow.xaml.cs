using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CNC_Improvements_gcode_solids.Utilities
{
    public partial class ProfileViewWindow : Window
    {
        // Zoom/pan transforms applied to the drawing canvas only
        private readonly ScaleTransform _scale = new ScaleTransform(1.0, 1.0);
        private readonly TranslateTransform _translate = new TranslateTransform();
        private readonly TransformGroup _transformGroup = new TransformGroup();

        // Diagnostics shown in UI (set by caller if wanted)
        private string _offsetType = "N/A";
        private double _noseRadius = double.NaN;
        private int _quadrant = -1;

        // Tag used to identify geometry we draw (so we can clear geometry without wiping overlays)
        private const string TAG_GEOM = "GEOM";

        /// <summary>
        /// One drawable segment in the profile viewer:
        /// a list of world points, with stroke brush and thickness.
        /// </summary>
        private sealed class StyledSegment
        {
            public List<Point> Points { get; } = new List<Point>();
            public Brush Stroke { get; set; } = GraphicsPalette.ProfileBrush;
            public double Thickness { get; set; } = 1.5;
        }

        /// <summary>
        /// One filled (shaded) closed region polygon in world coords.
        /// </summary>
        private sealed class FilledRegion
        {
            public List<Point> Polygon { get; } = new List<Point>();
            public Brush Fill { get; set; } = Brushes.Transparent;
        }

        // ------------------------------------------------------------
        // Remember last geometry so we can re-fit after layout/resize
        // ------------------------------------------------------------
        private List<StyledSegment>? _lastStyledSegments = null;
        private List<FilledRegion>? _lastFillRegions = null;
        private List<Point>? _lastWorldPolyline = null;

        private void ReRenderLast()
        {
            if (_lastStyledSegments != null)
                RenderWorldPolylines(_lastStyledSegments, _lastFillRegions);
            else if (_lastWorldPolyline != null)
                RenderWorldPolyline(_lastWorldPolyline);
        }




        private void ApplyLegendColors()
        {
            // Legend fills reflect the user palette
            try { Original.Fill = GraphicsPalette.ProfileBrush; }
            catch { Original.Fill = Brushes.Transparent; }

            try { Offset.Fill = GraphicsPalette.OffsetBrush; }
            catch { Offset.Fill = Brushes.Transparent; }

            try { Closing.Fill = GraphicsPalette.ClosingBrush; }
            catch { Closing.Fill = Brushes.Transparent; }

            // Optional: keep the legend visible on dark background
            Original.Stroke = Brushes.Black;
            Offset.Stroke = Brushes.Black;
            Closing.Stroke = Brushes.Black;
        }

        public ProfileViewWindow()
        {
            InitializeComponent();
            ApplyLegendColors();
            ApplyGraphicTextColor();











            _transformGroup.Children.Add(_scale);
            _transformGroup.Children.Add(_translate);

            ProfileCanvas.RenderTransform = _transformGroup;
            ProfileCanvas.RenderTransformOrigin = new Point(0, 0);

            // Refit once canvas has real size (and on window resize)
            ProfileCanvas.SizeChanged += (_, __) => ReRenderLast();

            UpdateInfoPanels();
        }

        /// <summary>
        /// Optional: set turning diagnostics for the viewer UI.
        /// You can call this before or after LoadProfile/LoadProfileScript.
        /// </summary>
        public void SetDiagnostics(string offsetType, double noseRadius, int quadrant)
        {
            _offsetType = string.IsNullOrWhiteSpace(offsetType) ? "N/A" : offsetType.Trim();
            _noseRadius = noseRadius;
            _quadrant = quadrant;
            UpdateInfoPanels();
        }

        private void UpdateInfoPanels()
        {
            var inv = CultureInfo.InvariantCulture;

            string nr = double.IsFinite(_noseRadius) ? _noseRadius.ToString("0.###", inv) : "N/A";
            string qd = (_quadrant >= 0) ? _quadrant.ToString(inv) : "N/A";

            TxtSummary.Text = $"OffsetType={_offsetType}  NoseRad={nr}  Quadrant={qd}";
            InfoText.Text =
                $"OFFSET\n" +
                $"Type={_offsetType}\n" +
                $"NoseRad={nr}\n" +
                $"Quadrant={qd}\n\n" +
                $"Zoom: Mouse wheel (cursor-centered)";


        }








        //==============================================
        private void ApplyGraphicTextColor()
        {
            try
            {
                var brush = GraphicsPalette.GraphicTextBrush;
                InfoText.Foreground = brush;

                TxtSummary.Foreground = brush;
                t111.Foreground = brush;
                t222.Foreground = brush;
                t333.Foreground = brush;
                Desc.Foreground = brush;





            }
            catch
            {
                // Safe fallback
                InfoText.Foreground = GraphicsPalette.GraphicTextBrush;

                TxtSummary.Foreground = GraphicsPalette.GraphicTextBrush;
                t111.Foreground = GraphicsPalette.GraphicTextBrush;
                t222.Foreground = GraphicsPalette.GraphicTextBrush;
                t333.Foreground = GraphicsPalette.GraphicTextBrush;
                Desc.Foreground = GraphicsPalette.GraphicTextBrush;

            }
        }

        /// <summary>
        /// Removes ONLY geometry elements we created (tagged TAG_GEOM),
        /// leaving any overlay UI intact even if it lives inside the Canvas.
        /// </summary>
        private void ClearGeometryOnly()
        {
            for (int i = ProfileCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (ProfileCanvas.Children[i] is FrameworkElement fe &&
                    fe.Tag is string tag &&
                    tag == TAG_GEOM)
                {
                    ProfileCanvas.Children.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Loads a list of profile lines like:
        ///   LINE x1 z1   x2 z2
        ///   ARC3_CCW x1 z1   xm zm   x2 z2
        ///   ARC3_CW  x1 z1   xm zm   x2 z2
        /// in radius-space (X=radius, Z=axis) and renders them into ProfileCanvas
        /// with auto-fit to extents.
        /// </summary>
        public void LoadProfile(List<string> profileLines)
        {
            if (profileLines == null || profileLines.Count == 0)
                return;

            var worldPoints = new List<Point>();

            foreach (string raw in profileLines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string line = raw.Trim();
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                    continue;

                string cmd = parts[0].ToUpperInvariant();

                if (cmd == "LINE" && parts.Length >= 5)
                {
                    double x1 = ParseDouble(parts[1]);
                    double z1 = ParseDouble(parts[2]);
                    double x2 = ParseDouble(parts[3]);
                    double z2 = ParseDouble(parts[4]);

                    AddSegmentPoints(worldPoints, new List<Point> { new Point(x1, z1), new Point(x2, z2) });
                }
                else if ((cmd == "ARC3_CCW" || cmd == "ARC3_CW") && parts.Length >= 7)
                {
                    double x1 = ParseDouble(parts[1]);
                    double z1 = ParseDouble(parts[2]);
                    double xm = ParseDouble(parts[3]);
                    double zm = ParseDouble(parts[4]);
                    double x2 = ParseDouble(parts[5]);
                    double z2 = ParseDouble(parts[6]);

                    var p1 = new Point(x1, z1);
                    var pm = new Point(xm, zm);
                    var p2 = new Point(x2, z2);

                    bool ccw = (cmd == "ARC3_CCW");
                    var arcPoints = ApproximateArcFrom3Points(p1, pm, p2, ccw, 48);
                    AddSegmentPoints(worldPoints, arcPoints);
                }
            }

            if (worldPoints.Count < 2)
                return;

            _lastWorldPolyline = worldPoints;
            _lastStyledSegments = null;
            _lastFillRegions = null;

            RenderWorldPolyline(worldPoints);
            UpdateInfoPanels();
        }

        // ============================================================
        // TRANSFORM DIRECTIVE SUPPORT (ViewAll)
        // ============================================================
        // Expected line format:
        //   @TRANSFORM MATRIX "<name>" ROTY <deg> TZ <value>
        //
        // Rule:
        // - Always apply TZ shift to Z (world Y axis in this viewer).
        // - Apply ROTY only when ~0 or ~180.
        // - ROTY 180 means mirror Z then shift:  z' = (-z) + tz
        // - ROTY 0 means shift only:           z' = z + tz
        // ============================================================

        private static readonly Regex _reTransform =
            new Regex(@"^\s*@TRANSFORM\s+MATRIX\s+""(?<name>[^""]*)""\s+ROTY\s+(?<roty>[-+]?\d+(?:\.\d+)?)\s+TZ\s+(?<tz>[-+]?\d+(?:\.\d+)?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool TryParseTransformDirective(string line, out string matrixName, out double rotYDeg, out double tz)
        {
            matrixName = "No Transformation";
            rotYDeg = 0.0;
            tz = 0.0;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            var m = _reTransform.Match(line);
            if (!m.Success)
                return false;

            matrixName = (m.Groups["name"]?.Value ?? "").Trim();
            if (matrixName.Length == 0)
                matrixName = "Matrix";

            if (!double.TryParse(m.Groups["roty"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rotYDeg))
                rotYDeg = 0.0;

            if (!double.TryParse(m.Groups["tz"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tz))
                tz = 0.0;

            return true;
        }

        private static double NormalizeDeg360(double deg)
        {
            double n = deg % 360.0;
            if (n < 0.0) n += 360.0;
            return n;
        }

        private static bool IsRotY180(double rotYDeg, double tolDeg = 0.5)
        {
            double n = NormalizeDeg360(rotYDeg);
            return Math.Abs(n - 180.0) <= tolDeg;
        }

        private static Point ApplyTransform(Point p, double rotYDeg, double tz)
        {
            // world: X = radius, Y = Z-axis
            bool flip = IsRotY180(rotYDeg);

            double z = p.Y;
            double z2 = flip ? (-z + tz) : (z + tz);

            return new Point(p.X, z2);
        }

        // ============================================================
        // VIEW-ALL REGION FILL SUPPORT (pattern you specified)
        //
        // Pattern:
        // - A region "profile" block starts when style color matches Settings:
        //     Settings.Default.ProfileColor OR Settings.Default.OffsetColor
        // - We accumulate geometry until the style color matches:
        //     Settings.Default.ClosingColor
        // - Then the EXACT NEXT 3 geometry lines are the closing edges.
        // - Fill = ClosingColor but with alpha scaled to 70% (AA * 0.7).
        // - Then next style line begins the next region, etc.
        // ============================================================

        private const double REGION_FILL_ALPHA_FACTOR = 0.7;
        private const double JOIN_EPS = 1e-3;

        private static string NormalizeHexColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            string t = s.Trim();

            if (!t.StartsWith("#", StringComparison.Ordinal))
                t = "#" + t;

            // Accept #RRGGBB -> #FFRRGGBB
            if (t.Length == 7)
                t = "#FF" + t.Substring(1);

            // Anything else: leave as-is; comparisons will fail safely
            return t.ToUpperInvariant();
        }

        private static bool IsStyleLine(string line)
            => !string.IsNullOrWhiteSpace(line) && line.StartsWith("(") && line.EndsWith(")");

        private static bool TryParseStyleLine(string line, out string hexColorNorm, out double thickness)
        {
            hexColorNorm = "";
            thickness = 1.5;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            string t = line.Trim();
            if (!t.StartsWith("(") || !t.EndsWith(")"))
                return false;

            string inner = t.Substring(1, t.Length - 2);
            var parts = inner.Split(',');

            if (parts.Length < 1)
                return false;

            hexColorNorm = NormalizeHexColor(parts[0].Trim());

            if (parts.Length >= 2)
            {
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out thickness))
                    thickness = 1.5;
            }

            return !string.IsNullOrWhiteSpace(hexColorNorm);
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

        private static Brush AlphaScaledBrushFromHex(string hexNorm, double alphaFactor, Brush fallback)
        {
            try
            {
                string h = NormalizeHexColor(hexNorm);
                if (h.Length != 9 || !h.StartsWith("#", StringComparison.Ordinal))
                    return fallback;

                byte a = byte.Parse(h.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte r = byte.Parse(h.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(h.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(h.Substring(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                int a2 = (int)Math.Round(a * alphaFactor);
                if (a2 < 0) a2 = 0;
                if (a2 > 255) a2 = 255;

                var scb = new SolidColorBrush(Color.FromArgb((byte)a2, r, g, b));
                scb.Freeze();
                return scb;
            }
            catch
            {
                return fallback;
            }
        }

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool AreClose(Point a, Point b, double eps = 1e-6)
            => Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) < eps;



        private static bool IsClosedPath(List<Point> pts, double eps = 1e-6)
        {
            if (pts == null || pts.Count < 3)
                return false;

            return AreClose(pts[0], pts[pts.Count - 1], eps);
        }



        private static IEnumerable<int[]> Permute3()
        {
            // 3! = 6 permutations
            yield return new[] { 0, 1, 2 };
            yield return new[] { 0, 2, 1 };
            yield return new[] { 1, 0, 2 };
            yield return new[] { 1, 2, 0 };
            yield return new[] { 2, 0, 1 };
            yield return new[] { 2, 1, 0 };
        }

        private static bool TryOrderClosingSegments(
            List<List<Point>> closeSegs,
            Point profileStart,
            Point profileEnd,
            out List<Point> orderedClosePoints)
        {
            orderedClosePoints = new List<Point>();

            if (closeSegs == null || closeSegs.Count != 3)
                return false;

            // Score all 6 * 8 = 48 combinations (perm + flip mask)
            double bestScore = double.PositiveInfinity;
            List<Point>? best = null;

            foreach (var perm in Permute3())
            {
                for (int mask = 0; mask < 8; mask++)
                {
                    var s0 = closeSegs[perm[0]];
                    var s1 = closeSegs[perm[1]];
                    var s2 = closeSegs[perm[2]];

                    if (s0.Count < 2 || s1.Count < 2 || s2.Count < 2)
                        continue;

                    var a0 = ((mask & 1) != 0) ? s0.AsEnumerable().Reverse().ToList() : s0;
                    var a1 = ((mask & 2) != 0) ? s1.AsEnumerable().Reverse().ToList() : s1;
                    var a2 = ((mask & 4) != 0) ? s2.AsEnumerable().Reverse().ToList() : s2;

                    Point a0s = a0[0], a0e = a0[^1];
                    Point a1s = a1[0], a1e = a1[^1];
                    Point a2s = a2[0], a2e = a2[^1];

                    double g0 = Dist(profileEnd, a0s);
                    double g1 = Dist(a0e, a1s);
                    double g2 = Dist(a1e, a2s);
                    double g3 = Dist(a2e, profileStart);

                    double score = g0 + g1 + g2 + g3;

                    if (score < bestScore)
                    {
                        // Build full close polyline
                        var tmp = new List<Point>();
                        AddSegmentPoints(tmp, a0);
                        AddSegmentPoints(tmp, a1);
                        AddSegmentPoints(tmp, a2);

                        bestScore = score;
                        best = tmp;
                    }
                }
            }

            if (best == null)
                return false;

            // Require we are "close enough" (your data should be near exact)
            if (bestScore > JOIN_EPS * 10.0) // allow a tiny numeric slop
                return false;

            orderedClosePoints = best;
            return true;
        }

        /// <summary>
        /// Loads a full "display script" of the form:
        ///   (#AARRGGBB,width)
        ///   LINE ...
        ///   ARC3_CCW ...
        ///   ARC3_CW  ...
        /// plus optional transform directives:
        ///   @TRANSFORM MATRIX "..." ROTY <deg> TZ <value>
        /// Transform applies to all subsequent geometry until next directive.
        ///
        /// ALSO builds shaded closed regions using your fixed pattern:
        /// ProfileColor/OffsetColor block -> ClosingColor -> next 3 geometry lines (closing edges)
        /// </summary>
        public void LoadProfileScript(string scriptText)
        {
            if (string.IsNullOrWhiteSpace(scriptText))
                return;

            ClearGeometryOnly();

            // Settings colors to match against
            string profileHex = NormalizeHexColor(Settings.Default.ProfileColor);
            string offsetHex = NormalizeHexColor(Settings.Default.OffsetColor);
            string closingHex = NormalizeHexColor(Settings.Default.ClosingColor);

            Brush currentBrush = Brushes.Lime;
            double currentThickness = 1.5;
            string currentStyleHex = "";

            // Current transform context
            double curRotY = 0.0;
            double curTz = 0.0;
            string curMatrixName = "No Transformation";

            var segments = new List<StyledSegment>();
            var fills = new List<FilledRegion>();

            // ------------------------------------------------------------
            // Region fill rule (REQUIRED FIX):
            // We must NOT "append points in script order" because segment order
            // can be non-walking and causes self-intersection -> holes (EvenOdd).
            //
            // Instead:
            // - collect each geometry segment polyline for the region
            // - at finalize, CHAIN them by endpoint matching into one loop
            // - fill that ordered loop
            // ------------------------------------------------------------
            const double JOIN_EPS = 1e-3;

            bool regionOpen = false;
            var regionSegs = new List<List<Point>>(); // each entry is a segment polyline (already transformed)

            static List<Point> CopyList(List<Point> pts)
            {
                var r = new List<Point>(pts.Count);
                for (int i = 0; i < pts.Count; i++) r.Add(pts[i]);
                return r;
            }

            static bool Close(Point a, Point b, double eps)
                => Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps;

            List<Point> TryBuildLoopFromStart(List<List<Point>> segs, int startIdx, out bool closed, out int usedCount)
            {
                closed = false;
                usedCount = 0;

                if (segs == null || segs.Count == 0)
                    return new List<Point>();

                // working list
                var remaining = new List<List<Point>>(segs.Count);
                for (int i = 0; i < segs.Count; i++)
                    remaining.Add(segs[i]);

                // pick start
                var startSeg = remaining[startIdx];
                remaining.RemoveAt(startIdx);

                var loop = new List<Point>();
                AddSegmentPoints(loop, startSeg);
                usedCount++;

                // chain
                while (remaining.Count > 0)
                {
                    if (loop.Count >= 4 && Close(loop[0], loop[^1], JOIN_EPS))
                    {
                        closed = true;
                        break;
                    }

                    Point tail = loop[^1];

                    int bestIdx = -1;
                    bool bestReverse = false;
                    double bestDist = double.PositiveInfinity;

                    for (int i = 0; i < remaining.Count; i++)
                    {
                        var s = remaining[i];
                        if (s == null || s.Count < 2)
                            continue;

                        Point a = s[0];
                        Point b = s[^1];

                        double da = Math.Sqrt((tail.X - a.X) * (tail.X - a.X) + (tail.Y - a.Y) * (tail.Y - a.Y));
                        double db = Math.Sqrt((tail.X - b.X) * (tail.X - b.X) + (tail.Y - b.Y) * (tail.Y - b.Y));

                        if (da < bestDist)
                        {
                            bestDist = da;
                            bestIdx = i;
                            bestReverse = false;
                        }
                        if (db < bestDist)
                        {
                            bestDist = db;
                            bestIdx = i;
                            bestReverse = true;
                        }
                    }

                    if (bestIdx < 0 || bestDist > JOIN_EPS * 10.0)
                    {
                        // no connectable next segment
                        break;
                    }

                    var next = remaining[bestIdx];
                    remaining.RemoveAt(bestIdx);

                    if (bestReverse)
                    {
                        var rev = new List<Point>(next.Count);
                        for (int k = next.Count - 1; k >= 0; k--)
                            rev.Add(next[k]);
                        next = rev;
                    }

                    AddSegmentPoints(loop, next);
                    usedCount++;
                }

                // final closure check
                if (loop.Count >= 4 && Close(loop[0], loop[^1], JOIN_EPS))
                    closed = true;

                return loop;
            }

            void FinalizeRegionIfAny()
            {
                if (!regionOpen)
                    return;

                // Build ONE ordered loop from the collected region segments
                List<Point> bestLoop = new List<Point>();
                bool bestClosed = false;
                int bestUsed = -1;

                if (regionSegs.Count > 0)
                {
                    // Try multiple starts to find a properly closed chain using most segments
                    int tries = Math.Min(regionSegs.Count, 50);
                    for (int s = 0; s < tries; s++)
                    {
                        var loop = TryBuildLoopFromStart(regionSegs, s, out bool closed, out int used);
                        if (used > bestUsed || (used == bestUsed && closed && !bestClosed))
                        {
                            bestLoop = loop;
                            bestClosed = closed;
                            bestUsed = used;
                        }

                        // early exit: perfect case uses all segments and is closed
                        if (closed && used == regionSegs.Count)
                            break;
                    }
                }

                // If upstream guarantees closed but chain isn't perfect due to tiny gaps,
                // we still force-close if endpoints are very near.
                if (bestLoop.Count >= 3)
                {
                    if (!Close(bestLoop[0], bestLoop[^1], JOIN_EPS))
                        bestLoop.Add(bestLoop[0]);

                    if (bestLoop.Count >= 4)
                    {
                        var fr = new FilledRegion
                        {
                            Fill = AlphaScaledBrushFromHex(closingHex, REGION_FILL_ALPHA_FACTOR, Brushes.Transparent)
                        };
                        fr.Polygon.AddRange(bestLoop);
                        fills.Add(fr);
                    }
                }

                regionOpen = false;
                regionSegs.Clear();
            }

            var lines = scriptText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw == null) continue;

                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                // Transform directive
                if (line.StartsWith("@TRANSFORM", StringComparison.OrdinalIgnoreCase))
                {
                    // Transform boundary: close out current region
                    FinalizeRegionIfAny();

                    if (TryParseTransformDirective(line, out string mn, out double ry, out double tz))
                    {
                        curMatrixName = mn;
                        curRotY = ry;
                        curTz = tz;
                    }
                    else
                    {
                        curMatrixName = "No Transformation";
                        curRotY = 0.0;
                        curTz = 0.0;
                    }

                    continue;
                }

                // Ignore comments
                if (line.StartsWith(";", StringComparison.Ordinal))
                    continue;

                // Style line: "(#AARRGGBB,width)"
                if (IsStyleLine(line))
                {
                    if (TryParseStyleLine(line, out string styleHex, out double thick))
                    {
                        currentStyleHex = styleHex;
                        currentThickness = thick;
                        currentBrush = BrushFromHex(styleHex, Brushes.Lime);

                        bool isRegionStart =
                            !string.IsNullOrWhiteSpace(styleHex) &&
                            (styleHex == profileHex || styleHex == offsetHex);

                        if (isRegionStart)
                        {
                            // new region begins -> finalize previous
                            FinalizeRegionIfAny();

                            regionOpen = true;
                            regionSegs.Clear();
                        }

                        // NOTE: ClosingColor does NOT end region.
                        // We fill every region based on the chained loop.
                    }

                    continue;
                }

                // Geometry line
                var segPoints = ParseGeometryLineToWorldPoints(line, curRotY, curTz);
                if (segPoints.Count >= 2)
                {
                    // Draw stroke segment (keep exact script color & thickness)
                    var seg = new StyledSegment
                    {
                        Stroke = currentBrush,
                        Thickness = currentThickness
                    };
                    seg.Points.AddRange(segPoints);
                    segments.Add(seg);

                    // Collect for region fill (as independent segment polylines)
                    if (regionOpen)
                        regionSegs.Add(CopyList(segPoints));
                }
            }

            // end-of-script -> finalize last region
            FinalizeRegionIfAny();

            if (segments.Count == 0)
                return;

            _lastStyledSegments = segments;
            _lastFillRegions = (fills.Count > 0) ? fills : null;
            _lastWorldPolyline = null;

            RenderWorldPolylines(segments, _lastFillRegions);
            UpdateInfoPanels();
        }



        private List<Point> ParseGeometryLineToWorldPoints(string line, double rotYDeg, double tz)
        {
            var result = new List<Point>();

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                return result;

            string cmd = parts[0].ToUpperInvariant();

            if (cmd == "LINE" && parts.Length >= 5)
            {
                double x1 = ParseDouble(parts[1]);
                double z1 = ParseDouble(parts[2]);
                double x2 = ParseDouble(parts[3]);
                double z2 = ParseDouble(parts[4]);

                var p1 = ApplyTransform(new Point(x1, z1), rotYDeg, tz);
                var p2 = ApplyTransform(new Point(x2, z2), rotYDeg, tz);

                result.Add(p1);
                result.Add(p2);
            }
            else if ((cmd == "ARC3_CCW" || cmd == "ARC3_CW") && parts.Length >= 7)
            {
                double x1 = ParseDouble(parts[1]);
                double z1 = ParseDouble(parts[2]);
                double xm = ParseDouble(parts[3]);
                double zm = ParseDouble(parts[4]);
                double x2 = ParseDouble(parts[5]);
                double z2 = ParseDouble(parts[6]);

                var p1 = ApplyTransform(new Point(x1, z1), rotYDeg, tz);
                var pm = ApplyTransform(new Point(xm, zm), rotYDeg, tz);
                var p2 = ApplyTransform(new Point(x2, z2), rotYDeg, tz);

                bool ccw = (cmd == "ARC3_CCW");
                result.AddRange(ApproximateArcFrom3Points(p1, pm, p2, ccw, 48));
            }

            return result;
        }

        private void RenderWorldPolylines(List<StyledSegment> segments, List<FilledRegion>? fills)
        {
            var allPoints = new List<Point>();
            for (int i = 0; i < segments.Count; i++)
                allPoints.AddRange(segments[i].Points);

            if (fills != null)
            {
                for (int i = 0; i < fills.Count; i++)
                    allPoints.AddRange(fills[i].Polygon);
            }

            if (allPoints.Count < 2)
                return;

            _scale.ScaleX = 1.0;
            _scale.ScaleY = 1.0;
            _translate.X = 0.0;
            _translate.Y = 0.0;

            double minX = allPoints.Min(p => p.X);
            double maxX = allPoints.Max(p => p.X);
            double minZ = allPoints.Min(p => p.Y);
            double maxZ = allPoints.Max(p => p.Y);

            double rangeX = maxX - minX; if (rangeX <= 0) rangeX = 1;
            double rangeZ = maxZ - minZ; if (rangeZ <= 0) rangeZ = 1;

            double canvasW = ProfileCanvas.ActualWidth;
            double canvasH = ProfileCanvas.ActualHeight;

            if (canvasW < 10 || canvasH < 10)
            {
                Dispatcher.BeginInvoke(new Action(ReRenderLast), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            double padding = 30.0;
            double availW = canvasW - 2.0 * padding; if (availW < 1) availW = 1;
            double availH = canvasH - 2.0 * padding; if (availH < 1) availH = 1;

            double scaleX = availW / rangeZ; // horizontal uses Z range
            double scaleY = availH / rangeX; // vertical uses X range
            double scale = Math.Min(scaleX, scaleY);

            scale *= 0.97;

            double contentW = rangeZ * scale;
            double contentH = rangeX * scale;

            double baseX = (canvasW - contentW) / 2.0;
            double baseY = (canvasH - contentH) / 2.0;

            ClearGeometryOnly();

            // 1) Draw fills first (behind strokes)
            if (fills != null)
            {
                for (int i = 0; i < fills.Count; i++)
                {
                    var fr = fills[i];
                    if (fr.Polygon == null || fr.Polygon.Count < 4)
                        continue;

                    var pg = new Polygon
                    {
                        Fill = fr.Fill ?? Brushes.Transparent,
                        Stroke = Brushes.Transparent,
                        StrokeThickness = 0.0,
                        Tag = TAG_GEOM
                    };

                    for (int p = 0; p < fr.Polygon.Count; p++)
                    {
                        var wp = fr.Polygon[p];
                        double sx = (wp.Y - minZ) * scale + baseX;      // screen X = world Z
                        double sy = (maxX - wp.X) * scale + baseY;      // screen Y = inverted world X
                        pg.Points.Add(new Point(sx, sy));
                    }

                    ProfileCanvas.Children.Add(pg);
                }
            }

            // 2) Draw strokes
            foreach (var seg in segments)
            {
                if (seg.Points.Count < 2)
                    continue;

                var poly = new Polyline
                {
                    Stroke = seg.Stroke ?? Brushes.Lime,
                    StrokeThickness = (seg.Thickness > 0) ? seg.Thickness : 1.0,
                    Tag = TAG_GEOM
                };

                foreach (var wp in seg.Points)
                {
                    double sx = (wp.Y - minZ) * scale + baseX;
                    double sy = (maxX - wp.X) * scale + baseY;
                    poly.Points.Add(new Point(sx, sy));
                }

                ProfileCanvas.Children.Add(poly);
            }
        }

        private void RenderWorldPolyline(List<Point> worldPoints)
        {
            if (worldPoints == null || worldPoints.Count < 2)
                return;

            _scale.ScaleX = 1.0;
            _scale.ScaleY = 1.0;
            _translate.X = 0.0;
            _translate.Y = 0.0;

            double minX = worldPoints.Min(p => p.X);
            double maxX = worldPoints.Max(p => p.X);
            double minZ = worldPoints.Min(p => p.Y);
            double maxZ = worldPoints.Max(p => p.Y);

            double rangeX = maxX - minX; if (rangeX <= 0) rangeX = 1;
            double rangeZ = maxZ - minZ; if (rangeZ <= 0) rangeZ = 1;

            double canvasW = ProfileCanvas.ActualWidth;
            double canvasH = ProfileCanvas.ActualHeight;

            if (canvasW < 10 || canvasH < 10)
            {
                Dispatcher.BeginInvoke(new Action(ReRenderLast), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            double padding = 30.0;
            double availW = canvasW - 2.0 * padding; if (availW < 1) availW = 1;
            double availH = canvasH - 2.0 * padding; if (availH < 1) availH = 1;

            double scaleX = availW / rangeZ;
            double scaleY = availH / rangeX;
            double scale = Math.Min(scaleX, scaleY);

            scale *= 0.97;

            double contentW = rangeZ * scale;
            double contentH = rangeX * scale;

            double baseX = (canvasW - contentW) / 2.0;
            double baseY = (canvasH - contentH) / 2.0;

            ClearGeometryOnly();

            var poly = new Polyline
            {
                Stroke = Brushes.Lime,
                StrokeThickness = 1.5,
                Tag = TAG_GEOM
            };

            foreach (var wp in worldPoints)
            {
                double sx = (wp.Y - minZ) * scale + baseX;
                double sy = (maxX - wp.X) * scale + baseY;
                poly.Points.Add(new Point(sx, sy));
            }

            ProfileCanvas.Children.Add(poly);
        }

        private static double ParseDouble(string s)
            => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static void AddSegmentPoints(List<Point> worldPoints, IList<Point> segmentPoints)
        {
            if (segmentPoints == null || segmentPoints.Count == 0)
                return;

            for (int i = 0; i < segmentPoints.Count; i++)
            {
                var p = segmentPoints[i];

                if (worldPoints.Count > 0 && i == 0)
                {
                    var last = worldPoints[^1];
                    if (AreClose(last, p))
                        continue;
                }

                worldPoints.Add(p);
            }
        }

        private static List<Point> ApproximateArcFrom3Points(Point p1, Point pm, Point p2, bool _ccwIgnored, int segments)
        {
            var result = new List<Point>();

            if (!TryComputeCircleCenter(p1, pm, p2, out Point c))
            {
                result.Add(p1);
                result.Add(p2);
                return result;
            }

            double cx = c.X;
            double cy = c.Y;

            double r = Math.Sqrt((p1.X - cx) * (p1.X - cx) + (p1.Y - cy) * (p1.Y - cy));
            if (r < 1e-9)
            {
                result.Add(p1);
                result.Add(p2);
                return result;
            }

            double a1 = Math.Atan2(p1.Y - cy, p1.X - cx);
            double a2 = Math.Atan2(p2.Y - cy, p2.X - cx);
            double am = Math.Atan2(pm.Y - cy, pm.X - cx);

            static double Norm2Pi(double a)
            {
                double twoPi = 2.0 * Math.PI;
                while (a < 0.0) a += twoPi;
                while (a >= twoPi) a -= twoPi;
                return a;
            }

            double a1n = Norm2Pi(a1);
            double a2n = Norm2Pi(a2);
            double amn = Norm2Pi(am);

            double deltaCcw = a2n - a1n;
            if (deltaCcw < 0.0) deltaCcw += 2.0 * Math.PI;

            double tMid = amn - a1n;
            if (tMid < 0.0) tMid += 2.0 * Math.PI;

            double delta;
            const double eps = 1e-6;

            if (tMid <= deltaCcw + eps)
                delta = deltaCcw;
            else
                delta = deltaCcw - 2.0 * Math.PI;

            result.Add(p1);

            for (int i = 1; i < segments; i++)
            {
                double t = i / (double)segments;
                double ang = a1n + t * delta;
                double x = cx + r * Math.Cos(ang);
                double y = cy + r * Math.Sin(ang);
                result.Add(new Point(x, y));
            }

            result.Add(p2);
            return result;
        }

        private static bool TryComputeCircleCenter(Point p1, Point p2, Point p3, out Point center)
        {
            double x1 = p1.X, y1 = p1.Y;
            double x2 = p2.X, y2 = p2.Y;
            double x3 = p3.X, y3 = p3.Y;

            double a11 = 2.0 * (x2 - x1);
            double a12 = 2.0 * (y2 - y1);
            double a21 = 2.0 * (x3 - x1);
            double a22 = 2.0 * (y3 - y1);

            double det = a11 * a22 - a12 * a21;
            if (Math.Abs(det) < 1e-9)
            {
                center = new Point();
                return false;
            }

            double b1 = x2 * x2 + y2 * y2 - x1 * x1 - y1 * y1;
            double b2 = x3 * x3 + y3 * y3 - x1 * x1 - y1 * y1;

            double cx = (b1 * a22 - b2 * a12) / det;
            double cy = (a11 * b2 - a21 * b1) / det;

            center = new Point(cx, cy);
            return true;
        }

        private void BtnFit_down_click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                _scale.ScaleX = 1.0;
                _scale.ScaleY = 1.0;
                _translate.X = 0.0;
                _translate.Y = 0.0;

                ReRenderLast();
                e.Handled = true;
            }
            catch
            {
                // swallow - fit should never crash the app
            }
        }

        private void ProfileCanvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ProfileCanvas.Focus();
        }

        private void ProfileCanvas_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            const double zoomStep = 1.2;
            double factor = (e.Delta > 0) ? zoomStep : (1.0 / zoomStep);

            double newScaleX = _scale.ScaleX * factor;
            double newScaleY = _scale.ScaleY * factor;

            const double minScale = 0.1;
            const double maxScale = 50.0;

            if (newScaleX < minScale || newScaleX > maxScale)
                return;

            Point p = e.GetPosition(ProfileCanvas);

            double oldScaleX = _scale.ScaleX;
            double oldScaleY = _scale.ScaleY;

            _scale.ScaleX = newScaleX;
            _scale.ScaleY = newScaleY;

            _translate.X = (oldScaleX - newScaleX) * p.X + _translate.X;
            _translate.Y = (oldScaleY - newScaleY) * p.Y + _translate.Y;

            e.Handled = true;
        }
    }
}
