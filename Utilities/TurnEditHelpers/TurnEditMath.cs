// File: Utilities/TurnEditHelpers/TurnEditMath.cs
using System;
using System.Collections.Generic;
using System.Windows;

namespace CNC_Improvements_gcode_solids.Utilities.TurnEditHelpers
{
    /// <summary>
    /// Shared math helpers for TurnEditWindow + TurnEditRender.
    /// Keep this class PURE (no WPF Canvas/Shapes here).
    /// World coords convention used by TurnEdit: Point.X = X, Point.Y = Z.
    /// </summary>
    internal static class TurnEditMath
    {
        public static bool IsFinite(double v) => !(double.IsNaN(v) || double.IsInfinity(v));

        public static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dz = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        public static double Dist2(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dz = a.Y - b.Y;
            return dx * dx + dz * dz;
        }

        /// <summary>Normalize radians to [0, 2pi).</summary>
        public static double Norm2Pi(double a)
        {
            double twoPi = 2.0 * Math.PI;
            a %= twoPi;
            if (a < 0.0) a += twoPi;
            return a;
        }

        /// <summary>CCW delta in [0, 2pi] from aFrom to aTo.</summary>
        public static double DeltaCCW(double aFrom, double aTo)
        {
            double d = Norm2Pi(aTo) - Norm2Pi(aFrom);
            if (d < 0.0) d += 2.0 * Math.PI;
            return d;
        }


        public static List<Point> SampleArc_CWFromZ(
    Point centerWorld,
    double rWorld,
    double startAngleCWZ,
    double sweepSigned,
    int samplesEven)
        {
            var pts = new List<Point>();

            if (rWorld <= 1e-9)
                return pts;

            if (!IsFinite(sweepSigned))
                return pts;

            int n = Math.Max(4, samplesEven);
            if ((n % 2) != 0) n++;

            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                double ang = startAngleCWZ + sweepSigned * t;
                ang = Norm2Pi(ang);

                // CW-from-+Z param:
                // x = cx + r*sin(theta)
                // z = cz + r*cos(theta)
                pts.Add(new Point(
                    centerWorld.X + rWorld * Math.Sin(ang),
                    centerWorld.Y + rWorld * Math.Cos(ang)
                ));
            }

            return pts;
        }



        /// <summary>CW delta in [0, 2pi] from aFrom to aTo.</summary>
        public static double DeltaCW(double aFrom, double aTo)
        {
            double d = Norm2Pi(aFrom) - Norm2Pi(aTo);
            if (d < 0.0) d += 2.0 * Math.PI;
            return d;
        }

        /// <summary>
        /// Angle measured CW from +Z, where:
        /// 0° = +Z direction, 90° = +X direction.
        /// Returns radians in [0, 2pi).
        /// </summary>
        public static double AngleCWFromZPlus(Point center, Point p)
        {
            double dx = p.X - center.X;
            double dz = p.Y - center.Y;

            // theta = atan2(dx, dz) gives 0 at +Z, increasing toward +X (CW)
            return Norm2Pi(Math.Atan2(dx, dz));
        }

        /// <summary>
        /// Compute signed sweep (radians) from A->B that CONTAINS MID M about center C.
        /// + = CCW, - = CW. If MID doesn't lie on either branch (bad MID), uses declared CCW if provided.
        /// </summary>
        public static double SignedSweepUsingMid(Point A, Point M, Point B, Point C, bool? declaredCCWIfBadMid = null)
        {
            // Angles in standard atan2 (CCW from +X), normalized
            double aA = Norm2Pi(Math.Atan2(A.Y - C.Y, A.X - C.X));
            double aB = Norm2Pi(Math.Atan2(B.Y - C.Y, B.X - C.X));
            double aM = Norm2Pi(Math.Atan2(M.Y - C.Y, M.X - C.X));

            double ccwSweep = DeltaCCW(aA, aB); // 0..2pi
            double ccwToM = DeltaCCW(aA, aM);
            if (ccwToM <= ccwSweep + 1e-9)
                return +ccwSweep;

            double cwSweep = DeltaCW(aA, aB); // 0..2pi
            double cwToM = DeltaCW(aA, aM);
            if (cwToM <= cwSweep + 1e-9)
                return -cwSweep;

            // MID is garbage -> fall back
            if (declaredCCWIfBadMid.HasValue)
                return declaredCCWIfBadMid.Value ? +ccwSweep : -cwSweep;

            // default
            return +ccwSweep;
        }


        public static List<Point> IntersectLineCircle_Infinite(Point a, Point b, Point c, double r)
        {
            // Intersections of the INFINITE line through a->b with the circle (center c, radius r).
            // Returns 0, 1 (tangent), or 2 points.
            var hits = new List<Point>();

            if (r <= 0.0)
                return hits;

            Vector d = b - a;
            double dd = d.X * d.X + d.Y * d.Y;
            if (dd < 1e-18)
                return hits;

            Vector f = a - c;

            // Solve |f + t d|^2 = r^2
            double A = dd;
            double B = 2.0 * (f.X * d.X + f.Y * d.Y);
            double C = (f.X * f.X + f.Y * f.Y) - r * r;

            double disc = B * B - 4.0 * A * C;
            if (disc < -1e-12)
                return hits;

            if (Math.Abs(disc) <= 1e-12)
            {
                double t = -B / (2.0 * A);
                hits.Add(a + t * d);
                return hits;
            }

            double s = Math.Sqrt(Math.Max(0.0, disc));
            double t1 = (-B - s) / (2.0 * A);
            double t2 = (-B + s) / (2.0 * A);

            hits.Add(a + t1 * d);
            hits.Add(a + t2 * d);
            return hits;
        }

        public static List<Point> IntersectCircleCircle(Point c1, double r1, Point c2, double r2)
        {
            // Circle-circle intersections.
            // Returns 0, 1 (tangent), or 2 points.
            var hits = new List<Point>();

            if (r1 <= 0.0 || r2 <= 0.0)
                return hits;

            double dx = c2.X - c1.X;
            double dy = c2.Y - c1.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);

            if (d < 1e-12)
                return hits; // concentric or identical -> infinite/none, ignore

            // No intersection cases
            if (d > r1 + r2 + 1e-12)
                return hits;

            if (d < Math.Abs(r1 - r2) - 1e-12)
                return hits;

            // a = distance from c1 to chord midpoint along line c1->c2
            double a = (r1 * r1 - r2 * r2 + d * d) / (2.0 * d);

            double h2 = r1 * r1 - a * a;

            // Midpoint of the intersection chord
            double xm = c1.X + a * (dx / d);
            double ym = c1.Y + a * (dy / d);

            if (h2 <= 1e-12)
            {
                // tangent
                hits.Add(new Point(xm, ym));
                return hits;
            }

            double h = Math.Sqrt(Math.Max(0.0, h2));

            // Perp vector (normalized)
            double rx = -dy / d;
            double ry = dx / d;

            hits.Add(new Point(xm + h * rx, ym + h * ry));
            hits.Add(new Point(xm - h * rx, ym - h * ry));

            return hits;
        }

        public static bool TryUnitDirAndLeftNormal(Point a, Point b, out Vector dir, out Vector normalLeft)
        {
            Vector v = b - a;
            double len = v.Length;
            if (len < 1e-12)
            {
                dir = new Vector();
                normalLeft = new Vector();
                return false;
            }

            dir = v / len;
            normalLeft = new Vector(-dir.Y, dir.X); // left normal
            return true;
        }

        public static bool TryIntersectInfiniteLines(Point p1, Point p2, Point p3, Point p4, out Point ip)
        {
            // Intersection of infinite lines p1->p2 and p3->p4.
            // Return false if parallel.
            double x1 = p1.X, y1 = p1.Y;
            double x2 = p2.X, y2 = p2.Y;
            double x3 = p3.X, y3 = p3.Y;
            double x4 = p4.X, y4 = p4.Y;

            double dx12 = x2 - x1;
            double dy12 = y2 - y1;
            double dx34 = x4 - x3;
            double dy34 = y4 - y3;

            double denom = dx12 * dy34 - dy12 * dx34;
            if (Math.Abs(denom) < 1e-12)
            {
                ip = new Point();
                return false;
            }

            double dx13 = x3 - x1;
            double dy13 = y3 - y1;

            double t = (dx13 * dy34 - dy13 * dx34) / denom;

            ip = new Point(x1 + t * dx12, y1 + t * dy12);
            return true;
        }

        public static void AddUniquePoint(List<Point> pts, Point p, double tol)
        {
            if (pts == null) return;

            for (int i = 0; i < pts.Count; i++)
            {
                if (Dist(pts[i], p) <= tol)
                    return;
            }

            pts.Add(p);
        }


    }
}
