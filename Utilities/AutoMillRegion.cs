using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CNC_Improvements_gcode_solids.Utilities
{
    internal static class AutoMillRegion
    {
        private const int ENDTAG_COLUMN = 75;

        // (M:A0000)
        private static readonly Regex RxEndTag = new Regex(@"\(\s*M\s*:\s*([A-Z])(\d{4})\s*\)", RegexOptions.Compiled);

        // Must match tokens with NO whitespace e.g. G0Z-29.  G1X10.982Y54.764F750.
        private static readonly Regex RxZ = new Regex(@"(?i)Z\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);
        private static readonly Regex RxX = new Regex(@"(?i)X\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);
        private static readonly Regex RxY = new Regex(@"(?i)Y\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);

        // Motion tokens anywhere in line, but not as part of a bigger number
        private static readonly Regex RxG0 = new Regex(@"(?i)(?:^|[^0-9A-Z])G0(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG1 = new Regex(@"(?i)(?:^|[^0-9A-Z])G1(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG2 = new Regex(@"(?i)(?:^|[^0-9A-Z])G2(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG3 = new Regex(@"(?i)(?:^|[^0-9A-Z])G3(?!\d)", RegexOptions.Compiled);

        private enum MotionMode { None, G0, G1, G2, G3 }

        /// <summary>
        /// Pass highlighted text in; return the region text (ST..END blocks) that can be appended to RTB.
        /// Also returns blocks as list-of-lines.
        /// </summary>
        public static bool TryBuildRegionsTextFromSelection(
    string selectedText,
    string fullRtbText,
    string baseName,
    out string regionsText,
    out List<List<string>> regionBlocks,
    out string userMessage)
        {
            regionsText = "";
            regionBlocks = new List<List<string>>();
            userMessage = "";

            selectedText = selectedText ?? "";
            fullRtbText = fullRtbText ?? "";
            baseName = (baseName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                userMessage = "No highlighted text.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(baseName))
            {
                userMessage = "No base name provided.";
                return false;
            }

            var selLines = SplitLines(selectedText)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (selLines.Count == 0)
            {
                userMessage = "Highlighted selection has no usable lines.";
                return false;
            }

            // lowest Z wins
            if (!TryFindLowestZ(selLines, out double zPlane))
            {
                userMessage = "No Z value found in highlighted text (Auto Mill needs a Z plane).";
                return false;
            }

            // pick next available M: alpha from full RTB
            char curAlpha = FindNextAvailableAlpha(fullRtbText);

            // split into strokes: end current on G0 XY; next stroke starts at next G0 XY before a feed XY cut
            var strokes = SplitIntoStrokes(selLines, zPlane);

            if (strokes.Count == 0)
            {
                userMessage = "No valid MILL regions could be derived from the highlighted text.";
                return false;
            }

            static char NextAlpha(char a)
            {
                a = char.ToUpperInvariant(a);
                if (a < 'A' || a > 'Z') return 'A';
                return (a == 'Z') ? 'A' : (char)(a + 1);
            }

            // SUCCESS counter drives (1), (2), (3) naming and alpha stepping
            int successCount = 0;

            for (int i = 0; i < strokes.Count; i++)
            {
                // IMPORTANT:
                // - We only "consume" an alpha if this stroke produces a REAL region (at least 1 XY line).
                // - This prevents dud strokes from stepping alpha.
                string regionName = $"{baseName} ({successCount + 1})";

                var block = new List<string>();
                block.Add($"({regionName} ST)");

                int tagIndex = 0;

                // First Z line, tagged + aligned by GeneralNormalizers
                {
                    string zLine = $"Z{FormatNum(zPlane)} {MakeEndTag(curAlpha, tagIndex++)}";
                    block.Add(GeneralNormalizers.NormalizeInsertLineAlignEndTag(zLine, ENDTAG_COLUMN));
                }

                int emittedXY = 0;

                foreach (var raw in strokes[i])
                {
                    string g = StripAnyTrailingParenTag(raw.Trim());
                    if (string.IsNullOrWhiteSpace(g)) continue;

                    bool hasX = RxX.IsMatch(g);
                    bool hasY = RxY.IsMatch(g);
                    bool hasXY = hasX || hasY;

                    if (!hasXY)
                        continue;

                    emittedXY++;

                    string lineWithTag = $"{g} {MakeEndTag(curAlpha, tagIndex++)}";
                    block.Add(GeneralNormalizers.NormalizeInsertLineAlignEndTag(lineWithTag, ENDTAG_COLUMN));
                }

                // If this stroke produced NO XY motion lines, it is a dud:
                // - do NOT create a region
                // - do NOT advance alpha
                if (emittedXY <= 0)
                    continue;

                block.Add($"({regionName} END)");
                regionBlocks.Add(block);

                successCount++;
                curAlpha = NextAlpha(curAlpha);
            }

            if (regionBlocks.Count == 0)
            {
                userMessage = "No valid MILL regions could be derived from the highlighted text.";
                return false;
            }

            // Join into one text blob ready to append to RTB
            var sb = new StringBuilder();
            for (int i = 0; i < regionBlocks.Count; i++)
            {
                foreach (var line in regionBlocks[i])
                    sb.AppendLine(line);
                sb.AppendLine();
            }

            regionsText = sb.ToString().TrimEnd();
            return true;
        }


        // ----------------------------
        // Stroke splitting
        // ----------------------------

        private static List<List<string>> SplitIntoStrokes(List<string> selLines, double zPlane)
        {
            var strokes = new List<List<string>>();

            MotionMode modal = MotionMode.None;

            // Lead-in remembered outside stroke
            string lastG0XY = null;
            string lastPlungeToPlane = null;

            bool inStroke = false;
            bool strokeHasCutXY = false;
            var cur = new List<string>();

            foreach (string lineRaw in selLines)
            {
                string line = StripAnyTrailingParenTag(lineRaw.Trim());
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Update modal
                if (RxG0.IsMatch(line)) modal = MotionMode.G0;
                else if (RxG1.IsMatch(line)) modal = MotionMode.G1;
                else if (RxG2.IsMatch(line)) modal = MotionMode.G2;
                else if (RxG3.IsMatch(line)) modal = MotionMode.G3;

                bool hasX = RxX.IsMatch(line);
                bool hasY = RxY.IsMatch(line);
                bool hasXY = hasX || hasY;

                bool hasZ = TryParseZ(line, out double zVal);

                // Lead-in: G0 XY
                if (modal == MotionMode.G0 && hasXY)
                {
                    // If we are already in a stroke and we have seen cut XY,
                    // this is control-out boundary -> finalize stroke now.
                    if (inStroke && strokeHasCutXY)
                    {
                        FinalizeStroke(strokes, cur);
                        cur = new List<string>();
                        inStroke = false;
                        strokeHasCutXY = false;
                        lastPlungeToPlane = null;
                    }

                    lastG0XY = line;
                    continue;
                }

                // Lead-in: plunge-to-plane (feed Z == plane, no XY)
                if (modal == MotionMode.G1 && !hasXY && hasZ && zVal == zPlane)
                {
                    lastPlungeToPlane = line;
                    continue;
                }

                // Identify first “cut XY on plane”
                bool isCutXYOnPlane =
                    (modal == MotionMode.G1 || modal == MotionMode.G2 || modal == MotionMode.G3) &&
                    hasXY &&
                    (!hasZ || zVal == zPlane);

                if (!inStroke)
                {
                    if (!isCutXYOnPlane)
                        continue;

                    inStroke = true;

                    if (!string.IsNullOrWhiteSpace(lastG0XY))
                        cur.Add(lastG0XY);

                    if (!string.IsNullOrWhiteSpace(lastPlungeToPlane))
                        cur.Add(lastPlungeToPlane);
                }

                // Within stroke: keep only feed/cut XY lines (G1/G2/G3 with XY)
                if ((modal == MotionMode.G1 || modal == MotionMode.G2 || modal == MotionMode.G3) && hasXY)
                {
                    cur.Add(line);
                    strokeHasCutXY = true;
                }
            }

            if (inStroke && strokeHasCutXY)
                FinalizeStroke(strokes, cur);

            return strokes;
        }

        private static void FinalizeStroke(List<List<string>> strokes, List<string> cur)
        {
            if (cur == null || cur.Count == 0)
                return;

            // Dedup consecutive identical lines
            var cleaned = new List<string>();
            string prev = null;
            foreach (var s in cur)
            {
                if (prev != null && string.Equals(prev, s, StringComparison.OrdinalIgnoreCase))
                    continue;
                cleaned.Add(s);
                prev = s;
            }

            if (cleaned.Count > 0)
                strokes.Add(cleaned);
        }

        // ----------------------------
        // Lowest Z + alpha
        // ----------------------------

        private static bool TryFindLowestZ(List<string> lines, out double lowest)
        {
            lowest = 0;
            bool found = false;

            foreach (var l in lines)
            {
                if (TryParseZ(l, out double z))
                {
                    if (!found || z < lowest)
                    {
                        lowest = z;
                        found = true;
                    }
                }
            }

            return found;
        }

        private static bool TryParseZ(string line, out double z)
        {
            z = 0;
            var m = RxZ.Match(line ?? "");
            if (!m.Success) return false;

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        private static char FindNextAvailableAlpha(string fullRtbText)
        {
            // We must detect the HIGHEST existing alpha already present in the editor text,
            // regardless of the leading tag kind letter (M/U/T/D/etc).
            // Examples we must handle:
            //   (M:A0000)
            //   (U:B0887)
            //   (T:C0123)
            //
            // Then we return "highest + 1" (wrapping A..Z back to A).

            char highestExistingAlpha = (char)('A' - 1);

            if (string.IsNullOrEmpty(fullRtbText))
                return 'A';

            // Match "(X:Y####)" where X and Y are letters and #### is 4 digits.
            // Group 1 = kind letter (ignored), Group 2 = alpha letter we care about.
            var rxAnyEndTag = new Regex(@"\(\s*[A-Z]\s*:\s*([A-Z])(\d{4})\s*\)", RegexOptions.Compiled);

            foreach (Match m in rxAnyEndTag.Matches(fullRtbText))
            {
                if (!m.Success || m.Groups.Count < 2)
                    continue;

                char a = m.Groups[1].Value[0]; // the alpha letter after the colon

                if (a >= 'A' && a <= 'Z' && a > highestExistingAlpha)
                    highestExistingAlpha = a;
            }

            char next = (char)(highestExistingAlpha + 1);

            // Wrap A..Z
            if (next < 'A' || next > 'Z')
                next = 'A';

            return next;
        }


        // ----------------------------
        // Formatting
        // ----------------------------

        private static List<string> SplitLines(string text)
        {
            return (text ?? "")
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n')
                .ToList();
        }

        private static string MakeEndTag(char alpha, int index)
        {
            return $"(M:{alpha}{index:0000})";
        }

        private static string StripAnyTrailingParenTag(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";

            int lastParenClose = line.LastIndexOf(')');
            if (lastParenClose < 0) return line;

            int lastParenOpen = line.LastIndexOf('(', lastParenClose);
            if (lastParenOpen < 0) return line;

            string after = line.Substring(lastParenClose + 1);
            if (!string.IsNullOrWhiteSpace(after)) return line;

            return line.Substring(0, lastParenOpen).TrimEnd();
        }

        private static string FormatNum(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 1e-12)
                return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
