// File: Utilities/AutoTurnRegion.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CNC_Improvements_gcode_solids.Utilities
{
    internal static class AutoTurnRegion
    {
        private const int ENDTAG_COLUMN = 75;

        // Axis tokens (TURN uses X/Z)
        private static readonly Regex RxX = new Regex(@"(?i)X\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);
        private static readonly Regex RxZ = new Regex(@"(?i)Z\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled);

        // Motion tokens anywhere in line, but not as part of a bigger number
        private static readonly Regex RxG0 = new Regex(@"(?i)(?:^|[^0-9A-Z])G0(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG1 = new Regex(@"(?i)(?:^|[^0-9A-Z])G1(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG2 = new Regex(@"(?i)(?:^|[^0-9A-Z])G2(?!\d)", RegexOptions.Compiled);
        private static readonly Regex RxG3 = new Regex(@"(?i)(?:^|[^0-9A-Z])G3(?!\d)", RegexOptions.Compiled);

        private enum MotionMode { None, G0, G1, G2, G3 }

        /// <summary>
        /// TURN equivalent of AutoMillRegion.
        /// - Splits highlighted selection into TURN strokes using G0 X/Z as control-in/out boundaries.
        /// - Emits (NAME ST) ... tagged lines ... (NAME END) blocks.
        /// - Tag format: (T:A0000) and aligned using GeneralNormalizers.NormalizeInsertLineAlignEndTag(...,75).
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
                .Select(l => (l ?? "").Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (selLines.Count == 0)
            {
                userMessage = "Highlighted selection has no usable lines.";
                return false;
            }

            // Pick next available alpha from full RTB (detect highest already present, return highest+1)
            char curAlpha = FindNextAvailableAlpha(fullRtbText);

            // Split into strokes using G0 X/Z boundaries
            var strokes = SplitIntoStrokes(selLines);

            if (strokes.Count == 0)
            {
                userMessage = "No valid TURN regions could be derived from the highlighted text.";
                return false;
            }

            static char NextAlpha(char a)
            {
                a = char.ToUpperInvariant(a);
                if (a < 'A' || a > 'Z') return 'A';
                return (a == 'Z') ? 'A' : (char)(a + 1);
            }

            int successCount = 0;

            for (int i = 0; i < strokes.Count; i++)
            {
                // Only consume alpha if this stroke emits at least 1 X/Z motion line.
                string regionName = $"{baseName} ({successCount + 1})";

                var block = new List<string>();
                block.Add($"({regionName} ST)");

                int tagIndex = 0;
                int emittedXZ = 0;

                foreach (var raw in strokes[i])
                {
                    string g = StripAnyTrailingParenTag((raw ?? "").Trim());
                    if (string.IsNullOrWhiteSpace(g))
                        continue;

                    bool hasX = RxX.IsMatch(g);
                    bool hasZ = RxZ.IsMatch(g);
                    bool hasXZ = hasX || hasZ;

                    if (!hasXZ)
                        continue;

                    emittedXZ++;

                    string lineWithTag = $"{g} {MakeEndTag(curAlpha, tagIndex++)}";
                    block.Add(GeneralNormalizers.NormalizeInsertLineAlignEndTag(lineWithTag, ENDTAG_COLUMN));
                }

                if (emittedXZ <= 0)
                    continue;

                block.Add($"({regionName} END)");
                regionBlocks.Add(block);

                successCount++;
                curAlpha = NextAlpha(curAlpha);
            }

            if (regionBlocks.Count == 0)
            {
                userMessage = "No valid TURN regions could be derived from the highlighted text.";
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
        // Stroke splitting (TURN)
        // ----------------------------
        private static List<List<string>> SplitIntoStrokes(List<string> selLines)
        {
            var strokes = new List<List<string>>();

            MotionMode modal = MotionMode.None;

            // Lead-in remembered outside stroke
            string lastG0XZ = null;

            bool inStroke = false;
            bool strokeHasCutXZ = false;
            var cur = new List<string>();

            foreach (string lineRaw in selLines)
            {
                string line = StripAnyTrailingParenTag((lineRaw ?? "").Trim());
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Update modal
                if (RxG0.IsMatch(line)) modal = MotionMode.G0;
                else if (RxG1.IsMatch(line)) modal = MotionMode.G1;
                else if (RxG2.IsMatch(line)) modal = MotionMode.G2;
                else if (RxG3.IsMatch(line)) modal = MotionMode.G3;

                bool hasX = RxX.IsMatch(line);
                bool hasZ = RxZ.IsMatch(line);
                bool hasXZ = hasX || hasZ;

                // Lead-in: any G0 move with X/Z is a control boundary
                if (modal == MotionMode.G0 && hasXZ)
                {
                    // If we are already in a stroke and we have seen cut X/Z,
                    // this is control-out boundary -> finalize stroke now.
                    if (inStroke && strokeHasCutXZ)
                    {
                        FinalizeStroke(strokes, cur);
                        cur = new List<string>();
                        inStroke = false;
                        strokeHasCutXZ = false;
                    }

                    lastG0XZ = line;
                    continue;
                }

                // Identify first cut X/Z
                bool isCutXZ =
                    (modal == MotionMode.G1 || modal == MotionMode.G2 || modal == MotionMode.G3) &&
                    hasXZ;

                if (!inStroke)
                {
                    if (!isCutXZ)
                        continue;

                    inStroke = true;

                    if (!string.IsNullOrWhiteSpace(lastG0XZ))
                        cur.Add(lastG0XZ);
                }

                // Within stroke: keep only cut X/Z (G1/G2/G3 with X/Z)
                if ((modal == MotionMode.G1 || modal == MotionMode.G2 || modal == MotionMode.G3) && hasXZ)
                {
                    cur.Add(line);
                    strokeHasCutXZ = true;
                }
            }

            if (inStroke && strokeHasCutXZ)
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
        // Alpha scanning
        // ----------------------------
        private static char FindNextAvailableAlpha(string fullRtbText)
        {
            // Detect the HIGHEST existing alpha already present in the editor text,
            // regardless of leading tag kind letter (M/U/T/D/etc).
            // Matches: (X:Y0000) where X is any letter, Y is the alpha letter, #### is 4 digits.
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
            return $"(T:{alpha}{index:0000})";
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
    }
}
