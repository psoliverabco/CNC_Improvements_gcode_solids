using Clipper2Lib;
using System;
using System.Collections.Generic;

namespace CNC_Improvements_gcode_solids.Geometry
{
    public static class MillOffsetGeometry
    {
        // Simple container for parsed geometry
        public class MillSeg
        {
            public string Type; // "LINE" or "ARC3"
            public double X1, Y1;
            public double X2, Y2;
            public double X3, Y3; // for 3-pt arcs
        }

        // Example parser stub (you can keep your existing implementation)
        public static List<MillSeg> ParseMillShape(IEnumerable<string> lines)
        {
            var segs = new List<MillSeg>();
            foreach (string s in lines)
            {
                string line = s.Trim();
                if (line.StartsWith("LINE", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 5)
                    {
                        segs.Add(new MillSeg
                        {
                            Type = "LINE",
                            X1 = double.Parse(p[1]),
                            Y1 = double.Parse(p[2]),
                            X2 = double.Parse(p[3]),
                            Y2 = double.Parse(p[4])
                        });
                    }
                }
                else if (line.StartsWith("ARC3", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 7)
                    {
                        segs.Add(new MillSeg
                        {
                            Type = line.Contains("CW") ? "ARC3_CW" : "ARC3_CCW",
                            X1 = double.Parse(p[1]),
                            Y1 = double.Parse(p[2]),
                            X2 = double.Parse(p[3]),
                            Y2 = double.Parse(p[4]),
                            X3 = double.Parse(p[5]),
                            Y3 = double.Parse(p[6])
                        });
                    }
                }
            }
            return segs;
        }

        // Build offset polygon (integer scaling version)
        public static PathsD OffsetPath(
            PathD subjPath,
            double offset,
            JoinType joinType = JoinType.Round,
            EndType endType = EndType.Polygon,
            double miterLimit = 2.0,
            double arcTolerance = 0.25)
        {
            if (subjPath == null)
                throw new ArgumentNullException(nameof(subjPath));

            const double scale = 1000.0; // scale factor for fixed-point
            Path64 subj64 = new Path64(subjPath.Count);
            foreach (PointD pt in subjPath)
            {
                long ix = (long)Math.Round(pt.x * scale);
                long iy = (long)Math.Round(pt.y * scale);
                subj64.Add(new Point64(ix, iy));
            }

            // Wrap single path as Paths64
            Paths64 subjPaths = new Paths64 { subj64 };

            double delta = offset * scale;
            double arcTolScaled = arcTolerance * scale;

            // Perform offset in integer space
            Paths64 solution64 = Clipper.InflatePaths(
                subjPaths,
                delta,
                joinType,
                endType,
                miterLimit,
                arcTolScaled);

            // Convert back to double
            PathsD solutionD = new PathsD(solution64.Count);
            foreach (Path64 path64 in solution64)
            {
                PathD pathD = new PathD(path64.Count);
                foreach (Point64 ip in path64)
                {
                    pathD.Add(new PointD(ip.X / scale, ip.Y / scale));
                }
                solutionD.Add(pathD);
            }

            return solutionD;
        }

        // Example utility: build PathD from segments
        public static PathD BuildPathFromSegments(List<MillSeg> segs, double chordTol = 0.1)
        {
            PathD path = new PathD();

            foreach (var seg in segs)
            {
                if (seg.Type == "LINE")
                {
                    path.Add(new PointD(seg.X1, seg.Y1));
                    path.Add(new PointD(seg.X2, seg.Y2));
                }
                else if (seg.Type.StartsWith("ARC3", StringComparison.OrdinalIgnoreCase))
                {
                    // Approximate 3-point arc into line segments
                    var arcPoints = ApproximateArc(seg, chordTol);
                    path.AddRange(arcPoints);
                }
            }

            return path;
        }

        private static List<PointD> ApproximateArc(MillSeg seg, double chordTol)
        {
            var pts = new List<PointD>();

            // --- Extract 3 points ---
            double x1 = seg.X1, y1 = seg.Y1;
            double x2 = seg.X2, y2 = seg.Y2;
            double x3 = seg.X3, y3 = seg.Y3;

            // --- Compute circle center from the three points (circumcircle) ---
            double a = x1 * (y2 - y3) - y1 * (x2 - x3) + x2 * y3 - x3 * y2;
            if (Math.Abs(a) < 1e-9)
            {
                // Nearly collinear: fall back to straight segment
                pts.Add(new PointD(x1, y1));
                pts.Add(new PointD(x3, y3));
                return pts;
            }

            double b = ((x1 * x1 + y1 * y1) * (y3 - y2)
                      + (x2 * x2 + y2 * y2) * (y1 - y3)
                      + (x3 * x3 + y3 * y3) * (y2 - y1));

            double c = ((x1 * x1 + y1 * y1) * (x2 - x3)
                      + (x2 * x2 + y2 * y2) * (x3 - x1)
                      + (x3 * x3 + y3 * y3) * (x1 - x2));

            double cx = -b / (2.0 * a);
            double cy = -c / (2.0 * a);

            double r = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));

            // --- Angles from center to each point ---
            double a1 = Math.Atan2(y1 - cy, x1 - cx); // start
            double a2 = Math.Atan2(y2 - cy, x2 - cx); // mid
            double a3 = Math.Atan2(y3 - cy, x3 - cx); // end

            bool ccw = seg.Type.IndexOf("CCW", StringComparison.OrdinalIgnoreCase) >= 0;
            double twoPi = 2.0 * Math.PI;

            double sweep;

            if (ccw)
            {
                // CCW: positive sweep. Choose the CCW sweep that passes through a2
                // and has the shortest magnitude.
                sweep = a3 - a1;
                while (sweep <= 0.0) sweep += twoPi; // now in (0, 2π]

                double midSweep = a2 - a1;
                while (midSweep < 0.0) midSweep += twoPi; // in [0, 2π)

                // If the mid angle is NOT between start and end along this CCW sweep,
                // then we accidentally picked the long way around; flip to the other arc.
                if (midSweep > sweep)
                {
                    // Take the other CCW arc (negative sweep, equivalent but shorter the other way)
                    sweep = sweep - twoPi; // becomes negative, |sweep| < π in typical use
                }
            }
            else
            {
                // CW: negative sweep. Choose the CW sweep that passes through a2
                // and has the shortest magnitude.
                sweep = a3 - a1;
                while (sweep >= 0.0) sweep -= twoPi; // now in [-2π, 0)

                double midSweep = a2 - a1;
                while (midSweep > 0.0) midSweep -= twoPi; // in (-2π, 0]

                // For CW, sweeps are negative. If midSweep is "beyond" sweep (more negative),
                // we picked the wrong arc; flip to the other one.
                if (midSweep < sweep)
                {
                    sweep = sweep + twoPi; // move towards 0, shorter arc through mid
                }
            }

            // --- Discretize the chosen arc ---
            double absSweep = Math.Abs(sweep);
            if (absSweep < 1e-9)
            {
                // Degenerate: treat as straight line
                pts.Add(new PointD(x1, y1));
                pts.Add(new PointD(x3, y3));
                return pts;
            }

            int steps = Math.Max(6, (int)Math.Ceiling(absSweep * r / chordTol));
            double da = sweep / steps;

            for (int i = 0; i <= steps; i++)
            {
                double ang = a1 + da * i;
                pts.Add(new PointD(cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }

            return pts;
        }


    }
}
