// File: Utilities/TurnEditOutputGcode.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// TURN Edit: deterministic G-code output helpers.
    ///
    /// Contract:
    /// - X in editor space is RADIUS (world X).
    /// - TURN G-code X is DIAMETER.
    /// - Arc output uses IK only (no R), with I in DIAMETER units and K in Z units.
    /// </summary>
    internal static class TurnEditOutputGcode
    {
        internal enum SegKind { Line, Arc }

        internal sealed class Seg
        {
            public SegKind Kind;

            // World points (X=radius, Y=Z)
            public Point A; // Start
            public Point B; // End

            // Arc-only
            public Point M;
            public Point C;
            public bool CCW;
        }

        internal static List<string> BuildRegionGcodeFromClosedLoop_IK(IReadOnlyList<Seg> ordered)
        {
            var outLines = new List<string>();
            var inv = CultureInfo.InvariantCulture;

            // Keep high precision to prevent gaps after re-parse.
            const string F = "0.####";

            if (ordered == null || ordered.Count == 0) return outLines;

            // First move to start point (X is DIAMETER in TURN gcode)
            Point p0 = ordered[0].A;
            outLines.Add(string.Format(inv, "G1 X{0} Z{1}",
                (p0.X * 2.0).ToString(F, inv),
                p0.Y.ToString(F, inv)));

            for (int i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];

                if (s.Kind == SegKind.Line)
                {
                    Point pe = s.B;
                    outLines.Add(string.Format(inv, "G1 X{0} Z{1}",
                        (pe.X * 2.0).ToString(F, inv),
                        pe.Y.ToString(F, inv)));
                    continue;
                }

                // ARC
                Point ps = s.A;
                Point peArc = s.B;

                double r = Dist(ps, s.C);
                if (r < 1e-9) continue;

                // NOTE (do not "fix" this again):
                // In TURN, X words are DIAMETER, but I/K center offsets are in the SAME UNITS as geometry,
                // i.e. I is in radius-units (NOT doubled), K is Z-units.
                // Doubling I causes progressive arc corruption on repeated export/import.
                double I = (s.C.X - ps.X);   // radius-units
                double K = (s.C.Y - ps.Y);   // Z-units

                string g = s.CCW ? "G3" : "G2";

                // NEW: include R (radius) in output
                outLines.Add(string.Format(inv, "{0} X{1} Z{2} I{3} K{4} R{5}",
                    g,
                    (peArc.X * 2.0).ToString(F, inv),
                    peArc.Y.ToString(F, inv),
                    I.ToString(F, inv),
                    K.ToString(F, inv),
                    r.ToString(F, inv)));
            }

            return outLines;
        }


        internal static string BuildSegDiagnosticsText(IReadOnlyList<Seg> ordered)
        {
            var inv = CultureInfo.InvariantCulture;
            const string F = "0.########";

            if (ordered == null || ordered.Count == 0)
                return "(no segs)\n";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== OUTPUT SEGS (TurnEditOutputGcode) ===");
            sb.AppendLine("Index  Kind  Dir  A(x,z) -> B(x,z)   C(x,z)   R   I(radius)   I*2(ref)   K");
            sb.AppendLine("-----  ----  ---  -----------------  -------  --  ---------   --------   --");

            for (int i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];

                if (s.Kind == SegKind.Line)
                {
                    sb.AppendLine(string.Format(inv,
                        "{0,5}  {1,-4}  {2,-3}  A({3},{4}) -> B({5},{6})",
                        i,
                        "LINE",
                        "",
                        s.A.X.ToString(F, inv), s.A.Y.ToString(F, inv),
                        s.B.X.ToString(F, inv), s.B.Y.ToString(F, inv)
                    ));
                    continue;
                }

                // ARC
                string dir = s.CCW ? "CCW" : "CW";
                double r = Dist(s.A, s.C);

                // EXACT export math (I is NOT doubled)
                double I = (s.C.X - s.A.X);      // radius-units
                double I2 = I * 2.0;             // reference only (old bug)
                double K = (s.C.Y - s.A.Y);      // Z-units

                sb.AppendLine(string.Format(inv,
                    "{0,5}  {1,-4}  {2,-3}  A({3},{4}) -> B({5},{6})  C({7},{8})  R={9}  I={10}  I2={11}  K={12}",
                    i,
                    "ARC",
                    dir,
                    s.A.X.ToString(F, inv), s.A.Y.ToString(F, inv),
                    s.B.X.ToString(F, inv), s.B.Y.ToString(F, inv),
                    s.C.X.ToString(F, inv), s.C.Y.ToString(F, inv),
                    r.ToString(F, inv),
                    I.ToString(F, inv),
                    I2.ToString(F, inv),
                    K.ToString(F, inv)
                ));
            }

            return sb.ToString();
        }



        internal static (int firstX, int firstZ, int lastX, int lastZ) ComputeTurnMarkerIndices(IReadOnlyList<string> regionLines)
        {
            if (regionLines == null || regionLines.Count == 0) return (-1, -1, -1, -1);

            int firstX = -1, firstZ = -1, lastX = -1, lastZ = -1;
            for (int i = 0; i < regionLines.Count; i++)
            {
                string s = regionLines[i] ?? "";
                if (firstX < 0 && LineHasAxis(s, 'X')) firstX = i;
                if (firstZ < 0 && LineHasAxis(s, 'Z')) firstZ = i;
                if (LineHasAxis(s, 'X')) lastX = i;
                if (LineHasAxis(s, 'Z')) lastZ = i;
            }

            return (firstX, firstZ, lastX, lastZ);
        }

        internal static List<string> AppendStartEndMarkers(List<string> lines, string newRegName)
        {
            if (lines == null || lines.Count == 0)
                throw new Exception("No lines to tag.");

            string tagST = $" ({newRegName} ST)";
            string tagEND = $" ({newRegName} END)";

            lines.Insert(0, tagST);
            lines.Add(tagEND);
            return lines;
        }

        private static bool LineHasAxis(string? line, char axis)
        {
            if (string.IsNullOrEmpty(line)) return false;

            string a = axis.ToString();
            var rx = new Regex(@"(?i)(?<![A-Z])" + a + @"\s*([-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)");
            return rx.IsMatch(line);
        }

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }




    }
}
