// File: Utilities/FaptDrill.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;

namespace CNC_Improvements_gcode_solids.Utilities
{
    internal static class FaptDrill
    {
        // Show unsupported-pattern popup only once per (G-code + name) so we don't spam.
        private static readonly HashSet<string> _unsupportedPopupShown =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal sealed class DrillPattern
        {
            public string Name = "";

            public int CycleCode;    // 1000, 1001, ...
            public int PatternCode;  // 1210..1219, etc

            public double ZHoleTop;  // B
            public double ZDepth;    // B + L

            public double Feed;      // F (optional)

            public double CenterX;   // H
            public double CenterY;   // V

            public double Radius;        // R (bolt circle)
            public double StartAngleDeg; // A

            // For G1216: C = pitch angle
            // For G1215: C = hole count (integer)
            // For G1214: dummy mapping (see below)
            public double PitchAngleDeg;
            public int HoleCount;

            public List<(double X, double Y)> Points = new();
        }

        internal static List<List<string>> BuildFaptDrillRegions(List<string> allLines)
        {
            var regions = new List<List<string>>();
            if (allLines == null || allLines.Count == 0)
                return regions;

            string currentG100x = null;

            for (int i = 0; i < allLines.Count; i++)
            {
                string raw = allLines[i] ?? "";
                string t = StripLeadingLineNumberPrefix(raw).Trim();
                if (t.Length == 0) continue;

                // Make detection robust: contains "(G100" / "(G121", not startswith.
                if (ContainsIgnoreCase(t, "(G100"))
                {
                    currentG100x = t;
                    continue;
                }

                if (ContainsIgnoreCase(t, "(G121"))
                {
                    // name MUST be appended after the ')' on the pattern line
                    string nm = FaptTurn.ExtractFaptRegionName(t);
                    if (string.IsNullOrWhiteSpace(nm))
                        continue;

                    // For selection list we still keep the modal line with it,
                    // because your picker + converter expects the region to carry its cycle line.
                    if (string.IsNullOrWhiteSpace(currentG100x))
                        continue;

                    regions.Add(new List<string> { currentG100x, t });
                }
            }

            return regions;
        }

        internal static bool TryConvertRegionToDrillPattern(
            List<string> drillRegionTwoLines,
            out DrillPattern pattern,
            out string error)
        {
            pattern = null;
            error = "";

            if (drillRegionTwoLines == null || drillRegionTwoLines.Count < 2)
            {
                error = "DRILL region must contain [G100x, G121x].";
                return false;
            }

            string g100xLine = StripLeadingLineNumberPrefix(drillRegionTwoLines[0] ?? "").Trim();
            string g121xLine = StripLeadingLineNumberPrefix(drillRegionTwoLines[1] ?? "").Trim();

            string name = FaptTurn.ExtractFaptRegionName(g121xLine);
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "DRILL pattern line has no appended name.";
                return false;
            }
            name = name.Trim();

            if (!TryGetParenLeadingGCodeNumber(g100xLine, "G100", out int cycleCode))
            {
                error = "Expected (G100x...) modal cycle line.";
                return false;
            }

            if (!TryGetParenLeadingGCodeNumber(g121xLine, "G121", out int patternCode))
            {
                error = "Expected (G121x...) pattern line.";
                return false;
            }

            // Need L from any G100x (depth relative to B)
            if (!TryParseParenTokenDouble(g100xLine, 'L', out double L))
            {
                error = "G100x missing L (depth).";
                return false;
            }

            // Feed optional
            double feed = 0.0;
            TryParseParenTokenDouble(g100xLine, 'F', out feed);

            // Need B from any G121x (hole top)
            if (!TryParseParenTokenDouble(g121xLine, 'B', out double B))
            {
                error = "G121x missing B (hole top).";
                return false;
            }

            // Common XY reference tokens (often H,V)
            double H = 0.0, V = 0.0;
            TryParseParenTokenDouble(g121xLine, 'H', out H);
            TryParseParenTokenDouble(g121xLine, 'V', out V);

            var pat = new DrillPattern
            {
                Name = name,
                CycleCode = cycleCode,
                PatternCode = patternCode,
                ZHoleTop = B,
                ZDepth = B + L,
                Feed = feed,
                CenterX = H,
                CenterY = V
            };

            // We are scoping to G1210..G1219 for now, but only decoding known ones.
            if (patternCode < 1210 || patternCode > 1219)
            {
                error = $"DRILL pattern not yet supported: G{patternCode}  Name={name}";
                ShowUnsupportedPopupOnce(error, patternCode, name);
                return false;
            }

            // ---- Known pattern decoding ----
            switch (patternCode)
            {
                case 1216:
                    {
                        // (G1216 B.. H.. V.. R.. A.. C.. D..)
                        if (!TryParseParenTokenDouble(g121xLine, 'R', out double R))
                        {
                            error = "G1216 missing R (radius).";
                            return false;
                        }
                        if (!TryParseParenTokenDouble(g121xLine, 'A', out double A)) A = 0.0;

                        // For 1216: C = pitch angle
                        if (!TryParseParenTokenDouble(g121xLine, 'C', out double pitchAngleDeg))
                        {
                            error = "G1216 missing C (pitch angle).";
                            return false;
                        }

                        if (!TryParseParenTokenInt(g121xLine, 'D', out int holeCount))
                        {
                            error = "G1216 missing D (hole count).";
                            return false;
                        }

                        pat.Radius = R;
                        pat.StartAngleDeg = A;
                        pat.PitchAngleDeg = pitchAngleDeg;
                        pat.HoleCount = holeCount;

                        pat.Points = BuildBoltCirclePoints(H, V, R, A, pitchAngleDeg, holeCount);
                        pattern = pat;
                        return true;
                    }

                case 1215:
                    {
                        // Example:
                        // (G1215 ... R157.16 A0. C6.)BOLTUPHOLES
                        // For 1215: C = HOLE COUNT (integer), no D.
                        if (!TryParseParenTokenDouble(g121xLine, 'R', out double R))
                        {
                            error = "G1215 missing R (radius).";
                            return false;
                        }
                        if (!TryParseParenTokenDouble(g121xLine, 'A', out double A)) A = 0.0;

                        if (!TryParseParenTokenInt(g121xLine, 'C', out int holeCount))
                        {
                            error = "G1215 missing C (hole count).";
                            return false;
                        }

                        if (holeCount <= 0)
                        {
                            error = "G1215 invalid hole count (C).";
                            return false;
                        }

                        double pitchAngleDeg = 360.0 / holeCount;

                        pat.Radius = R;
                        pat.StartAngleDeg = A;
                        pat.PitchAngleDeg = pitchAngleDeg;
                        pat.HoleCount = holeCount;

                        pat.Points = BuildBoltCirclePoints(H, V, R, A, pitchAngleDeg, holeCount);
                        pattern = pat;
                        return true;
                    }

                case 1214:
                    {
                        // DUMMY / TODO (assumed line of holes) - not verified
                        // H,V = start point
                        // A   = direction angle (deg)
                        // C   = pitch (mm)
                        // D   = number of holes
                        if (!TryParseParenTokenDouble(g121xLine, 'A', out double A)) A = 0.0;

                        if (!TryParseParenTokenDouble(g121xLine, 'C', out double pitch))
                        {
                            error = "G1214 missing C (pitch) [dummy mapping].";
                            return false;
                        }

                        if (!TryParseParenTokenInt(g121xLine, 'D', out int count))
                        {
                            error = "G1214 missing D (count) [dummy mapping].";
                            return false;
                        }

                        pat.StartAngleDeg = A;
                        pat.PitchAngleDeg = pitch;
                        pat.HoleCount = count;

                        pat.Points = BuildLinePatternPoints(H, V, A, pitch, count);
                        pattern = pat;
                        return true;
                    }

                default:
                    {
                        error = $"DRILL pattern not yet supported: G{patternCode}  Name={name}";
                        ShowUnsupportedPopupOnce(error, patternCode, name);
                        return false;
                    }
            }
        }

        private static void ShowUnsupportedPopupOnce(string msg, int patternCode, string name)
        {
            // Always independent of log window. Caller already logs the same message when log is on.
            string key = "G" + patternCode.ToString(CultureInfo.InvariantCulture) + "|" + (name ?? "");
            lock (_unsupportedPopupShown)
            {
                if (_unsupportedPopupShown.Contains(key))
                    return;

                _unsupportedPopupShown.Add(key);
            }

            try
            {
                MessageBox.Show(
                    msg + "\n\nThis drill pattern was skipped. Other selected patterns will continue.",
                    "FAPT Drill",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch
            {
                // never crash conversion
            }
        }

        private static string StripLeadingLineNumberPrefix(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return s ?? "";

            // Remove optional leading "123:" with optional spaces
            // e.g. "53: (G1216...)" -> "(G1216...)"
            return Regex.Replace(s, @"^\s*\d+\s*:\s*", "", RegexOptions.CultureInvariant);
        }

        private static bool ContainsIgnoreCase(string s, string needle)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(needle))
                return false;

            return s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<(double X, double Y)> BuildBoltCirclePoints(
            double cx,
            double cy,
            double radius,
            double startAngleDeg,
            double pitchAngleDeg,
            int holeCount)
        {
            var pts = new List<(double X, double Y)>(Math.Max(0, holeCount));
            if (holeCount <= 0)
                return pts;

            for (int i = 0; i < holeCount; i++)
            {
                double angDeg = startAngleDeg + pitchAngleDeg * i;
                double angRad = angDeg * Math.PI / 180.0;
                double x = cx + radius * Math.Cos(angRad);
                double y = cy + radius * Math.Sin(angRad);
                pts.Add((x, y));
            }

            return pts;
        }

        private static List<(double X, double Y)> BuildLinePatternPoints(
            double x0,
            double y0,
            double directionAngleDeg,
            double pitch,
            int count)
        {
            var pts = new List<(double X, double Y)>(Math.Max(0, count));
            if (count <= 0)
                return pts;

            double angRad = directionAngleDeg * Math.PI / 180.0;
            double ux = Math.Cos(angRad);
            double uy = Math.Sin(angRad);

            for (int i = 0; i < count; i++)
            {
                double x = x0 + ux * pitch * i;
                double y = y0 + uy * pitch * i;
                pts.Add((x, y));
            }

            return pts;
        }

        private static bool TryGetParenLeadingGCodeNumber(string line, string prefix, out int codeNumber)
        {
            codeNumber = 0;
            if (string.IsNullOrWhiteSpace(line)) return false;

            int a = line.IndexOf('(');
            int b = line.IndexOf(')');
            if (a < 0 || b <= a) return false;

            string inside = line.Substring(a + 1, b - a - 1).Trim();
            if (!inside.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            int idx = prefix.Length;
            int startDigits = idx;
            while (idx < inside.Length && char.IsDigit(inside[idx]))
                idx++;

            if (idx == startDigits)
                return false;

            string suffixDigits = inside.Substring(startDigits, idx - startDigits);

            // prefix like "G121" -> base "121", suffix "6" => "1216"
            string baseDigits = prefix.Substring(1);
            if (!int.TryParse(baseDigits + suffixDigits, NumberStyles.Integer, CultureInfo.InvariantCulture, out codeNumber))
                return false;

            return true;
        }

        private static bool TryParseParenTokenDouble(string line, char key, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(line)) return false;

            int a = line.IndexOf('(');
            int b = line.IndexOf(')');
            if (a < 0 || b <= a) return false;

            string inside = line.Substring(a + 1, b - a - 1);

            // Tokens are packed: "...G1216B0.H0.V0.R157.16A-32.C120.D3."
            // Using \b breaks (6B is not a word boundary). So don't use \b.
            var m = Regex.Match(
                inside,
                $@"(?<![A-Z]){Regex.Escape(key.ToString())}\s*([-+]?\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!m.Success) return false;

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseParenTokenInt(string line, char key, out int value)
        {
            value = 0;
            if (!TryParseParenTokenDouble(line, key, out double d))
                return false;

            value = (int)Math.Round(d);
            return true;
        }
    }
}
