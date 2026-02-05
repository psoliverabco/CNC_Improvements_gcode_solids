// File: Utilities/TextSearching.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// SINGLE source of truth for ALL G-code text searching in the app (Turn/Mill/Drill).
    ///
    /// NormalizeTextLineToGcodeAndEndTag rules:
    ///  1) remove optional leading "1234:" (with optional spaces)
    ///  2) remove FIRST leading anchor "#...#" block (ONLY if present at line start)
    ///  3) remove ALL whitespace characters everywhere
    ///  4) keep the rest of the line EXACTLY (including the unique suffix tag), case-insensitive via uppercasing
    /// </summary>
    internal static class GeneralNormalizers
    {


        /// <summary>
        /// Strip optional leading "1234:" line-number prefix, and strip ALL "(...)" comment blocks
        /// anywhere on the line (e.g. "(u:a0001)").
        /// Returns the remaining text trimmed.
        /// </summary>
        public static string StripLineNumberPrefixAndParenComments(string line)
        {
            if (string.IsNullOrEmpty(line))
                return "";

            string s = line;

            // 1) Remove leading "nnnn:" (allow whitespace)
            // Examples:
            // "  80: G1X1Y2" -> "G1X1Y2"
            // "80:G1X1Y2"    -> "G1X1Y2"
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"^\s*\d+\s*:\s*",
                "",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant
            );

            // 2) Remove ALL "(...)" blocks anywhere on the line
            // This is intentionally "dumb but safe" for your use-case:
            // it nukes "(G1206)" too, which is fine because we already used that line as a marker
            // and we only want motion parsing lines after it.
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"\([^)]*\)",
                "",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant
            );

            // 3) Trim
            return (s ?? "").Trim();
        }


        private static bool TryStripLeadingLineNumber(string? s, out string result)
        {
            result = s ?? "";
            if (string.IsNullOrEmpty(result))
                return false;

            int i = 0;
            while (i < result.Length && char.IsWhiteSpace(result[i]))
                i++;

            int startDigits = i;
            while (i < result.Length && char.IsDigit(result[i]))
                i++;

            // no digits -> no prefix
            if (i == startDigits)
                return false;

            // optional spaces before colon
            while (i < result.Length && char.IsWhiteSpace(result[i]))
                i++;

            if (i >= result.Length || result[i] != ':')
                return false;

            // strip colon and any spaces right after it
            i++; // past ':'
            while (i < result.Length && char.IsWhiteSpace(result[i]))
                i++;

            result = result.Substring(i);
            return true;
        }






        public static string NormalizeTextLineToGcodeAndEndTag(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            // normalize line endings
            string t = s.Replace("\r", "").Replace("\n", "");
            if (t.Length == 0)
                return "";

            // strip optional leading anchor "#...#" (FIRST block only)
            {
                int i = 0;
                while (i < t.Length && char.IsWhiteSpace(t[i]))
                    i++;

                if (i < t.Length && t[i] == '#')
                {
                    int anchorClose = t.IndexOf('#', i + 1);
                    if (anchorClose > i)
                    {
                        t = t.Substring(anchorClose + 1);
                    }
                }
            }

            // strip optional "1234:" prefix
            TryStripLeadingLineNumber(t, out t);
            if (string.IsNullOrEmpty(t))
                return "";

            // remove ALL whitespace everywhere
            var chars = new char[t.Length];
            int n = 0;

            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (char.IsWhiteSpace(c))
                    continue;
                chars[n++] = c;
            }

            if (n <= 0)
                return "";

            return new string(chars, 0, n).ToUpperInvariant();
        }

        public static string NormalizeInsertLineAlignEndTag(string? s, int tagColumn = 75)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            // normalize line endings
            string t = s.Replace("\r", "").Replace("\n", "");
            if (t.Length == 0)
                return "";

            // strip optional leading anchor "#...#" (FIRST block only)
            {
                int i = 0;
                while (i < t.Length && char.IsWhiteSpace(t[i]))
                    i++;

                if (i < t.Length && t[i] == '#')
                {
                    int anchorClose = t.IndexOf('#', i + 1);
                    if (anchorClose > i)
                        t = t.Substring(anchorClose + 1);
                }
            }

            // strip optional "1234:" prefix
            TryStripLeadingLineNumber(t, out t);
            if (string.IsNullOrEmpty(t))
                return "";

            t = t.TrimEnd();

            int tagStart = t.LastIndexOf('(');
            if (tagStart < 0)
                return t;

            int tagClose = t.IndexOf(')', tagStart);
            if (tagClose < 0 || tagClose != t.Length - 1)
                return t;

            // accept ANY unique end-tag of the form "(X:Y0000)" where X and Y are letters (any case)
            string inside = t.Substring(tagStart);
            string chk = NormalizeTextLineToGcodeAndEndTag(inside); // removes whitespace, uppercases

            bool isUnique =
                chk.Length == 9 &&
                chk[0] == '(' &&
                chk[8] == ')' &&
                chk[2] == ':' &&
                char.IsLetter(chk[1]) &&
                char.IsLetter(chk[3]) &&
                (chk[4] >= '0' && chk[4] <= '9') &&
                (chk[5] >= '0' && chk[5] <= '9') &&
                (chk[6] >= '0' && chk[6] <= '9') &&
                (chk[7] >= '0' && chk[7] <= '9');

            if (!isUnique)
                return t;

            string basePart = t.Substring(0, tagStart).TrimEnd();
            string tagPart = t.Substring(tagStart).Trim();

            if (tagColumn < 0)
                tagColumn = 0;

            if (basePart.Length == 0)
                return tagPart;

            if (basePart.Length < tagColumn)
                return basePart.PadRight(tagColumn, ' ') + tagPart;

            return basePart + " " + tagPart;
        }


        public static string NormalizeTextLineAsIs(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            string t = s.Replace("\r", "").Replace("\n", "");
            if (string.IsNullOrEmpty(t))
                return "";

            var chars = new char[t.Length];
            int n = 0;

            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (char.IsWhiteSpace(c))
                    continue;

                chars[n++] = c;
            }

            if (n <= 0)
                return "";

            return new string(chars, 0, n).ToUpperInvariant();
        }

       
        


    }
}
