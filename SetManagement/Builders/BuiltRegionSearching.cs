// File: SetManagement/Builders/BuiltRegionSearches.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CNC_Improvements_gcode_solids.SetManagement.Builders
{
    public static class BuiltRegionSearches
    {
        // Remove "#uid,n#" then normalize remainder
        public static string NormalizeRemoveUid(string keyText)
        {
            if (keyText == null)
                return string.Empty;

            string s = keyText.Trim();

            if (s.Length > 0 && s[0] == '#')
            {
                int idx2 = s.IndexOf('#', 1);
                if (idx2 > 0 && idx2 + 1 <= s.Length)
                    s = s.Substring(idx2 + 1);
            }

            return BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(s);
        }

        // Single line match: raw editor line vs stored key (anchored or not)
        public static bool FindKeyMatch(string textLine, string keyText)
        {
            string a = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(textLine ?? "");
            string b = NormalizeRemoveUid(keyText ?? "");
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        // Find first/last match in range [rangeStart..rangeEnd] (inclusive).
        // If rangeStart/rangeEnd invalid (<0) => searches whole list.
        public static int FindSingleLine(
            IReadOnlyList<string> allLines,
            string keyText,
            int rangeStart,
            int rangeEnd,
            bool preferLast)
        {
            if (allLines == null || allLines.Count == 0)
                return -1;

            string needle = NormalizeRemoveUid(keyText ?? "");
            if (needle.Length == 0)
                return -1;

            int s = rangeStart;
            int e = rangeEnd;

            if (s < 0 || e < 0 || s >= allLines.Count || e >= allLines.Count || e < s)
            {
                s = 0;
                e = allLines.Count - 1;
            }

            int found = -1;

            if (!preferLast)
            {
                for (int i = s; i <= e; i++)
                {
                    string got = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(allLines[i] ?? "");
                    if (string.Equals(got, needle, StringComparison.Ordinal))
                        return i;
                }

                return -1;
            }

            for (int i = e; i >= s; i--)
            {
                string got = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(allLines[i] ?? "");
                if (string.Equals(got, needle, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        // Multi-line block match:
        // allLines are raw editor lines
        // regionLines are stored anchored lines (#uid,n#NORMALIZED...)
        // If rangeStart/rangeEnd invalid (<0 or out of bounds) => searches whole list.
        // Range is inclusive: [rangeStart..rangeEnd]
        public static bool FindMultiLine(
            IReadOnlyList<string> allLines,
            ObservableCollection<string> regionLinesAnchored,
            out int startIndex,
            out int endIndex,
            out int matchCount,
            int rangeStart = -1,
            int rangeEnd = -1)
        {
            startIndex = -1;
            endIndex = -1;
            matchCount = 0;

            if (allLines == null || allLines.Count == 0)
                return false;

            if (regionLinesAnchored == null || regionLinesAnchored.Count == 0)
                return false;

            int n = regionLinesAnchored.Count;
            if (n > allLines.Count)
                return false;

            // Pre-normalize needle = region stored lines with UID stripped
            var needle = new string[n];
            for (int i = 0; i < n; i++)
            {
                needle[i] = NormalizeRemoveUid(regionLinesAnchored[i] ?? "");
                if (needle[i].Length == 0)
                    return false;
            }

            // Determine inclusive search range
            int lo = rangeStart;
            int hi = rangeEnd;

            if (lo < 0 || hi < 0 || lo >= allLines.Count || hi >= allLines.Count || hi < lo)
            {
                lo = 0;
                hi = allLines.Count - 1;
            }

            // Range window must be at least n lines
            if (hi - lo + 1 < n)
                return false;

            int lastStart = hi - n + 1;

            // Pre-normalize haystack once (full list)
            var hay = new string[allLines.Count];
            for (int i = 0; i < allLines.Count; i++)
                hay[i] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(allLines[i] ?? "");

            for (int s = lo; s <= lastStart; s++)
            {
                bool ok = true;

                for (int j = 0; j < n; j++)
                {
                    if (!string.Equals(hay[s + j], needle[j], StringComparison.Ordinal))
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
                    startIndex = s;
                    endIndex = s + n - 1;
                }
            }

            return matchCount > 0;
        }

    }
}
