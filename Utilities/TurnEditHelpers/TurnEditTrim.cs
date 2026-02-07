// File: Utilities/TurnEditHelpers/TurnEditTrim.cs
using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Utilities.TurnEditHelpers
{
    /// <summary>
    /// Trim tool (A=trim element, B=target).
    ///
    /// New rules implemented:
    /// - Intersections: treat arcs as FULL circles and lines as INFINITE for candidate generation.
    /// - Enumerate + cycle TRIM OPTIONS (outcomes), not raw IPs.
    /// - Internal arc handling: we always OUTPUT ARC3_CW and build outcomes for both branches.
    ///   We do NOT attempt to preserve "move start/end" meaning for arcs; caller/editor doesn't care.
    ///
    /// Preview:
    /// - If host supports polyline preview (IHostPolyline), we draw the preview segment.
    /// - Otherwise we fall back to drawing only points (IP + endpoints).
    /// </summary>
    internal static class TurnEditTrim
    {
        // ---------------- Host contracts ----------------


        // Single source of truth tolerance for ALL trim geometry decisions.
        // Turn editor should be stricter than FreeCAD sew tolerance.
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




        internal interface IHost
        {
            bool MapValid { get; }
            void ClearPreviewOnly();
            void DrawPreviewPointWorld(Point worldPt, Brush fill, double diamPx, double opacity);
        }

        /// <summary>
        /// Optional host extension: if implemented, we can preview the trimmed segment as a polyline.
        /// TurnEditRender already has DrawPreviewPolylineWorld, so your TrimHost can implement this easily.
        /// </summary>
        internal interface IHostPolyline : IHost
        {
            void DrawPreviewPolylineWorld(IReadOnlyList<Point> worldPts, Brush stroke, double thickness, double opacity);
        }

        // ---------------- Inputs ----------------

        internal enum SegKind { Line, Arc }

        // World coords: Point.X = X, Point.Y = Z
        internal sealed class SegView
        {
            public SegKind Kind;
            public Point A;
            public Point B;

            // Arc-only
            public Point M;
            public Point C;
            public bool CCW;

            public double Radius => (Kind == SegKind.Arc) ? TurnEditMath.Dist(A, C) : 0.0;
        }

        internal sealed class Pick
        {
            public int SegIndex;
            public SegView Seg = null!;
            public bool PickStart;        // true => picked Start, false => picked End
            public Point PickEndWorld;    // endpoint chosen in world (not required for new logic, but kept)
        }

        // ---------------- Output ----------------

        private sealed class TrimOption
        {
            public string Label = "";
            public Point IP;

            // Replacement region-input line returned to caller
            public string ReplacementLine = "";

            // Preview geometry (world points); may be empty if host can't draw polylines
            public List<Point> PreviewWorld = new List<Point>();

            // For dialog text
            public Point PrevA;
            public Point PrevB;
        }

        // ============================================================
        // Public entry
        // ============================================================

        /// <summary>
        /// Runs Trim tool (A=trim element, B=target). Returns:
        /// - replaceIndex: which seg in TurnEditWindow to replace (A's index)
        /// - replacementLine: region-input LINE/ARC3_CW describing the updated A segment
        /// </summary>
        public static bool Run(
    Pick aPick,
    Pick bPick,
    IHost host,
    out int replaceIndex,
    out string? replacementLine,
    out string statusText)
        {
            replaceIndex = -1;
            replacementLine = null;
            statusText = "";

            if (host == null) throw new ArgumentNullException(nameof(host));
            if (aPick == null || bPick == null || aPick.Seg == null || bPick.Seg == null)
            {
                statusText = "Trim: invalid selection.";
                return false;
            }

            if (!host.MapValid)
            {
                statusText = "Trim: view not ready.";
                return false;
            }

            // A is the segment we replace
            int replaceIdxLocal = aPick.SegIndex;

            // Build IP candidates (infinite primitives). If there is no true intersection,
            // we replace the TARGET (B) with a deterministic helper LINE and intersect against that.
            // This makes trim deterministic even when geometry is separated by any distance.
            List<Point> ips = BuildIntersectionCandidates_Infinite(aPick.Seg, bPick.Seg);
            if (ips.Count == 0)
            {
                if (TryBuildHelperTargetLine(aPick.Seg, bPick.Seg, out SegView helperLine, out _))
                {
                    ips = BuildIntersectionCandidates_Infinite(aPick.Seg, helperLine);
                }

                if (ips.Count == 0)
                {
                    statusText = "Trim: no intersections found.";
                    return false;
                }
            }

            // Enumerate trim outcomes (cycle these, not raw IPs)
            List<TrimOption> options = BuildTrimOptions(aPick, ips);
            if (options.Count == 0)
            {
                statusText = "Trim: no valid trim outcomes.";
                return false;
            }

            // Dialog
            bool keepAccepted = ShowTrimOptionsDialog(host, options, aPick, out int chosenIndex);
            host.ClearPreviewOnly();

            if (!keepAccepted || chosenIndex < 0 || chosenIndex >= options.Count)
            {
                statusText = "Trim: cancelled.";
                return false;
            }

            // Return selected option
            replaceIndex = replaceIdxLocal;
            replacementLine = options[chosenIndex].ReplacementLine;
            statusText = "Trim: replacement segment returned (applied by caller).";
            return true;
        }

        private static bool TryBuildHelperTargetLine(SegView a, SegView b, out SegView helperLine, out string why)
        {
            helperLine = new SegView();
            why = "";

            // LINE–ARC (either ordering)
            if ((a.Kind == SegKind.Line && b.Kind == SegKind.Arc) || (a.Kind == SegKind.Arc && b.Kind == SegKind.Line))
            {
                SegView line = (a.Kind == SegKind.Line) ? a : b;
                SegView arc = (a.Kind == SegKind.Arc) ? a : b;

                Vector v = line.B - line.A;
                double vv = v.X * v.X + v.Y * v.Y;
                if (!TurnEditMath.IsFinite(vv) || vv < 1e-18)
                    return false;

                // Normal to line
                Vector n = new Vector(-v.Y, v.X);
                double nn = Math.Sqrt(n.X * n.X + n.Y * n.Y);
                if (!TurnEditMath.IsFinite(nn) || nn < 1e-18)
                    return false;

                n /= nn;

                // Any 2 distinct points define an infinite line for intersection generation.
                // Use unit-length offsets about the arc center.
                Point c = arc.C;
                Point p0 = new Point(c.X - n.X, c.Y - n.Y);
                Point p1 = new Point(c.X + n.X, c.Y + n.Y);

                helperLine = new SegView
                {
                    Kind = SegKind.Line,
                    A = p0,
                    B = p1
                };

                why = "LINE–ARC miss: helper = (arc center) + normal-to-line";
                return true;
            }

            // ARC–ARC
            if (a.Kind == SegKind.Arc && b.Kind == SegKind.Arc)
            {
                Point c1 = a.C;
                Point c2 = b.C;
                double d = TurnEditMath.Dist(c1, c2);
                if (!TurnEditMath.IsFinite(d) || d < 1e-18)
                    return false;

                helperLine = new SegView
                {
                    Kind = SegKind.Line,
                    A = c1,
                    B = c2
                };

                why = "ARC–ARC miss: helper = line between centers";
                return true;
            }

            return false;
        }


        private static bool TryBuildSnapCandidate(Pick aPick, Pick bPick, double tol, out Point snapWorld)
        {
            snapWorld = new Point(double.NaN, double.NaN);

            if (aPick == null || bPick == null || aPick.Seg == null || bPick.Seg == null)
                return false;

            // Only snap based on the endpoint the user actually picked on A
            Point movingEnd = aPick.PickStart ? aPick.Seg.A : aPick.Seg.B;

            // Snap moving endpoint to closest point on B (finite line segment or full circle)
            if (bPick.Seg.Kind == SegKind.Line)
            {
                Point q = ClosestPointOnSegment(movingEnd, bPick.Seg.A, bPick.Seg.B);
                if (TurnEditMath.Dist(movingEnd, q) <= tol)
                {
                    snapWorld = q;
                    return true;
                }
                return false;
            }

            if (bPick.Seg.Kind == SegKind.Arc)
            {
                // Treat arc as full circle (consistent with your current candidate rules)
                Point C = bPick.Seg.C;
                double r = bPick.Seg.Radius;
                if (!TurnEditMath.IsFinite(r) || r <= 1e-12)
                    return false;

                Point q = ClosestPointOnCircle(movingEnd, C, r);

                // radial distance from point to circle surface
                double dr = Math.Abs(TurnEditMath.Dist(movingEnd, C) - r);
                if (dr <= tol)
                {
                    snapWorld = q;
                    return true;
                }
                return false;
            }

            return false;
        }

        private static Point ClosestPointOnSegment(Point p, Point a, Point b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double vv = vx * vx + vy * vy;
            if (vv <= 1e-24)
                return a;

            double t = (wx * vx + wy * vy) / vv;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;

            return new Point(a.X + vx * t, a.Y + vy * t);
        }

        private static Point ClosestPointOnCircle(Point p, Point c, double r)
        {
            double dx = p.X - c.X;
            double dy = p.Y - c.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);

            if (!TurnEditMath.IsFinite(d) || d <= 1e-24)
            {
                // arbitrary direction if point is at center
                return new Point(c.X + r, c.Y);
            }

            double inv = r / d;
            return new Point(c.X + dx * inv, c.Y + dy * inv);
        }

        private static double PointLineDistance(Point p, Point a, Point b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;

            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double vv = vx * vx + vy * vy;
            if (vv <= 1e-24)
                return TurnEditMath.Dist(p, a);

            // area magnitude / base length
            double cross = Math.Abs(vx * wy - vy * wx);
            double len = Math.Sqrt(vv);
            return cross / len;
        }


        // ============================================================
        // Option enumeration
        // ============================================================

        private static List<TrimOption> BuildTrimOptions(Pick aPick, List<Point> ips)
        {
            var a = aPick.Seg;
            var outOpts = new List<TrimOption>();

            if (a.Kind == SegKind.Line)
            {
                BuildLineTrimOptions(aPick, ips, outOpts);
                return outOpts;
            }

            if (a.Kind == SegKind.Arc)
            {
                BuildArcTrimOptions(aPick, ips, outOpts);
                return outOpts;
            }

            return outOpts;
        }

        // ---------- LINE options ----------

        private static void BuildLineTrimOptions(Pick aPick, List<Point> ips, List<TrimOption> outOpts)
        {
            var seg = aPick.Seg;
            Point A = seg.A;
            Point B = seg.B;

            double sew = CNC_Improvements_gcode_solids.Properties.Settings.Default.SewTol;
            if (!TurnEditMath.IsFinite(sew) || sew <= 0.0) sew = 0.001;
            double editTol = Math.Max(1e-12, sew * 0.5);

            // Use ONE tolerance everywhere (no mixed eps)
            double colTol = editTol;
            double betweenTol = Math.Max(1e-12, editTol * 0.01);

            for (int i = 0; i < ips.Count; i++)
            {
                Point ip = ips[i];

                bool onSegmentInterior = IsPointOnSegmentInterior(A, B, ip, colTol, betweenTol);

                if (onSegmentInterior)
                {
                    // Split -> 2 outcomes (keep either half)
                    outOpts.Add(MakeLineOption(
                        label: $"LINE split @IP{i + 1}: keep Start→IP",
                        ip: ip,
                        p0: A,
                        p1: ip));

                    outOpts.Add(MakeLineOption(
                        label: $"LINE split @IP{i + 1}: keep IP→End",
                        ip: ip,
                        p0: ip,
                        p1: B));

                    continue;
                }

                // NEW: if IP is NOT collinear with the original line, allow a SNAP outcome.
                // This is what handles "near miss" (e.g. line 0.0005 away from arc): we move the picked end
                // directly to the closest point on the target (within EditTol), even though it's not on the ray.
                bool ipIsCollinear = (PointLineDistance(ip, A, B) <= colTol);
                if (!ipIsCollinear)
                {
                    bool moveStart = aPick.PickStart;
                    Point p0 = moveStart ? ip : A;
                    Point p1 = moveStart ? B : ip;

                    outOpts.Add(MakeLineOption(
                        label: $"LINE SNAP (≤EditTol) @IP{i + 1}: move {(moveStart ? "Start" : "End")}",
                        ip: ip,
                        p0: p0,
                        p1: p1));

                    // Also offer the opposite end snap (only if it makes sense)
                    bool moveStartOpp = !moveStart;
                    Point p0o = moveStartOpp ? ip : A;
                    Point p1o = moveStartOpp ? B : ip;

                    outOpts.Add(MakeLineOption(
                        label: $"LINE SNAP (≤EditTol) @IP{i + 1}: move {(moveStartOpp ? "Start" : "End")}",
                        ip: ip,
                        p0: p0o,
                        p1: p1o));

                    continue;
                }

                // Extend/trim: prefer picked end, but also include the swapped end if it is "in front"
                bool pickedMoveStart = aPick.PickStart;

                // Outcome 1: move picked end (ray test uses EditTol, not 1e-9)
                {
                    bool moveStart = pickedMoveStart;
                    Point fixedEnd = moveStart ? B : A;
                    Point movingEnd = moveStart ? A : B;

                    if (IsOnRay_FromFixedThroughMoving(ip, fixedEnd, movingEnd, editTol))
                    {
                        Point p0 = moveStart ? ip : A;
                        Point p1 = moveStart ? B : ip;

                        outOpts.Add(MakeLineOption(
                            label: $"LINE trim @IP{i + 1}: move {(moveStart ? "Start" : "End")}",
                            ip: ip,
                            p0: p0,
                            p1: p1));
                    }
                }

                // Outcome 2: move opposite end (if valid)
                {
                    bool moveStart = !pickedMoveStart;
                    Point fixedEnd = moveStart ? B : A;
                    Point movingEnd = moveStart ? A : B;

                    if (IsOnRay_FromFixedThroughMoving(ip, fixedEnd, movingEnd, editTol))
                    {
                        Point p0 = moveStart ? ip : A;
                        Point p1 = moveStart ? B : ip;

                        outOpts.Add(MakeLineOption(
                            label: $"LINE trim @IP{i + 1}: move {(moveStart ? "Start" : "End")}",
                            ip: ip,
                            p0: p0,
                            p1: p1));
                    }
                }
            }

            // de-dup identical outcomes
            outOpts = DedupOptions(outOpts, editTol);

            var copy = outOpts.ToList();
            outOpts.Clear();
            outOpts.AddRange(copy);
        }



        private static TrimOption MakeLineOption(string label, Point ip, Point p0, Point p1)
        {
            // Replacement line
            string line = string.Format(CultureInfo.InvariantCulture,
                "LINE {0} {1}   {2} {3}",
                p0.X.ToString("R", CultureInfo.InvariantCulture),
                p0.Y.ToString("R", CultureInfo.InvariantCulture),
                p1.X.ToString("R", CultureInfo.InvariantCulture),
                p1.Y.ToString("R", CultureInfo.InvariantCulture));

            return new TrimOption
            {
                Label = label,
                IP = ip,
                ReplacementLine = line,
                PrevA = p0,
                PrevB = p1,
                PreviewWorld = new List<Point> { p0, p1 }
            };
        }

        // ---------- ARC options ----------

        private static void BuildArcTrimOptions(Pick aPick, List<Point> ips, List<TrimOption> outOpts)
        {
            var seg = aPick.Seg;

            SegView arc = NormalizeArcToCW(seg);

            Point origA = arc.A;
            Point origB = arc.B;
            Point C = arc.C;
            double r = arc.Radius;

            if (r < 1e-12)
                return;

            double tol = EditTol;
            double onCircTol = tol;
            const int PREVIEW_SAMPLES = 72;

            for (int i = 0; i < ips.Count; i++)
            {
                Point ip = ips[i];

                // must be on circle
                double dR = Math.Abs(TurnEditMath.Dist(ip, C) - r);
                if (dR > onCircTol)
                    continue;

                // Two fixed-end choices
                Point[] fixedEnds = new[] { origA, origB };

                for (int fe = 0; fe < fixedEnds.Length; fe++)
                {
                    Point fixedEnd = fixedEnds[fe];

                    // skip degenerate: ip == fixedEnd
                    if (TurnEditMath.Dist(ip, fixedEnd) <= tol)
                        continue;

                    // Branch 1: endpoints (fixed -> ip) as CW
                    {
                        Point start = fixedEnd;
                        Point end = ip;

                        if (TryMakeArcCwOption(
                            labelPrefix: $"ARC @IP{i + 1}: keep {(fe == 0 ? "Start" : "End")}",
                            start: start,
                            end: end,
                            C: C,
                            r: r,
                            samples: PREVIEW_SAMPLES,
                            out TrimOption opt))
                        {
                            opt.Label = $"{opt.Label} | {ClassifyShortLongCW(start, end, C)}";
                            outOpts.Add(opt);
                        }
                    }

                    // Branch 2: endpoints (ip -> fixed) as CW
                    {
                        Point start = ip;
                        Point end = fixedEnd;

                        if (TryMakeArcCwOption(
                            labelPrefix: $"ARC @IP{i + 1}: keep {(fe == 0 ? "Start" : "End")}",
                            start: start,
                            end: end,
                            C: C,
                            r: r,
                            samples: PREVIEW_SAMPLES,
                            out TrimOption opt))
                        {
                            opt.Label = $"{opt.Label} | {ClassifyShortLongCW(start, end, C)}";
                            outOpts.Add(opt);
                        }
                    }
                }
            }

            // Dedup identical outcomes
            outOpts = DedupOptions(outOpts, tol);
            var copy = outOpts.ToList();
            outOpts.Clear();
            outOpts.AddRange(copy);
        }


        private static SegView NormalizeArcToCW(SegView s)
        {
            if (s.Kind != SegKind.Arc)
                return s;

            if (!s.CCW)
                return s;

            // CCW -> CW normalization (internal only):
            // swap endpoints, keep same MID + center, set CCW=false
            return new SegView
            {
                Kind = SegKind.Arc,
                A = s.B,
                B = s.A,
                M = s.M,
                C = s.C,
                CCW = false
            };
        }

        private static bool TryMakeArcCwOption(
            string labelPrefix,
            Point start,
            Point end,
            Point C,
            double r,
            int samples,
            out TrimOption opt)
        {
            opt = new TrimOption();

            double aStart = TurnEditMath.Norm2Pi(Math.Atan2(start.Y - C.Y, start.X - C.X));
            double aEnd = TurnEditMath.Norm2Pi(Math.Atan2(end.Y - C.Y, end.X - C.X));

            double deltaCW = TurnEditMath.DeltaCW(aStart, aEnd); // 0..2pi
            if (!TurnEditMath.IsFinite(deltaCW) || deltaCW < 1e-12)
                return false;

            // CW sweep is negative in standard atan2 angle space
            double sweepSigned = -deltaCW;

            // Midpoint on that directed sweep
            double aMid = TurnEditMath.Norm2Pi(aStart + 0.5 * sweepSigned);

            Point M = new Point(
                C.X + r * Math.Cos(aMid),
                C.Y + r * Math.Sin(aMid));

            // Replacement line is ALWAYS ARC3_CW (per your latest instruction)
            string outLine = string.Format(CultureInfo.InvariantCulture,
                "ARC3_CCW {0} {1}   {2} {3}   {4} {5}   {6} {7}",
                start.X.ToString("R", CultureInfo.InvariantCulture),
                start.Y.ToString("R", CultureInfo.InvariantCulture),
                M.X.ToString("R", CultureInfo.InvariantCulture),
                M.Y.ToString("R", CultureInfo.InvariantCulture),
                end.X.ToString("R", CultureInfo.InvariantCulture),
                end.Y.ToString("R", CultureInfo.InvariantCulture),
                C.X.ToString("R", CultureInfo.InvariantCulture),
                C.Y.ToString("R", CultureInfo.InvariantCulture));

            // Preview polyline
            var pts = SampleArcStdAngles(C, r, aStart, sweepSigned, samples);

            opt.Label = labelPrefix;
            opt.IP = (TurnEditMath.Dist(start, end) < 1e-12) ? start : end; // not used strongly; dialog shows separate
            opt.ReplacementLine = outLine;
            opt.PrevA = start;
            opt.PrevB = end;
            opt.PreviewWorld = pts;

            return true;
        }

        private static string ClassifyShortLongCW(Point start, Point end, Point C)
        {
            double aStart = TurnEditMath.Norm2Pi(Math.Atan2(start.Y - C.Y, start.X - C.X));
            double aEnd = TurnEditMath.Norm2Pi(Math.Atan2(end.Y - C.Y, end.X - C.X));
            double deltaCW = TurnEditMath.DeltaCW(aStart, aEnd); // 0..2pi

            const double EPS = 1e-9;

            if (Math.Abs(deltaCW - Math.PI) <= 1e-7)
                return "180°";

            return (deltaCW <= Math.PI + EPS) ? "Short" : "Long";
        }

        private static List<Point> SampleArcStdAngles(Point C, double r, double angStart, double sweepSigned, int samples)
        {
            var pts = new List<Point>();
            int n = Math.Max(6, samples);

            for (int i = 0; i < n; i++)
            {
                double t = (n == 1) ? 0.0 : (double)i / (n - 1);
                double ang = angStart + sweepSigned * t;

                pts.Add(new Point(
                    C.X + r * Math.Cos(ang),
                    C.Y + r * Math.Sin(ang)));
            }

            return pts;
        }

        // ============================================================
        // Dialog + preview
        // ============================================================

        private static bool ShowTrimOptionsDialog(IHost host, List<TrimOption> options, Pick aPick, out int chosenIndex)
        {
            int chosenLocal = -1;
            chosenIndex = -1;

            var w = new Window
            {
                Title = "Trim: pick outcome",
                Width = 640,
                Height = 230,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                Owner = Application.Current?.MainWindow
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // detail
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

            var txt = new TextBlock
            {
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(txt, 0);
            root.Children.Add(txt);

            var txtDetail = new TextBlock
            {
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(txtDetail, 1);
            root.Children.Add(txtDetail);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnPrev = new Button { Content = "Prev", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            var btnNext = new Button { Content = "Next", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            var btnKeep = new Button { Content = "Keep", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "Cancel", Width = 90 };

            btnRow.Children.Add(btnPrev);
            btnRow.Children.Add(btnNext);
            btnRow.Children.Add(btnKeep);
            btnRow.Children.Add(btnCancel);

            Grid.SetRow(btnRow, 2);
            root.Children.Add(btnRow);

            w.Content = root;

            int idx = 0;
            bool keep = false;

            // choose a reasonable default option:
            // prefer a LINE option that moves the picked end, or otherwise first option.
            idx = FindDefaultOptionIndex(options, aPick);

            void Refresh()
            {
                if (idx < 0) idx = 0;
                if (idx >= options.Count) idx = options.Count - 1;

                var opt = options[idx];

                txt.Text = $"Outcomes: {options.Count}   (cycle and Keep)";
                txtDetail.Text =
                    $"[{idx + 1}] {opt.Label}\n" +
                    $"IP ≈ ({Fmt(opt.IP.X)},{Fmt(opt.IP.Y)})   A=({Fmt(opt.PrevA.X)},{Fmt(opt.PrevA.Y)})   B=({Fmt(opt.PrevB.X)},{Fmt(opt.PrevB.Y)})";

                RenderOutcomePreview(host, opt, options);
            }

            btnPrev.Click += (_, __) => { idx--; Refresh(); };
            btnNext.Click += (_, __) => { idx++; Refresh(); };

            btnKeep.Click += (_, __) =>
            {
                keep = true;
                chosenLocal = idx;
                w.DialogResult = true;
                w.Close();
            };

            btnCancel.Click += (_, __) =>
            {
                keep = false;
                chosenLocal = -1;
                w.DialogResult = false;
                w.Close();
            };

            w.Loaded += (_, __) => Refresh();

            bool? ok = w.ShowDialog();

            chosenIndex = chosenLocal;
            return (ok == true) && keep && chosenIndex >= 0;
        }

        private static int FindDefaultOptionIndex(List<TrimOption> options, Pick aPick)
        {
            if (options == null || options.Count == 0) return 0;

            double tol = EditTol;

            // For lines, try to prefer the outcome that keeps the opposite end fixed and moves the picked end.
            if (aPick?.Seg?.Kind == SegKind.Line)
            {
                Point A = aPick.Seg.A;
                Point B = aPick.Seg.B;
                bool pickStart = aPick.PickStart;

                for (int i = 0; i < options.Count; i++)
                {
                    var o = options[i];

                    if (pickStart)
                    {
                        if (TurnEditMath.Dist(o.PrevA, A) > tol && TurnEditMath.Dist(o.PrevB, B) <= tol)
                            return i;
                    }
                    else
                    {
                        if (TurnEditMath.Dist(o.PrevB, B) > tol && TurnEditMath.Dist(o.PrevA, A) <= tol)
                            return i;
                    }
                }
            }

            return 0;
        }


        private static void RenderOutcomePreview(IHost host, TrimOption opt, List<TrimOption> allOptions)
        {
            host.ClearPreviewOnly();

            double tol = EditTol;

            const double FAINT_OP = 0.35;
            const double HI_OP = 1.0;
            const double D_FAINT = 6.0;
            const double D_HI = 11.0;

            var ips = new List<Point>();
            foreach (var o in allOptions)
                AddUnique(ips, o.IP, tol);

            for (int i = 0; i < ips.Count; i++)
            {
                bool hi = TurnEditMath.Dist(ips[i], opt.IP) <= tol;

                host.DrawPreviewPointWorld(
                    ips[i],
                    Brushes.Red,
                    hi ? D_HI : D_FAINT,
                    hi ? HI_OP : FAINT_OP);
            }

            host.DrawPreviewPointWorld(opt.PrevA, Brushes.Orange, 7.0, 0.95);
            host.DrawPreviewPointWorld(opt.PrevB, Brushes.Orange, 7.0, 0.95);

            if (host is IHostPolyline hp && opt.PreviewWorld != null && opt.PreviewWorld.Count >= 2)
            {
                bool isArc = opt.ReplacementLine != null &&
                             opt.ReplacementLine.StartsWith("ARC3_", StringComparison.OrdinalIgnoreCase);

                hp.DrawPreviewPolylineWorld(
                    opt.PreviewWorld,
                    isArc ? Brushes.Magenta : Brushes.Yellow,
                    isArc ? 3.2 : 2.4,
                    1.0);
            }
        }


        private static void AddUnique(List<Point> pts, Point p, double tol)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                if (TurnEditMath.Dist(pts[i], p) <= tol)
                    return;
            }
            pts.Add(p);
        }

        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ============================================================
        // Intersection candidates (infinite primitives)
        // ============================================================

        private static List<Point> BuildIntersectionCandidates_Infinite(SegView a, SegView b)
        {
            double tol = EditTol;
            var outPts = new List<Point>();

            if (a.Kind == SegKind.Line && b.Kind == SegKind.Line)
            {
                if (TurnEditMath.TryIntersectInfiniteLines(a.A, a.B, b.A, b.B, out Point ip))
                    outPts.Add(ip);

                return DedupPoints(outPts, tol);
            }

            if (a.Kind == SegKind.Line && b.Kind == SegKind.Arc)
            {
                double rb = b.Radius;
                if (rb > 1e-9)
                    outPts.AddRange(TurnEditMath.IntersectLineCircle_Infinite(a.A, a.B, b.C, rb));

                return DedupPoints(outPts, tol);
            }

            if (a.Kind == SegKind.Arc && b.Kind == SegKind.Line)
            {
                double ra = a.Radius;
                if (ra > 1e-9)
                    outPts.AddRange(TurnEditMath.IntersectLineCircle_Infinite(b.A, b.B, a.C, ra));

                return DedupPoints(outPts, tol);
            }

            if (a.Kind == SegKind.Arc && b.Kind == SegKind.Arc)
            {
                double ra = a.Radius;
                double rb = b.Radius;

                if (ra > 1e-9 && rb > 1e-9)
                    outPts.AddRange(TurnEditMath.IntersectCircleCircle(a.C, ra, b.C, rb));

                return DedupPoints(outPts, tol);
            }

            return outPts;
        }


        private static List<Point> DedupPoints(List<Point> pts, double tol)
        {
            var outPts = new List<Point>();
            if (pts == null) return outPts;

            for (int i = 0; i < pts.Count; i++)
            {
                Point p = pts[i];
                bool found = false;

                for (int k = 0; k < outPts.Count; k++)
                {
                    if (TurnEditMath.Dist(p, outPts[k]) <= tol)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    outPts.Add(p);
            }

            return outPts;
        }

        // ============================================================
        // Geometry predicates
        // ============================================================

        private static bool IsOnRay_FromFixedThroughMoving(Point ip, Point fixedEnd, Point movingEnd, double tol)
        {
            Vector dir = movingEnd - fixedEnd;
            double dd = dir.X * dir.X + dir.Y * dir.Y;
            if (dd < 1e-18) return false;

            Vector v = ip - fixedEnd;
            double dot = v.X * dir.X + v.Y * dir.Y;

            return dot >= -tol;
        }

        private static bool IsPointOnSegmentInterior(Point a, Point b, Point p, double colTol, double betweenTol)
        {
            Vector ab = b - a;
            double ab2 = ab.X * ab.X + ab.Y * ab.Y;
            if (ab2 < 1e-18)
                return false;

            Vector ap = p - a;

            // colinearity via cross
            double cross = ab.X * ap.Y - ab.Y * ap.X;
            double crossAbs = Math.Abs(cross);
            double abLen = Math.Sqrt(ab2);

            if (crossAbs > colTol * abLen)
                return false;

            // param t
            double t = (ap.X * ab.X + ap.Y * ab.Y) / ab2;

            // strictly inside (not endpoints)
            return (t > 0.0 + betweenTol) && (t < 1.0 - betweenTol);
        }

        private static List<TrimOption> DedupOptions(List<TrimOption> opts, double tol)
        {
            var outOpts = new List<TrimOption>();
            if (opts == null) return outOpts;

            for (int i = 0; i < opts.Count; i++)
            {
                var o = opts[i];
                bool dup = false;

                for (int k = 0; k < outOpts.Count; k++)
                {
                    var x = outOpts[k];

                    // Same replacement line is the strongest dedup
                    if (string.Equals(o.ReplacementLine, x.ReplacementLine, StringComparison.Ordinal))
                    {
                        dup = true;
                        break;
                    }

                    // Also dedup if endpoints nearly identical and IP nearly identical
                    if (TurnEditMath.Dist(o.IP, x.IP) <= tol &&
                        TurnEditMath.Dist(o.PrevA, x.PrevA) <= tol &&
                        TurnEditMath.Dist(o.PrevB, x.PrevB) <= tol)
                    {
                        dup = true;
                        break;
                    }
                }

                if (!dup)
                    outOpts.Add(o);
            }

            return outOpts;
        }
    }
}
