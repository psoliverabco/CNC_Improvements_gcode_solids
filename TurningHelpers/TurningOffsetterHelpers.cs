using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace CNC_Improvements_gcode_solids.TurningHelpers
{
    /// <summary>
    /// Helper math + parsing for TurningOffsetter.
    ///
    /// IMPORTANT (your rule):
    /// - We DO NOT compute arc centers from 3-point geometry anymore.
    /// - We ONLY trust the appended arc extras in the profile text.
    ///   ARC3_* must contain at least cx cz (fields 7,8).
    /// </summary>
    internal static class TurningOffsetterHelpers
    {
        // ---------------------------
        // Parsing
        // ---------------------------

        public static List<ProfileSegment> ParseProfileSegmentsStrict(List<string> profileShape)
        {
            var segments = new List<ProfileSegment>();
            if (profileShape == null) return segments;

            foreach (string raw in profileShape)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string line = raw.Trim();
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;

                string cmd = parts[0].ToUpperInvariant();

                if (cmd == "LINE")
                {
                    if (parts.Length < 5) continue;

                    double x1 = ParseInv(parts[1]);
                    double z1 = ParseInv(parts[2]);
                    double x2 = ParseInv(parts[3]);
                    double z2 = ParseInv(parts[4]);

                    segments.Add(new ProfileSegment
                    {
                        Command = "LINE",
                        P1 = new Point(x1, z1),
                        Pm = new Point((x1 + x2) * 0.5, (z1 + z2) * 0.5),
                        P2 = new Point(x2, z2),
                        HasArcCenter = false,
                        HasArcVectors = false
                    });
                }
                else if (cmd == "ARC3_CW" || cmd == "ARC3_CCW")
                {
                    if (parts.Length < 9)
                        throw new InvalidOperationException(
                            "Arc segment is missing appended center fields (cx cz).\n" +
                            "Your pipeline requires extras appended to ARC3_* lines.\n" +
                            "Expected: ARC3_* x1 z1 xm zm x2 z2 cx cz [vSx vSz vEx vEz]");

                    double x1 = ParseInv(parts[1]);
                    double z1 = ParseInv(parts[2]);
                    double xm = ParseInv(parts[3]);
                    double zm = ParseInv(parts[4]);
                    double x2 = ParseInv(parts[5]);
                    double z2 = ParseInv(parts[6]);

                    double cx = ParseInv(parts[7]);
                    double cz = ParseInv(parts[8]);

                    var seg = new ProfileSegment
                    {
                        Command = cmd,
                        P1 = new Point(x1, z1),
                        Pm = new Point(xm, zm),
                        P2 = new Point(x2, z2),
                        HasArcCenter = true,
                        ArcCenter = new Point(cx, cz)
                    };

                    // Optional vectors (P1->C, P2->C) if present
                    if (parts.Length >= 13)
                    {
                        double vsx = ParseInv(parts[9]);
                        double vsz = ParseInv(parts[10]);
                        double vex = ParseInv(parts[11]);
                        double vez = ParseInv(parts[12]);

                        seg.HasArcVectors = true;
                        seg.ArcStartToCenter = new Point(vsx, vsz);
                        seg.ArcEndToCenter = new Point(vex, vez);
                    }
                    else
                    {
                        seg.HasArcVectors = false;
                    }

                    segments.Add(seg);
                }
            }

            return segments;
        }

        public static double ParseInv(string s)
        {
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ---------------------------
        // Small segment threshold
        // ---------------------------

        public static double SmallSegmentThreshold()
        {
            // You said you have this setting. This must exist in Settings:
            // Name: SmallSegment  Type: double  Scope: User  Value: e.g. 0.05
            double v = CNC_Improvements_gcode_solids.Properties.Settings.Default.TangentAngTol;
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
            return (v < 0.0) ? 0.0 : v;
        }


        // ---------------------------
        // Basic vector / geometry
        // ---------------------------

        public static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dz = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        public static double LineLen(Point a, Point b) => Dist(a, b);

        public static bool TryNormalize(double x, double z, out double ux, out double uz)
        {
            double len = Math.Sqrt(x * x + z * z);
            if (len < 1e-12)
            {
                ux = uz = 0;
                return false;
            }
            ux = x / len;
            uz = z / len;
            return true;
        }

        public static double AngleAt(Point center, Point p)
        {
            return Math.Atan2(p.Y - center.Y, p.X - center.X);
        }

        public static double NormalizePositive(double angRad)
        {
            double twoPi = Math.PI * 2.0;
            angRad %= twoPi;
            if (angRad < 0) angRad += twoPi;
            return angRad;
        }

        public static double Mod2Pi(double angRad) => NormalizePositive(angRad);

        public static Point ProjectToRadius(Point p, Point c, double r)
        {
            double vx = p.X - c.X;
            double vz = p.Y - c.Y;

            double len = Math.Sqrt(vx * vx + vz * vz);
            if (len < 1e-12) return p;

            double ux = vx / len;
            double uz = vz / len;
            return new Point(c.X + ux * r, c.Y + uz * r);
        }

        // ---------------------------
        // Arc mid-point (CRITICAL FIX)
        // ---------------------------
        //
        // The bug you saw ("diametrically opposite midpoint") happens when midpoint
        // is computed via naive averaging of angles (wrap + direction not handled).
        //
        // This function always computes the midpoint by walking HALF the directed sweep.
        // ---------------------------

        public static Point MidPointOnArc(Point p1, Point p2, Point c, bool isCW, bool preferMinor)
            => MidPointOnArc(p1, p2, c, isCW, preferMinor, null);

        public static Point MidPointOnArc(Point p1, Point p2, Point c, bool isCW, bool preferMinor, Point? hintPoint)
        {
            // radius from p1 (should be consistent after projection)
            double r = Dist(p1, c);
            if (r < 1e-12) return p1;

            double a1 = AngleAt(c, p1);
            double a2 = AngleAt(c, p2);

            // CCW sweep from a1 to a2
            double sweepCCW = Mod2Pi(a2 - a1);
            double sweepCW = Mod2Pi(a1 - a2);

            // If caller says "preferMinor" and no hint is supplied, pick the shorter sweep
            // by possibly flipping direction.
            bool useCW = isCW;
            double sweep = isCW ? sweepCW : sweepCCW;

            if (preferMinor && hintPoint == null)
            {
                if (sweepCW < sweepCCW)
                {
                    useCW = true;
                    sweep = sweepCW;
                }
                else
                {
                    useCW = false;
                    sweep = sweepCCW;
                }
            }

            // If a hint is provided, choose the sweep (CW/CCW) that actually CONTAINS the hint angle.
            if (hintPoint != null)
            {
                double ah = AngleAt(c, hintPoint.Value);

                bool hintOnCCW = IsAngleOnCCWArc(a1, a2, ah);
                bool hintOnCW = IsAngleOnCWArc(a1, a2, ah);

                // prefer the direction that contains the hint
                if (hintOnCW && !hintOnCCW)
                {
                    useCW = true;
                    sweep = sweepCW;
                }
                else if (hintOnCCW && !hintOnCW)
                {
                    useCW = false;
                    sweep = sweepCCW;
                }
                else
                {
                    // ambiguous: fall back to requested direction
                    useCW = isCW;
                    sweep = isCW ? sweepCW : sweepCCW;
                }
            }

            // Walk half the directed sweep (THIS is the key fix)
            double amid = useCW
                ? NormalizePositive(a1 - sweep * 0.5)
                : NormalizePositive(a1 + sweep * 0.5);

            return new Point(c.X + Math.Cos(amid) * r, c.Y + Math.Sin(amid) * r);
        }

        private static bool IsAngleOnCCWArc(double a1, double a2, double ah)
        {
            // CCW arc from a1 to a2 (inclusive)
            double sweep = Mod2Pi(a2 - a1);
            double d = Mod2Pi(ah - a1);
            return d <= sweep + 1e-12;
        }

        private static bool IsAngleOnCWArc(double a1, double a2, double ah)
        {
            // CW arc from a1 to a2 (inclusive)
            double sweep = Mod2Pi(a1 - a2);
            double d = Mod2Pi(a1 - ah);
            return d <= sweep + 1e-12;
        }

        // ---------------------------
        // Travel tangents (for turn/cross, fillet direction)
        // ---------------------------

        public static bool TryGetTravelTangentLine(Point p1, Point p2, out double tx, out double tz)
        {
            return TryNormalize(p2.X - p1.X, p2.Y - p1.Y, out tx, out tz);
        }

        public static bool TryGetTravelTangentArcAtStart(ProfileSegment arc, Point startPoint, out double tx, out double tz)
        {
            tx = tz = 0;
            if (arc == null || !arc.IsArc || !arc.HasArcCenter) return false;

            double rx = arc.ArcCenter.X - startPoint.X;
            double rz = arc.ArcCenter.Y - startPoint.Y;

            // Using point->center vector:
            // CW  => +90, CCW => -90 (matches your working convention)
            double tdx, tdz;
            if (arc.IsCW) RotPlus90(rx, rz, out tdx, out tdz);
            else RotMinus90(rx, rz, out tdx, out tdz);

            return TryNormalize(tdx, tdz, out tx, out tz);
        }

        public static bool TryGetTravelTangentArcAtEnd(ProfileSegment arc, Point endPoint, out double tx, out double tz)
        {
            tx = tz = 0;
            if (arc == null || !arc.IsArc || !arc.HasArcCenter) return false;

            double rx = arc.ArcCenter.X - endPoint.X;
            double rz = arc.ArcCenter.Y - endPoint.Y;

            double tdx, tdz;
            if (arc.IsCW) RotPlus90(rx, rz, out tdx, out tdz);
            else RotMinus90(rx, rz, out tdx, out tdz);

            return TryNormalize(tdx, tdz, out tx, out tz);
        }

        private static void RotPlus90(double x, double z, out double rx, out double rz)
        {
            rx = -z;
            rz = +x;
        }

        private static void RotMinus90(double x, double z, out double rx, out double rz)
        {
            rx = +z;
            rz = -x;
        }

        // ---------------------------
        // Offsetting
        // ---------------------------

        public static bool TryOffsetLine(Point p1, Point p2, int offsetDir, double rad, out Point op1, out Point op2)
        {
            op1 = new Point();
            op2 = new Point();

            double dx = p2.X - p1.X;
            double dz = p2.Y - p1.Y;

            if (!TryNormalize(dx, dz, out double ux, out double uz))
                return false;

            // Left normal
            double nx = -uz;
            double nz = +ux;

            double off = offsetDir * rad;

            op1 = new Point(p1.X + nx * off, p1.Y + nz * off);
            op2 = new Point(p2.X + nx * off, p2.Y + nz * off);
            return true;
        }

        // ---------------------------
        // Intersections (infinite primitives)
        // ---------------------------

        public static bool TryIntersectLineLine(Point a1, Point a2, Point b1, Point b2, out Point p)
        {
            p = new Point();

            double x1 = a1.X, y1 = a1.Y;
            double x2 = a2.X, y2 = a2.Y;
            double x3 = b1.X, y3 = b1.Y;
            double x4 = b2.X, y4 = b2.Y;

            double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (Math.Abs(den) < 1e-12) return false;

            double px = ((x1 * y2 - y1 * x2) * (x3 - x4) - (x1 - x2) * (x3 * y4 - y3 * x4)) / den;
            double py = ((x1 * y2 - y1 * x2) * (y3 - y4) - (y1 - y2) * (x3 * y4 - y3 * x4)) / den;

            p = new Point(px, py);
            return true;
        }

        public static bool TryIntersectLineCircle(Point p1, Point p2, Point c, double r, out Point i1, out Point i2, out int count)
        {
            i1 = new Point();
            i2 = new Point();
            count = 0;

            // Line param: p = p1 + t*(d)
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;

            double fx = p1.X - c.X;
            double fy = p1.Y - c.Y;

            double a = dx * dx + dy * dy;
            if (a < 1e-12) return false;

            double b = 2 * (fx * dx + fy * dy);
            double cc = fx * fx + fy * fy - r * r;

            double disc = b * b - 4 * a * cc;
            if (disc < -1e-12) return false;
            if (disc < 0) disc = 0;

            double s = Math.Sqrt(disc);
            double t1 = (-b - s) / (2 * a);
            double t2 = (-b + s) / (2 * a);

            Point pA = new Point(p1.X + t1 * dx, p1.Y + t1 * dy);
            Point pB = new Point(p1.X + t2 * dx, p1.Y + t2 * dy);

            if (Math.Abs(disc) < 1e-12)
            {
                i1 = pA;
                count = 1;
                return true;
            }

            i1 = pA;
            i2 = pB;
            count = 2;
            return true;
        }

        public static bool TryIntersectCircleCircle(Point c1, double r1, Point c2, double r2, out Point i1, out Point i2, out int count)
        {
            i1 = new Point();
            i2 = new Point();
            count = 0;

            double d = Dist(c1, c2);
            if (d < 1e-12) return false;

            if (d > r1 + r2 + 1e-12) return false;
            if (d < Math.Abs(r1 - r2) - 1e-12) return false;

            double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
            double h2 = r1 * r1 - a * a;
            if (h2 < -1e-12) return false;
            if (h2 < 0) h2 = 0;

            double h = Math.Sqrt(h2);

            double x0 = c1.X + a * (c2.X - c1.X) / d;
            double y0 = c1.Y + a * (c2.Y - c1.Y) / d;

            double rx = -(c2.Y - c1.Y) * (h / d);
            double ry = (c2.X - c1.X) * (h / d);

            Point pA = new Point(x0 + rx, y0 + ry);
            Point pB = new Point(x0 - rx, y0 - ry);

            if (h < 1e-12)
            {
                i1 = pA;
                count = 1;
                return true;
            }

            i1 = pA;
            i2 = pB;
            count = 2;
            return true;
        }

        public static Point ChooseClosest(Point hint, Point a, Point b)
        {
            double da = Dist(hint, a);
            double db = Dist(hint, b);
            return (da <= db) ? a : b;
        }

        // ---------------------------
        // Arc length (directional)
        // ---------------------------

        public static double ArcLen(Point p1, Point p2, Point c, bool isCW)
        {
            double r = (Dist(p1, c) + Dist(p2, c)) * 0.5;
            if (r < 1e-12) return 0.0;

            double a1 = AngleAt(c, p1);
            double a2 = AngleAt(c, p2);

            double sweep = isCW ? Mod2Pi(a1 - a2) : Mod2Pi(a2 - a1);
            return r * sweep;
        }

        public static double ArcLen(Point p1, Point p2, Point c, bool isCW, bool preferMinor)
        {
            // Keep a compatible overload (TurningOffsetter may call this).
            // If preferMinor is true, choose smaller sweep by possibly flipping direction,
            // but DO NOT change the original arc command in the geometry unless caller does.
            double r = (Dist(p1, c) + Dist(p2, c)) * 0.5;
            if (r < 1e-12) return 0.0;

            double a1 = AngleAt(c, p1);
            double a2 = AngleAt(c, p2);

            double sweepCCW = Mod2Pi(a2 - a1);
            double sweepCW = Mod2Pi(a1 - a2);

            double sweep = isCW ? sweepCW : sweepCCW;
            if (preferMinor) sweep = Math.Min(sweepCW, sweepCCW);

            return r * sweep;
        }



        public static List<string> ApplyQuadrantNoseShiftToProfileText(List<string> profileOpen, int quadrant, double noseRad)
        {
            // Baseline: do nothing
            if (profileOpen == null) return new List<string>();
            if (quadrant == 9) return new List<string>(profileOpen);
            if (noseRad == 0.0) return new List<string>(profileOpen);

            // Map quadrant -> (dx, dz)
            // You already defined:
            //  Quad3 (P3) : +X +Z
            //  Quad2 (P2) : -X +Z
            //  Quad7 (P7) : (X not used) +Z  => treat as dx=0, dz=+R
            //
            // Anything not explicitly handled: return unchanged (safe).




            double dx = 0, dz = 0;



            switch (quadrant)
            {
                case 1: dx = -noseRad; dz = -noseRad; break;
                case 2: dx = -noseRad; dz = +noseRad; break;
                case 3: dx = +noseRad; dz = +noseRad; break;
                case 4: dx = +noseRad; dz = -noseRad; break;
                case 5: dz = -noseRad; break;
                case 6: dx = -noseRad; break;
                case 7: dz = +noseRad; break;
                case 8: dx = +noseRad; break;





                default:
                    return new List<string>(profileOpen);
            }

            var outLines = new List<string>(profileOpen.Count);

            foreach (string raw in profileOpen)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    outLines.Add(raw);
                    continue;
                }

                string line = raw.Trim();
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    outLines.Add(raw);
                    continue;
                }

                string cmd = parts[0].ToUpperInvariant();

                if (cmd == "LINE")
                {
                    // LINE x1 z1   x2 z2
                    if (parts.Length < 5) { outLines.Add(raw); continue; }

                    double x1 = ParseInv(parts[1]) + dx;
                    double z1 = ParseInv(parts[2]) + dz;
                    double x2 = ParseInv(parts[3]) + dx;
                    double z2 = ParseInv(parts[4]) + dz;

                    outLines.Add(string.Format(CultureInfo.InvariantCulture,
                        "LINE {0} {1}   {2} {3}",
                        x1, z1, x2, z2));

                    continue;
                }

                if (cmd == "ARC3_CW" || cmd == "ARC3_CCW")
                {
                    // ARC3_* x1 z1   xm zm   x2 z2   [cx cz] [optional vectors...]
                    if (parts.Length < 7) { outLines.Add(raw); continue; }

                    double x1 = ParseInv(parts[1]) + dx;
                    double z1 = ParseInv(parts[2]) + dz;
                    double xm = ParseInv(parts[3]) + dx;
                    double zm = ParseInv(parts[4]) + dz;
                    double x2 = ParseInv(parts[5]) + dx;
                    double z2 = ParseInv(parts[6]) + dz;

                    // Rebuild keeping any extras:
                    // - if cx cz exist (strict pipeline usually does), they MUST be shifted too
                    // - vectors (vsx vSz vEx vEz) are DIRECTION vectors: do NOT shift them
                    if (parts.Length >= 9)
                    {
                        double cx = ParseInv(parts[7]) + dx;
                        double cz = ParseInv(parts[8]) + dz;

                        if (parts.Length >= 13)
                        {
                            // keep vectors unchanged
                            double vsx = ParseInv(parts[9]);
                            double vsz = ParseInv(parts[10]);
                            double vex = ParseInv(parts[11]);
                            double vez = ParseInv(parts[12]);

                            outLines.Add(string.Format(CultureInfo.InvariantCulture,
                                "{0} {1} {2}   {3} {4}   {5} {6}   {7} {8}   {9} {10}   {11} {12}",
                                cmd,
                                x1, z1,
                                xm, zm,
                                x2, z2,
                                cx, cz,
                                vsx, vsz,
                                vex, vez));
                        }
                        else
                        {
                            outLines.Add(string.Format(CultureInfo.InvariantCulture,
                                "{0} {1} {2}   {3} {4}   {5} {6}   {7} {8}",
                                cmd,
                                x1, z1,
                                xm, zm,
                                x2, z2,
                                cx, cz));
                        }
                    }
                    else
                    {
                        // No center extras: just shift the 3 points and keep as-is format
                        outLines.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0} {1} {2}   {3} {4}   {5} {6}",
                            cmd,
                            x1, z1,
                            xm, zm,
                            x2, z2));
                    }

                    continue;
                }

                // Unknown command: pass through unchanged
                outLines.Add(raw);
            }

            return outLines;
        }


        public static bool TryComputeArcDirectionFromMidpoint(
    Point p1,
    Point pm,
    Point p2,
    Point center,
    out bool isCW,
    double angTolRad = 1e-9)
        {
            isCW = false;

            // Degenerate: if endpoints are basically same, direction is ambiguous
            if (Dist(p1, p2) < 1e-12)
                return false;

            double a1 = AngleAt(center, p1);
            double a2 = AngleAt(center, p2);
            double am = AngleAt(center, pm);

            // CCW sweep from a1 to a2 and where am lands along that sweep
            double sweepCCW = Mod2Pi(a2 - a1);
            double dCCW = Mod2Pi(am - a1);

            // CW sweep from a1 to a2 and where am lands along that sweep
            double sweepCW = Mod2Pi(a1 - a2);
            double dCW = Mod2Pi(a1 - am);

            bool onCCW = dCCW <= sweepCCW + angTolRad;
            bool onCW = dCW <= sweepCW + angTolRad;

            // If midpoint lies on exactly one directed sweep, that's the direction.
            if (onCW && !onCCW)
            {
                isCW = true;
                return true;
            }

            if (onCCW && !onCW)
            {
                isCW = false;
                return true;
            }

            // Ambiguous (can happen with near-180/360 numeric cases or bad pm)
            return false;
        }





    }
}
