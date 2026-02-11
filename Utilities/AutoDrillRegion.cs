using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CNC_Improvements_gcode_solids.Utilities
{
    internal static class AutoDrillRegion
    {
        private const int ENDTAG_COLUMN = 75;

        private static readonly Regex RxZ = new Regex(@"(?i)Z\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);
        private static readonly Regex RxR = new Regex(@"(?i)R\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);
        private static readonly Regex RxX = new Regex(@"(?i)X\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);
        private static readonly Regex RxY = new Regex(@"(?i)Y\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);

        // Motion tokens anywhere in line, but not as part of a bigger number
        private static readonly Regex RxG0 = new Regex(@"(?i)(?:^|[^0-9A-Z])G0(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG1 = new Regex(@"(?i)(?:^|[^0-9A-Z])G1(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG2 = new Regex(@"(?i)(?:^|[^0-9A-Z])G2(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG3 = new Regex(@"(?i)(?:^|[^0-9A-Z])G3(?!\d)", RegexOptions.Compiled);

        // Canned cycle start: G81..G89
        private static readonly Regex RxG8x = new Regex(@"(?i)(?:^|[^0-9A-Z])G8[1-9](?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG80 = new Regex(@"(?i)(?:^|[^0-9A-Z])G80(?!\d)", RegexOptions.Compiled);

        private sealed class CycleGroup
        {
            public double DepthZ;
            public double TopZ;
            public List<(double X, double Y)> Points = new List<(double X, double Y)>();
        }

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

            // pick next available alpha from full RTB (any kind letter)
            char curAlpha = FindNextAvailableAlpha(fullRtbText);

            // Extract drill cycle groups (each group = one G81..G89 series)
            var groups = ExtractCycleGroups(selLines);

            if (groups.Count == 0)
            {
                userMessage = "No drill cycles found (G81..G89).";
                return false;
            }

            static char NextAlpha(char a)
            {
                a = char.ToUpperInvariant(a);
                if (a < 'A' || a > 'Z') return 'A';
                return (a == 'Z') ? 'A' : (char)(a + 1);
            }

            int successCount = 0;

            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g == null || g.Points == null || g.Points.Count == 0)
                    continue;

                string regionName = $"{baseName} ({successCount + 1})";

                var block = new List<string>();
                block.Add($"({regionName} ST)");

                int tagIndex = 0;

                // Canonical DRILL lines:
                //  0: D Z<depth>
                //  1: T Z<top>
                //  2..:  X..Y..
                {
                    string dLine = $"D Z{FormatNum(g.DepthZ)} {MakeEndTag(curAlpha, tagIndex++)}";
                    block.Add(GeneralNormalizers.NormalizeInsertLineAlignEndTag(dLine, ENDTAG_COLUMN));
                }
                {
                    string tLine = $"T Z{FormatNum(g.TopZ)} {MakeEndTag(curAlpha, tagIndex++)}";
                    block.Add(GeneralNormalizers.NormalizeInsertLineAlignEndTag(tLine, ENDTAG_COLUMN));
                }

                int emittedPts = 0;

                for (int p = 0; p < g.Points.Count; p++)
                {
                    var pt = g.Points[p];
                    string line = $" X{FormatNum(pt.X)}Y{FormatNum(pt.Y)} {MakeEndTag(curAlpha, tagIndex++)}";
                    block.Add(GeneralNormalizers.NormalizeInsertLineAlignEndTag(line, ENDTAG_COLUMN));
                    emittedPts++;
                }

                if (emittedPts <= 0)
                    continue;

                block.Add($"({regionName} END)");
                regionBlocks.Add(block);

                successCount++;
                curAlpha = NextAlpha(curAlpha);
            }

            if (regionBlocks.Count == 0)
            {
                userMessage = "No valid DRILL regions could be derived from the highlighted text.";
                return false;
            }

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
        // Cycle extraction
        // ----------------------------

        private static List<CycleGroup> ExtractCycleGroups(List<string> selLines)
        {
            var groups = new List<CycleGroup>();

            double? modalX = null;
            double? modalY = null;

            double? lastRapidZ = null;

            bool inCycle = false;
            double curDepth = 0;
            double curTop = 0;
            var curPts = new List<(double X, double Y)>();

            void FinalizeCycleIfAny()
            {
                if (!inCycle) return;

                if (curPts.Count > 0)
                {
                    groups.Add(new CycleGroup
                    {
                        DepthZ = curDepth,
                        TopZ = curTop,
                        Points = new List<(double X, double Y)>(curPts)
                    });
                }

                inCycle = false;
                curPts.Clear();
            }

            for (int i = 0; i < selLines.Count; i++)
            {
                string raw = selLines[i] ?? "";
                string line = StripAnyTrailingParenTag(raw.Trim());
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Update modals from any X/Y on any line
                if (TryParseX(line, out double xVal)) modalX = xVal;
                if (TryParseY(line, out double yVal)) modalY = yVal;

                bool isG0 = RxG0.IsMatch(line);
                bool isG1 = RxG1.IsMatch(line);
                bool isG2 = RxG2.IsMatch(line);
                bool isG3 = RxG3.IsMatch(line);
                bool isMotionBoundary = (isG0 || isG1 || isG2 || isG3);

                // Track last rapid Z (used as fallback top Z if no R)
                if (isG0 && TryParseZ(line, out double zRapid))
                    lastRapidZ = zRapid;

                bool isG80 = RxG80.IsMatch(line);
                bool isCycleStart = RxG8x.IsMatch(line);

                // Termination: G80 ends active cycle
                if (inCycle && isG80)
                {
                    FinalizeCycleIfAny();
                    continue;
                }

                // Termination: any motion boundary (G0/G1/G2/G3) ends active cycle
                // (except the cycle start line itself, which is handled below)
                if (inCycle && !isCycleStart && isMotionBoundary)
                {
                    FinalizeCycleIfAny();
                    // continue processing this line as normal (modals already updated)
                    continue;
                }

                // New cycle start:
                if (isCycleStart)
                {
                    // If we were already in a cycle, finalize it first
                    if (inCycle)
                        FinalizeCycleIfAny();

                    // Depth MUST come from the cycle line Z
                    if (!TryParseZ(line, out double zDepth))
                    {
                        // No depth -> can't use this cycle
                        inCycle = false;
                        curPts.Clear();
                        continue;
                    }

                    curDepth = zDepth;

                    // Top: prefer R from cycle line, else last rapid Z, else 0
                    if (TryParseR(line, out double rTop))
                        curTop = rTop;
                    else if (lastRapidZ.HasValue)
                        curTop = lastRapidZ.Value;
                    else
                        curTop = 0;

                    inCycle = true;

                    // First point: if cycle line has X/Y use them; else use modal if available
                    double? px = null, py = null;

                    if (TryParseX(line, out double x0)) px = x0;
                    else if (modalX.HasValue) px = modalX.Value;

                    if (TryParseY(line, out double y0)) py = y0;
                    else if (modalY.HasValue) py = modalY.Value;

                    if (px.HasValue && py.HasValue)
                        curPts.Add((px.Value, py.Value));

                    continue;
                }

                // While in cycle: collect points from any line that has X or Y (modal fill)
                if (inCycle)
                {
                    bool hasX = RxX.IsMatch(line);
                    bool hasY = RxY.IsMatch(line);
                    if (hasX || hasY)
                    {
                        if (!modalX.HasValue || !modalY.HasValue)
                            continue;

                        curPts.Add((modalX.Value, modalY.Value));
                    }
                }
            }

            // finalize at end
            if (inCycle)
                FinalizeCycleIfAny();

            return groups;
        }

        // ----------------------------
        // Alpha scan (same rule as AutoMillRegion)
        // ----------------------------

        private static char FindNextAvailableAlpha(string fullRtbText)
        {
            char highestExistingAlpha = (char)('A' - 1);

            if (string.IsNullOrEmpty(fullRtbText))
                return 'A';

            var rxAnyEndTag = new Regex(@"\(\s*[A-Z]\s*:\s*([A-Z])(\d{4})\s*\)", RegexOptions.Compiled);

            foreach (Match m in rxAnyEndTag.Matches(fullRtbText))
            {
                if (!m.Success || m.Groups.Count < 2)
                    continue;

                char a = m.Groups[1].Value[0];

                if (a >= 'A' && a <= 'Z' && a > highestExistingAlpha)
                    highestExistingAlpha = a;
            }

            char next = (char)(highestExistingAlpha + 1);

            if (next < 'A' || next > 'Z')
                next = 'A';

            return next;
        }

        // ----------------------------
        // Formatting / parsing helpers
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
            // NO spaces inside tag
            return $"(D:{alpha}{index:0000})";
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

        private static bool TryParseZ(string line, out double z)
        {
            z = 0;
            var m = RxZ.Match(line ?? "");
            if (!m.Success) return false;

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        private static bool TryParseR(string line, out double r)
        {
            r = 0;
            var m = RxR.Match(line ?? "");
            if (!m.Success) return false;

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out r);
        }

        private static bool TryParseX(string line, out double x)
        {
            x = 0;
            var m = RxX.Match(line ?? "");
            if (!m.Success) return false;

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x);
        }

        private static bool TryParseY(string line, out double y)
        {
            y = 0;
            var m = RxY.Match(line ?? "");
            if (!m.Success) return false;

            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        private static string FormatNum(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 1e-12)
                return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
