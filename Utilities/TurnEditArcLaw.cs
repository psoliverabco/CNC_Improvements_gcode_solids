// File: Utilities/TurnEditArcLaw.cs
using CNC_Improvements_gcode_solids.Utilities.TurnEditHelpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// Arc-law / fillet candidate normalization logic used by the ArcTest bench and Fillet preview.
    ///
    /// PURE logic:
    /// - no Canvas/WPF shape creation
    /// - no TurnEditWindow private EditSeg types
    ///
    /// Inputs:
    /// - candidate fillet centers (CP list)
    /// - tangent point resolvers for Seg A and Seg B (provided by TurnEditWindow)
    ///
    /// Outputs:
    /// - FilletArcData list (CW-normalized, short sweep <= 180°, with optional 180° complement)
    /// - formatted log text
    /// </summary>
    internal static class TurnEditArcLaw
    {
        internal delegate bool TryGetTangent(Point filletCenter, double rFillet, out Point tanWorld);

        internal sealed class FilletArcData
        {
            public int Index { get; set; }                 // 1-based for logging
            public string PairType { get; set; } = "";     // "LINE-LINE", "LINE-ARC", "ARC-ARC"
            public string CenterSource { get; set; } = ""; // "C1", "C2"... (which candidate center)
            public bool Is180Complement { get; set; }      // true only for the special 180° second arc

            public Point CP { get; set; }                  // fillet center (candidate)
            public double R { get; set; }                  // fillet radius

            public Point Tan1 { get; set; }                // ordered by CW normalization rule
            public Point Tan2 { get; set; }

            public double AngTan1 { get; set; }            // CW from +Z (0..2pi)
            public double AngTan2 { get; set; }
            public double DeltaCW_Tan1ToTan2_Increasing { get; set; } // 0..2pi

            public double ShortSweep { get; set; }         // 0..pi (<= 180°)
            public bool ShortIsForward { get; set; }       // always true after normalization

            public double AngMidShort { get; set; }        // midpoint angle for the short sweep
            public Point MidShort { get; set; }            // midpoint point on fillet circle for short sweep

            public string Tan1From { get; set; } = "";     // "A" or "B"
            public string Tan2From { get; set; } = "";
        }

        public static void BuildCandidates(
            List<FilletArcData> outList,
            string pairTypeLabel,
            double rFillet,
            IReadOnlyList<Point> centers,
            TryGetTangent tryGetTanA,
            TryGetTangent tryGetTanB)
        {
            if (outList == null) throw new ArgumentNullException(nameof(outList));
            outList.Clear();

            if (centers == null || centers.Count == 0)
                return;

            if (rFillet <= 1e-12)
                return;

            const double EPS_ANGLE = 1e-9;
            const double EPS_180 = 1e-9;
            double twoPi = 2.0 * Math.PI;

            int idx = 0;

            for (int ci = 0; ci < centers.Count; ci++)
            {
                Point cp = centers[ci];
                string cLabel = "C" + (ci + 1).ToString(CultureInfo.InvariantCulture);

                if (!tryGetTanA(cp, rFillet, out Point tA))
                    continue;

                if (!tryGetTanB(cp, rFillet, out Point tB))
                    continue;

                double angA = TurnEditMath.AngleCWFromZPlus(cp, tA);
                double angB = TurnEditMath.AngleCWFromZPlus(cp, tB);

                // Base ordering rule:
                // tan1 = first encountered when traveling CW from 0deg where 0deg is +Z.
                Point tan1 = tA, tan2 = tB;
                double ang1 = angA, ang2 = angB;
                string tan1From = "A", tan2From = "B";

                if (angB + EPS_ANGLE < angA)
                {
                    tan1 = tB; tan2 = tA;
                    ang1 = angB; ang2 = angA;
                    tan1From = "B"; tan2From = "A";
                }

                // delta along increasing CW-angle direction from tan1 -> tan2 (0..2pi)
                double deltaCW = ang2 - ang1;
                if (deltaCW < 0.0) deltaCW += twoPi;

                // CW NORMALIZATION RULE:
                // If (tan2 - tan1) increasing > 180 => swap endpoints so sweep <= 180 and CW.
                if (deltaCW > Math.PI + EPS_ANGLE)
                {
                    (tan1, tan2) = (tan2, tan1);
                    (ang1, ang2) = (ang2, ang1);
                    (tan1From, tan2From) = (tan2From, tan1From);

                    deltaCW = ang2 - ang1;
                    if (deltaCW < 0.0) deltaCW += twoPi;
                }

                double shortSweep = deltaCW;       // 0..pi
                bool shortIsForward = true;        // always true after normalization

                double angMidShort = TurnEditMath.Norm2Pi(ang1 + 0.5 * shortSweep);

                // CW-from-+Z param:
                // x = cx + r*sin(theta)
                // z = cz + r*cos(theta)
                Point midShortWorld = new Point(
                    cp.X + rFillet * Math.Sin(angMidShort),
                    cp.Y + rFillet * Math.Cos(angMidShort)
                );

                idx++;
                outList.Add(new FilletArcData
                {
                    Index = idx,
                    PairType = pairTypeLabel ?? "",
                    CenterSource = cLabel,
                    Is180Complement = false,

                    CP = cp,
                    R = rFillet,

                    Tan1 = tan1,
                    Tan2 = tan2,
                    Tan1From = tan1From,
                    Tan2From = tan2From,

                    AngTan1 = ang1,
                    AngTan2 = ang2,
                    DeltaCW_Tan1ToTan2_Increasing = deltaCW,

                    ShortSweep = shortSweep,
                    ShortIsForward = shortIsForward,

                    AngMidShort = angMidShort,
                    MidShort = midShortWorld
                });

                // SPECIAL CASE: exactly 180° => add complement arc
                if (Math.Abs(shortSweep - Math.PI) <= EPS_180)
                {
                    Point tan1b = tan2;
                    Point tan2b = tan1;
                    double ang1b = ang2;
                    double ang2b = ang1;

                    string tan1FromB = tan2From;
                    string tan2FromB = tan1From;

                    double angMidOpp = TurnEditMath.Norm2Pi(angMidShort + Math.PI);

                    Point midOppWorld = new Point(
                        cp.X + rFillet * Math.Sin(angMidOpp),
                        cp.Y + rFillet * Math.Cos(angMidOpp)
                    );

                    double deltaCWb = ang2b - ang1b;
                    if (deltaCWb < 0.0) deltaCWb += twoPi;

                    idx++;
                    outList.Add(new FilletArcData
                    {
                        Index = idx,
                        PairType = pairTypeLabel ?? "",
                        CenterSource = cLabel,
                        Is180Complement = true,

                        CP = cp,
                        R = rFillet,

                        Tan1 = tan1b,
                        Tan2 = tan2b,
                        Tan1From = tan1FromB,
                        Tan2From = tan2FromB,

                        AngTan1 = ang1b,
                        AngTan2 = ang2b,
                        DeltaCW_Tan1ToTan2_Increasing = deltaCWb,

                        ShortSweep = Math.PI,
                        ShortIsForward = true,

                        AngMidShort = angMidOpp,
                        MidShort = midOppWorld
                    });
                }
            }
        }

        public static string BuildLog(List<FilletArcData> list, double r, string pairLabel)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== ARC LAW TEST ===");
            sb.AppendLine($"r = {r.ToString("0.###", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Pair: {pairLabel}");
            sb.AppendLine($"Candidates: {(list != null ? list.Count : 0)}");
            sb.AppendLine();

            if (list == null || list.Count == 0)
            {
                sb.AppendLine("No candidates.");
                return sb.ToString();
            }

            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];

                sb.AppendLine($"[{d.Index}] Center = ({Fmt(d.CP.X)},{Fmt(d.CP.Y)})  {d.CenterSource}" +
                              (d.Is180Complement ? "   (180° complement)" : ""));
                sb.AppendLine($"     tan1({d.Tan1From}) = ({Fmt(d.Tan1.X)},{Fmt(d.Tan1.Y)})  ang(tan1) CW(Z+) = {FmtDeg(d.AngTan1)}");
                sb.AppendLine($"     tan2({d.Tan2From}) = ({Fmt(d.Tan2.X)},{Fmt(d.Tan2.Y)})  ang(tan2) CW(Z+) = {FmtDeg(d.AngTan2)}");
                sb.AppendLine($"     deltaCW(tan1->tan2 increasing) = {FmtDeg(d.DeltaCW_Tan1ToTan2_Increasing)}");
                sb.AppendLine($"     shortSweep(<=180) = {FmtDeg(d.ShortSweep)}  dir={(d.ShortIsForward ? "forward" : "reverse")}");
                sb.AppendLine($"     ang(midShort) CW(Z+) = {FmtDeg(d.AngMidShort)}  midShort=({Fmt(d.MidShort.X)},{Fmt(d.MidShort.Y)})");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string FmtDeg(double rad)
        {
            double deg = rad * (180.0 / Math.PI);
            return deg.ToString("0.###", CultureInfo.InvariantCulture) + "°";
        }
    }
}
