// File: SetManagement/Builders/BuiltRegionNormalizers.cs
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace CNC_Improvements_gcode_solids.SetManagement.Builders
{
    internal static class BuiltRegionNormalizers
    {
        // Canonical normalizer:
        // - strip optional leading "1234:" prefix
        // - strip first leading "#...#" anchor block
        // - remove all whitespace
        // - uppercase invariant
        public static string NormalizeTextLineToGcodeAndEndTag(string raw)
        {
            if (raw == null)
                return string.Empty;

            string s = raw.Trim();

            // Strip "1234:" prefix (optional spaces around colon)
            s = Regex.Replace(s, @"^\s*\d+\s*:\s*", "");

            // Strip one leading "#...#" anchor block
            if (s.Length > 0 && s[0] == '#')
            {
                int idx2 = s.IndexOf('#', 1);
                if (idx2 > 0 && idx2 + 1 <= s.Length)
                    s = s.Substring(idx2 + 1);
            }

            // Remove all whitespace
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }

            return sb.ToString().ToUpperInvariant();
        }


        



        public static string BuildAnchoredLine(string uidN, int localIndex1Based, string normalizedGcodeAndEndTag)
        {
            if (string.IsNullOrWhiteSpace(uidN))
                throw new ArgumentException("uidN is blank", nameof(uidN));
            if (localIndex1Based <= 0)
                throw new ArgumentOutOfRangeException(nameof(localIndex1Based), "localIndex1Based must be >= 1");

            return $"#{uidN},{localIndex1Based}#{normalizedGcodeAndEndTag ?? string.Empty}";
        }

        public static string NewUidN() => Guid.NewGuid().ToString("N");
    }
}
