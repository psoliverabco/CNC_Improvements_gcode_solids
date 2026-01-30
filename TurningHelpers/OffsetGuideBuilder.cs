using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace CNC_Improvements_gcode_solids.TurningHelpers
{
    /// <summary>
    /// OffsetGuideBuilder follows this strict architecture:
    ///
    /// 1) Parse the profile into ProfileSegments
    /// 2) Build a list of segment pairs → ("L,L", "L,CW", "CW,L", ... )
    /// 3) Walk the pair list with a switch block
    /// 4) For each pair type, call a dedicated evaluator method
    ///      - each Evaluate_* has its OWN code path
    ///      - Evaluate_* calls only the small shared helpers (vector builders / angle / tangency / tool-side)
    /// 5) Return:
    ///      - segmentPairs
    ///      - cornerGuide
    /// </summary>
    internal class OffsetGuideBuilder
    {
        private readonly List<string> _profileShape;
        private readonly string _toolUsageUpper;

        public OffsetGuideBuilder(List<string> profileShape, string toolUsage)
        {
            _profileShape = profileShape ?? throw new ArgumentNullException(nameof(profileShape));
            _toolUsageUpper = (toolUsage ?? "OFF").Trim().ToUpperInvariant();
        }

        public (List<string> segmentPairs, List<string> cornerGuide) BuildGuide()
        {
            var segs = ParseProfileSegments(_profileShape);
            var pairList = BuildSegmentPairList(segs);
            var guideList = BuildCornerGuide(segs);
            return (pairList, guideList);
        }

        // ================================================================
        // 1) BUILD SEGMENT PAIR LIST
        // ================================================================
        private List<string> BuildSegmentPairList(List<ProfileSegment> segs)
        {
            var list = new List<string>();
            list.Add("=== SEGMENT PAIRS ===");

            if (segs.Count < 2)
            {
                list.Add("No corners.");
                return list;
            }

            for (int i = 0; i < segs.Count - 1; i++)
            {
                string t1 = OffsetGuideBuilderHelpers.GetSegmentType(segs[i]);
                string t2 = OffsetGuideBuilderHelpers.GetSegmentType(segs[i + 1]);
                list.Add($"{i:00}: {t1},{t2}");
            }

            return list;
        }

        // ================================================================
        // 2) CORNER GUIDE GENERATION (switch on pair types)
        // ================================================================
        private List<string> BuildCornerGuide(List<ProfileSegment> segs)
        {
            var outList = new List<string>();
            outList.Add("=== CORNER GUIDE ===");

            if (_toolUsageUpper == "OFF")
            {
                outList.Add("no comp for OFF required");
                return outList;
            }

            int offsetDir = (_toolUsageUpper == "LEFT") ? +1 :
                            (_toolUsageUpper == "RIGHT") ? -1 : 0;

            if (offsetDir == 0)
            {
                outList.Add($"unknown tool usage '{_toolUsageUpper}', treating as OFF");
                return outList;
            }

            double tangentTolDeg = Math.Abs(Settings.Default.TangentAngTol);
            if (tangentTolDeg < 1e-6)
                tangentTolDeg = 1e-6;

            if (segs.Count < 2)
            {
                outList.Add("No corners.");
                return outList;
            }

            int count = segs.Count - 1;

            for (int i = 0; i < count; i++)
            {
                var s1 = segs[i];
                var s2 = segs[i + 1];

                string t1 = OffsetGuideBuilderHelpers.GetSegmentType(s1);
                string t2 = OffsetGuideBuilderHelpers.GetSegmentType(s2);
                string key = $"{t1},{t2}";

                switch (key)
                {
                    case "L,L":
                        outList.Add(Evaluate_LL(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    case "L,CW":
                        outList.Add(Evaluate_L_CW(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    case "L,CCW":
                        outList.Add(Evaluate_L_CCW(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    case "CW,L":
                        outList.Add(Evaluate_CW_L(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    case "CCW,L":
                        outList.Add(Evaluate_CCW_L(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    case "CW,CW":
                        outList.Add(Evaluate_CW_CW(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    case "CW,CCW":
                        outList.Add(Evaluate_CW_CCW(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    case "CCW,CW":
                        outList.Add(Evaluate_CCW_CW(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    case "CCW,CCW":
                        outList.Add(Evaluate_CCW_CCW(i, s1, s2, offsetDir, tangentTolDeg));
                        break;

                    default:
                        outList.Add($"{i:00}: {key}  // guide not yet calculated for {key}");
                        break;
                }
            }

            return outList;
        }

        // ================================================================
        // EVALUATORS (each has its own explicit code path)
        // ================================================================
        private string Evaluate_LL(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "L,L";

            if (!OffsetGuideBuilderHelpers.Seg1LineVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 LINE vector";

            if (!OffsetGuideBuilderHelpers.Seg2LineVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 LINE vector";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        private string Evaluate_L_CW(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "L,CW";

            if (!OffsetGuideBuilderHelpers.Seg1LineVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 LINE vector";

            if (!OffsetGuideBuilderHelpers.Seg2ArcVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 ARC vector (missing appended arc vectors?)";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        private string Evaluate_L_CCW(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "L,CCW";

            if (!OffsetGuideBuilderHelpers.Seg1LineVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 LINE vector";

            if (!OffsetGuideBuilderHelpers.Seg2ArcVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 ARC vector (missing appended arc vectors?)";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        private string Evaluate_CW_L(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "CW,L";

            if (!OffsetGuideBuilderHelpers.Seg1ArcVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 ARC vector (missing appended arc vectors?)";

            if (!OffsetGuideBuilderHelpers.Seg2LineVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 LINE vector";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        private string Evaluate_CCW_L(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "CCW,L";

            if (!OffsetGuideBuilderHelpers.Seg1ArcVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 ARC vector (missing appended arc vectors?)";

            if (!OffsetGuideBuilderHelpers.Seg2LineVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 LINE vector";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        private string Evaluate_CW_CW(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "CW,CW";

            if (!OffsetGuideBuilderHelpers.Seg1ArcVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 ARC vector (missing appended arc vectors?)";

            if (!OffsetGuideBuilderHelpers.Seg2ArcVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 ARC vector (missing appended arc vectors?)";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        private string Evaluate_CW_CCW(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "CW,CCW";

            if (!OffsetGuideBuilderHelpers.Seg1ArcVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 ARC vector (missing appended arc vectors?)";

            if (!OffsetGuideBuilderHelpers.Seg2ArcVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 ARC vector (missing appended arc vectors?)";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        private string Evaluate_CCW_CW(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "CCW,CW";

            if (!OffsetGuideBuilderHelpers.Seg1ArcVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 ARC vector (missing appended arc vectors?)";

            if (!OffsetGuideBuilderHelpers.Seg2ArcVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 ARC vector (missing appended arc vectors?)";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        private string Evaluate_CCW_CCW(int index, ProfileSegment s1, ProfileSegment s2, int offsetDir, double tolDeg)
        {
            const string pairKey = "CCW,CCW";

            if (!OffsetGuideBuilderHelpers.Seg1ArcVector(s1, out double v1x, out double v1z))
                return $"{index:00}: {pairKey}  // cannot build seg1 ARC vector (missing appended arc vectors?)";

            if (!OffsetGuideBuilderHelpers.Seg2ArcVector(s2, out double v2x, out double v2z))
                return $"{index:00}: {pairKey}  // cannot build seg2 ARC vector (missing appended arc vectors?)";

            OffsetGuideBuilderHelpers.Seg1Seg2Angles(v1x, v1z, v2x, v2z,
                out double deltaDeg, out double innerDeg, out double outerDeg);

            if (OffsetGuideBuilderHelpers.Seg1Seg2TanTest(deltaDeg, tolDeg))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:00}: {1}  TAN (delta = {2:0.###}°, tol = {3:0.###}°)",
                    index, pairKey, deltaDeg, tolDeg);
            }

            OffsetGuideBuilderHelpers.GetCornerFromToolSide(v1x, v1z, v2x, v2z, offsetDir,
                out double crossTurn, out bool chooseOuter);

            string kind = chooseOuter ? "OUTER" : "INNER";
            double val = chooseOuter ? outerDeg : innerDeg;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}: {1}  TOOL_SIDE={2}  ANGLE={3} {4:0.###}°  (inner={5:0.###}°, outer={6:0.###}°, delta={7:0.###}°, cross={8:+0.###;-0.###;0})",
                index, pairKey, _toolUsageUpper, kind, val, innerDeg, outerDeg, deltaDeg, crossTurn);
        }

        // ================================================================
        // PARSING SEGMENTS
        // ================================================================
        private List<ProfileSegment> ParseProfileSegments(List<string> profileShape)
        {
            var segments = new List<ProfileSegment>();
            if (profileShape == null)
                return segments;

            foreach (string raw in profileShape)
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
                    double x1 = OffsetGuideBuilderHelpers.ParseInv(parts[1]);
                    double z1 = OffsetGuideBuilderHelpers.ParseInv(parts[2]);
                    double x2 = OffsetGuideBuilderHelpers.ParseInv(parts[3]);
                    double z2 = OffsetGuideBuilderHelpers.ParseInv(parts[4]);

                    segments.Add(new ProfileSegment
                    {
                        Command = "LINE",
                        P1 = new Point(x1, z1),
                        Pm = new Point((x1 + x2) * 0.5, (z1 + z2) * 0.5),
                        P2 = new Point(x2, z2)
                    });
                }
                else if ((cmd == "ARC3_CW" || cmd == "ARC3_CCW") && parts.Length >= 7)
                {
                    double x1 = OffsetGuideBuilderHelpers.ParseInv(parts[1]);
                    double z1 = OffsetGuideBuilderHelpers.ParseInv(parts[2]);
                    double xm = OffsetGuideBuilderHelpers.ParseInv(parts[3]);
                    double zm = OffsetGuideBuilderHelpers.ParseInv(parts[4]);
                    double x2 = OffsetGuideBuilderHelpers.ParseInv(parts[5]);
                    double z2 = OffsetGuideBuilderHelpers.ParseInv(parts[6]);

                    var arc = new ProfileSegment
                    {
                        Command = cmd,
                        P1 = new Point(x1, z1),
                        Pm = new Point(xm, zm),
                        P2 = new Point(x2, z2)
                    };

                    // IMPORTANT: use ONLY appended fields if present (no 3-point circle solve here)
                    OffsetGuideBuilderHelpers.ApplyArcExtrasFromTokens(arc, parts);

                    segments.Add(arc);
                }
            }

            return segments;
        }
    }
}
