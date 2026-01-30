using System;
using System.Globalization;
using System.Windows;

namespace CNC_Improvements_gcode_solids.TurningHelpers
{
    /// <summary>
    /// Shared segment representation:
    ///   LINE     x1 z1   x2 z2
    ///   ARC3_CW  x1 z1   xm zm   x2 z2   [cx cz  vSx vSz  vEx vEz]
    ///   ARC3_CCW x1 z1   xm zm   x2 z2   [cx cz  vSx vSz  vEx vEz]
    ///
    /// Appended arc extras (strongly preferred):
    ///   cx cz   : arc center in radius-space
    ///   vSx vSz : vector (P1 -> Center)  == (C - P1)
    ///   vEx vEz : vector (P2 -> Center)  == (C - P2)
    /// </summary>
    internal class ProfileSegment
    {
        public string Command;   // "LINE", "ARC3_CW", "ARC3_CCW"
        public Point P1;         // start
        public Point Pm;         // mid
        public Point P2;         // end

        public bool HasArcCenter;
        public Point ArcCenter;

        public bool HasArcVectors;
        public Point ArcStartToCenter; // (C - P1)
        public Point ArcEndToCenter;   // (C - P2)

        public bool IsLine => Command == "LINE";
        public bool IsArc => Command == "ARC3_CW" || Command == "ARC3_CCW";
        public bool IsCW => Command == "ARC3_CW";
        public bool IsCCW => Command == "ARC3_CCW";
    }

    internal static class OffsetGuideBuilderHelpers
    {
        // ================================================================
        // TYPE TAGS
        // ================================================================
        public static string GetSegmentType(ProfileSegment seg)
        {
            if (seg == null) return "?";
            if (seg.IsLine) return "L";
            if (seg.IsCW) return "CW";
            if (seg.IsCCW) return "CCW";
            return "?";
        }

        // ================================================================
        // PARSING HELPERS
        // ================================================================
        public static double ParseInv(string s)
        {
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public static void ApplyArcExtrasFromTokens(ProfileSegment arc, string[] parts)
        {
            // parts:
            // 0 cmd
            // 1..6  x1 z1 xm zm x2 z2
            // optional:
            // 7..8  cx cz
            // 9..12 vSx vSz vEx vEz   (vectors P1->C, P2->C)
            //
            // IMPORTANT: We DO NOT compute anything from 3-point arcs anymore.
            // We only trust appended data.

            if (arc == null || parts == null)
                return;

            // cx cz
            if (parts.Length >= 9)
            {
                double cx = ParseInv(parts[7]);
                double cz = ParseInv(parts[8]);

                arc.ArcCenter = new Point(cx, cz);
                arc.HasArcCenter = true;
            }

            // vSx vSz vEx vEz
            if (parts.Length >= 13)
            {
                double vsx = ParseInv(parts[9]);
                double vsz = ParseInv(parts[10]);
                double vex = ParseInv(parts[11]);
                double vez = ParseInv(parts[12]);

                arc.ArcStartToCenter = new Point(vsx, vsz);
                arc.ArcEndToCenter = new Point(vex, vez);
                arc.HasArcVectors = true;

                // If center wasn't supplied but vectors were, reconstruct center from P1 + vS
                if (!arc.HasArcCenter)
                {
                    arc.ArcCenter = new Point(arc.P1.X + vsx, arc.P1.Y + vsz);
                    arc.HasArcCenter = true;
                }
            }

            // No fallback computation. If extras are missing, they stay missing.
        }



        // ================================================================
        // VECTOR MATH
        // ================================================================
        private static bool TryNormalize(double x, double z, out double ux, out double uz)
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

        // ================================================================
        // YOUR 4 VECTOR BUILDERS (exact rule set)
        //
        // LINE: always P1 -> P2
        // ARC:
        //   Seg1ArcVector uses END vector (P2 -> Center) then +/-90
        //   Seg2ArcVector uses START vector (P1 -> Center) then +/-90
        //   CW  => +90
        //   CCW => -90
        //
        // No reversals. No circle solve.
        // ================================================================

        public static bool Seg1LineVector(ProfileSegment seg1, out double v1x, out double v1z)
        {
            v1x = v1z = 0;
            if (seg1 == null || !seg1.IsLine) return false;
            return TryNormalize(seg1.P2.X - seg1.P1.X, seg1.P2.Y - seg1.P1.Y, out v1x, out v1z);
        }

        // --- Seg2 LINE (TRAVEL tangent at seg2 start) ---
        // NO reversing. Both seg1 and seg2 tangents are in travel direction.
        public static bool Seg2LineVector(ProfileSegment seg2, out double v2x, out double v2z)
        {
            v2x = v2z = 0;

            if (seg2 == null || !seg2.IsLine)
                return false;

            // Travel direction P1 -> P2
            return TryNormalize(seg2.P2.X - seg2.P1.X, seg2.P2.Y - seg2.P1.Y, out v2x, out v2z);
        }

        // --- Seg1 ARC (incoming to corner at end) ---
        // Uses appended vectors: ArcEndToCenter = (C - P2)
        public static bool Seg1ArcVector(ProfileSegment seg1, out double v1x, out double v1z)
        {
            v1x = v1z = 0;

            if (seg1 == null || !seg1.IsArc)
                return false;

            // MUST already be present from appended fields
            if (!seg1.HasArcVectors)
                return false;

            // End radial vector (P2 -> Center) stored as (C - P2)
            double rx = seg1.ArcEndToCenter.X;
            double rz = seg1.ArcEndToCenter.Y;

            // IMPORTANT: For stored vectors (C - P), the correct mapping is:
            //   CW  => rotate -90
            //   CCW => rotate +90
            double tx, tz;
            if (seg1.IsCW) RotMinus90(rx, rz, out tx, out tz);
            else RotPlus90(rx, rz, out tx, out tz);

            return TryNormalize(tx, tz, out v1x, out v1z);
        }

        // --- Seg2 ARC (TRAVEL tangent at seg2 start) ---
        // Uses appended vectors only: ArcStartToCenter = (C - P1)
        // NO negation here. Tangency is handled purely by the downstream dot/cross math.
        public static bool Seg2ArcVector(ProfileSegment seg2, out double v2x, out double v2z)
        {
            v2x = v2z = 0;

            if (seg2 == null || !seg2.IsArc)
                return false;

            // MUST already be present from appended fields
            if (!seg2.HasArcVectors)
                return false;

            // Start radial vector (P1 -> Center) stored as (C - P1)
            double rx = seg2.ArcStartToCenter.X;
            double rz = seg2.ArcStartToCenter.Y;

            // For stored vectors (C - P):
            //   CW  => rotate -90
            //   CCW => rotate +90
            double tx, tz;
            if (seg2.IsCW) RotMinus90(rx, rz, out tx, out tz);
            else RotPlus90(rx, rz, out tx, out tz);

            return TryNormalize(tx, tz, out v2x, out v2z);
        }


        // ================================================================
        // ANGLES (single source of truth)
        // ================================================================
        public static void Seg1Seg2Angles(
            double v1x, double v1z,
            double v2x, double v2z,
            out double deltaDeg,
            out double innerDeg,
            out double outerDeg)
        {
            double dot = v1x * v2x + v1z * v2z;
            dot = Math.Clamp(dot, -1.0, 1.0);

            // delta between TRAVEL tangents in 0..180
            deltaDeg = Math.Acos(dot) * 180.0 / Math.PI;

            // corner angles
            innerDeg = 180.0 - deltaDeg;
            outerDeg = 180.0 + deltaDeg;
        }

        public static bool Seg1Seg2TanTest(double deltaDeg, double tolDeg)
        {
            return deltaDeg <= tolDeg;
        }

        // ================================================================
        // TOOL-SIDE PICK (single source of truth)
        // ================================================================
        public static bool GetCornerFromToolSide(
            double v1x, double v1z,
            double v2x, double v2z,
            int offsetDir,
            out double crossTurn,
            out bool chooseOuter)
        {
            // Define cross sign once, never touch it again:
            crossTurn = v1x * v2z - v1z * v2x;

            // Convention you've been using: (offsetDir * cross) < 0 => OUTER
            chooseOuter = (offsetDir * crossTurn) < 0.0;

            return true;
        }
    }
}
