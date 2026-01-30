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
    internal static class TextSearching
    {
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



        // Removes a trailing unique end-tag like "(T:A0000)" / "(M:B0123)" / "(F:C0007)" etc.
        // Returns true if a valid tag was removed.
        public static bool TryRemoveUniqueTag(string? line, out string withoutTag)
        {
            withoutTag = (line ?? "");
            if (string.IsNullOrWhiteSpace(withoutTag))
                return false;

            string t = withoutTag.Replace("\r", "").Replace("\n", "").TrimEnd();

            // must end with ')'
            if (t.Length < 9 || t[t.Length - 1] != ')')
                return false;

            int open = t.LastIndexOf('(');
            if (open < 0)
                return false;

            string tag = t.Substring(open);

            // Validate "(X:Y0000)" exactly 9 chars:
            // 0 '('
            // 1 kind letter
            // 2 ':'
            // 3 setId letter
            // 4..7 digits
            // 8 ')'
            if (tag.Length != 9)
                return false;

            if (tag[0] != '(' || tag[8] != ')')
                return false;

            char kind = tag[1];
            char colon = tag[2];
            char setId = tag[3];

            if (!char.IsLetter(kind))
                return false;

            if (colon != ':')
                return false;

            if (!char.IsLetter(setId))
                return false;

            for (int i = 4; i <= 7; i++)
            {
                if (tag[i] < '0' || tag[i] > '9')
                    return false;
            }

            // Strip it
            withoutTag = t.Substring(0, open).TrimEnd();
            return true;
        }







        /// <summary>
        /// Normalizes a single line to a stable compare key.
        /// - strips optional "1234:" prefix
        /// - strips FIRST leading "#...#" anchor block (only if at start)
        /// - removes ALL whitespace everywhere
        /// - uppercases invariant
        /// </summary>
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

        /// <summary>
        /// NEW: remove ONLY the trailing unique end-tag "(t:...)/(m:...)/(d:...)/(u:...)" (if present).
        /// Keeps everything else (including a leading "#uid,n#" anchor if present).
        ///
        /// Notes:
        /// - This is used for "retagging" flows.
        /// - This does NOT strip internal whitespace by itself (call NormalizeTextLineAsIs if you want that).
        /// </summary>
        public static string NormalizeTextLineToRemEndTag(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            string t = s.Replace("\r", "").Replace("\n", "");
            if (t.Length == 0)
                return "";

            t = t.TrimEnd();

            int tagStart = t.LastIndexOf('(');
            if (tagStart < 0)
                return t;

            int tagClose = t.IndexOf(')', tagStart);
            if (tagClose < 0 || tagClose != t.Length - 1)
                return t; // not a trailing "(...)" block

            // Check it's a unique tag (t/m/d/u) ignoring whitespace/case
            string tail = t.Substring(tagStart);
            string chk = NormalizeTextLineToGcodeAndEndTag(tail); // removes whitespace, uppercases
            if (!(chk.StartsWith("(T:", StringComparison.Ordinal) ||
                  chk.StartsWith("(M:", StringComparison.Ordinal) ||
                  chk.StartsWith("(D:", StringComparison.Ordinal) ||
                  chk.StartsWith("(U:", StringComparison.Ordinal)))
            {
                return t; // not a unique tag
            }

            // remove it
            return t.Substring(0, tagStart).TrimEnd();
        }

        /// <summary>
        /// For inserting/displaying a line back into the editor:
        /// - strips optional leading anchor "#...#" (FIRST block only)
        /// - strips optional leading "1234:" prefix
        /// - keeps the text as-is (does NOT remove internal whitespace)
        /// - finds a trailing unique tag "(t:...)/(m:...)/(d:...)/(u:...)" and aligns the '(' to tagColumn (0-based index)
        /// </summary>
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

            string inside = t.Substring(tagStart);
            string chk = NormalizeTextLineToGcodeAndEndTag(inside);
            if (!(chk.StartsWith("(T:", StringComparison.Ordinal) ||
                  chk.StartsWith("(M:", StringComparison.Ordinal) ||
                  chk.StartsWith("(D:", StringComparison.Ordinal) ||
                  chk.StartsWith("(U:", StringComparison.Ordinal)))
            {
                return t;
            }

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

        public static int FindSingleLine(
            List<string> lines,
            string needleStoredOrRaw,
            int rangeStart = -1,
            int rangeEnd = -1,
            bool preferLast = false)
        {
            if (lines == null || lines.Count == 0)
                return -1;

            string want = NormalizeTextLineToGcodeAndEndTag(needleStoredOrRaw);
            if (string.IsNullOrEmpty(want))
                return -1;

            int lo = Math.Max(0, rangeStart);
            int hi = (rangeEnd >= 0) ? Math.Min(lines.Count - 1, rangeEnd) : (lines.Count - 1);
            if (lo > hi)
                return -1;

            if (!preferLast)
            {
                for (int i = lo; i <= hi; i++)
                {
                    if (NormalizeTextLineToGcodeAndEndTag(lines[i]) == want)
                        return i;
                }
            }
            else
            {
                for (int i = hi; i >= lo; i--)
                {
                    if (NormalizeTextLineToGcodeAndEndTag(lines[i]) == want)
                        return i;
                }
            }

            return -1;
        }

        public static bool FindMultiLine(
            List<string> allLines,
            IList<string> storedRegionLines,
            out int start,
            out int end,
            out int matchCount,
            int rangeStart = -1,
            int rangeEnd = -1)
        {
            start = -1;
            end = -1;
            matchCount = 0;

            if (allLines == null || allLines.Count == 0)
                return false;

            if (storedRegionLines == null || storedRegionLines.Count == 0)
                return false;

            int n = storedRegionLines.Count;
            if (n > allLines.Count)
                return false;

            var needle = new string[n];
            for (int i = 0; i < n; i++)
            {
                needle[i] = NormalizeTextLineToGcodeAndEndTag(storedRegionLines[i]);
                if (needle[i].Length == 0)
                    return false;
            }

            int lo = Math.Max(0, rangeStart);
            int hi = (rangeEnd >= 0) ? Math.Min(allLines.Count - 1, rangeEnd) : (allLines.Count - 1);

            if (hi - lo + 1 < n)
                return false;

            int lastStart = hi - n + 1;

            for (int s = lo; s <= lastStart; s++)
            {
                bool ok = true;

                for (int j = 0; j < n; j++)
                {
                    string got = NormalizeTextLineToGcodeAndEndTag(allLines[s + j]);
                    if (got != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                    continue;

                matchCount++;

                if (matchCount == 1)
                {
                    start = s;
                    end = s + n - 1;
                }
            }

            return matchCount > 0;
        }




        // -------------------------------
        // NEW shared helpers for CodeCleanup + searching
        // -------------------------------

        // Remove ALL parenthesis blocks (comments, old tags, etc)
        private static readonly Regex RxAllParenBlocks = new Regex(@"\([^)]*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Detect motion-ish tokens (for snapshot value classification)
        private static readonly Regex RxHasG0123 = new Regex(@"\bG0\b|\bG00\b|\bG1\b|\bG01\b|\bG2\b|\bG02\b|\bG3\b|\bG03\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex RxHasAxisWord = new Regex(@"[XYZIJKR]\s*[+\-]?\d",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parse the store-time identity anchor "#uid,n#" ONLY (must be at line start after whitespace).
        /// Returns uid, n, and anchorEndIndex (index after the closing '#').
        /// </summary>
        public static bool TryParseLeadingUidNAnchor(string? s, out string uid, out int n, out int anchorEndIndex)
        {
            uid = "";
            n = 0;
            anchorEndIndex = -1;

            if (string.IsNullOrEmpty(s))
                return false;

            string t = s;

            int i = 0;
            while (i < t.Length && char.IsWhiteSpace(t[i]))
                i++;

            if (i >= t.Length || t[i] != '#')
                return false;

            int close = t.IndexOf('#', i + 1);
            if (close <= i)
                return false;

            string inside = t.Substring(i + 1, close - (i + 1)); // "<uid>,<n>"
            int comma = inside.IndexOf(',');
            if (comma <= 0 || comma >= inside.Length - 1)
                return false;

            string uidPart = inside.Substring(0, comma).Trim();
            string nPart = inside.Substring(comma + 1).Trim();

            if (uidPart.Length == 0)
                return false;

            if (!int.TryParse(nPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nn))
                return false;

            uid = uidPart;
            n = nn;
            anchorEndIndex = close + 1;
            return true;
        }

        /// <summary>
        /// Returns anchor key "#uid,n#" if a leading uid,n anchor exists.
        /// </summary>
        public static bool TryGetLeadingUidNAnchorKey(string? s, out string anchorKey)
        {
            anchorKey = "";
            if (string.IsNullOrWhiteSpace(s))
                return false;

            string t = (s ?? "").Trim();
            if (TryParseLeadingUidNAnchor(t, out string uid, out int n, out _))
            {
                anchorKey = $"#{uid},{n}#";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Strip leading "#uid,n#" anchor if present (uid,n form only).
        /// </summary>
        public static string StripLeadingUidNAnchorIfPresent(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return s ?? "";

            string t = (s ?? "").Replace("\r", "").Replace("\n", "").Trim();

            if (TryParseLeadingUidNAnchor(t, out _, out _, out int endIx))
                return t.Substring(endIx).Trim();

            return t;
        }

        /// <summary>
        /// Public wrapper for line-number stripping (same behavior as internal TryStripLeadingLineNumber).
        /// Returns the original string if no "1234:" prefix exists.
        /// </summary>
        public static string StripLeadingLineNumberIfPresent(string? s)
        {
            string t = (s ?? "").Replace("\r", "").Replace("\n", "");
            TryStripLeadingLineNumber(t, out t);
            return t;
        }

        /// <summary>
        /// Remove all "(...)" blocks anywhere in the line.
        /// </summary>
        public static string RemoveAllParenBlocks(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            string t = s.Replace("\r", "").Replace("\n", "");
            if (t.Length == 0)
                return "";

            return RxAllParenBlocks.Replace(t, "");
        }

        /// <summary>
        /// Payload match key:
        /// - strip leading "#uid,n#" if present
        /// - strip optional "1234:" prefix
        /// - remove ALL "(...)" blocks
        /// - remove ALL whitespace
        /// - uppercase invariant
        /// </summary>
        public static string NormalizePayloadForMatch(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            string t = (s ?? "").Replace("\r", "").Replace("\n", "").Trim();
            if (t.Length == 0)
                return "";

            t = StripLeadingUidNAnchorIfPresent(t);
            t = StripLeadingLineNumberIfPresent(t);
            t = RemoveAllParenBlocks(t);

            return NormalizeTextLineAsIs(t); // removes whitespace + uppercases
        }

        public static bool IsPureParenLine(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return true;

            string t = (s ?? "").Trim();
            return (t.Length >= 2 && t.StartsWith("(", StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal));
        }

        /// <summary>
        /// Motion-line classifier used by cleanup remap.
        /// </summary>
        public static bool LooksLikeMotionLineValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (IsPureParenLine(value))
                return false;

            string t = (value ?? "").Replace("\r", "").Replace("\n", "").Trim();
            if (t.Length == 0)
                return false;

            // remove (...) blocks first
            t = RemoveAllParenBlocks(t);

            // strip uid,n anchor
            t = StripLeadingUidNAnchorIfPresent(t);

            // strip line number
            t = StripLeadingLineNumberIfPresent(t);

            t = (t ?? "").Trim();
            if (t.Length == 0)
                return false;

            if (RxHasG0123.IsMatch(t))
                return true;

            if (RxHasAxisWord.IsMatch(t))
                return true;

            return false;
        }








    }
}
