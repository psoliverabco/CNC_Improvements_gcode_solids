// File: Utilities/AutoAddRegion.cs
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// Universal RegionSet creator for TURN regions.
    /// IMPORTANT: This class MUST be fed ONLY the actual region gcode text lines
    /// (no "(NAME ST)" / "(NAME END)" wrapper lines).
    ///
    /// The RichTextBox can already contain the formatted block (with wrappers + tags);
    /// this class only creates the RegionSet and stores markers/snapshot keys.
    /// </summary>
    internal static class AutoAddTurnRegion
    {
        // ----------------------------
        // Public API
        // ----------------------------
        public static RegionSet AddTurnRegionSet_FromRegionGcodeOnly(
            MainWindow main,
            string regionName,
            IReadOnlyList<string> regionGcodeLinesOnly,
            string KEY_ToolUsage,
            string KEY_Quadrant,
            string KEY_StartXLine,
            string KEY_StartZLine,
            string KEY_EndXLine,
            string KEY_EndZLine,
            string KEY_TxtZExt,
            string KEY_NRad,
            string defaultToolUsage = "OFF",
            string defaultQuadrant = "3",
            string defaultZExt = "-100",
            string defaultNRad = "0.8",
            bool showInViewAll = true,
            bool exportEnabled = false)
            
            
            
        {
            if (main == null) throw new ArgumentNullException(nameof(main));

            if (string.IsNullOrWhiteSpace(regionName))
                throw new ArgumentException("Region name is empty.", nameof(regionName));

            if (regionGcodeLinesOnly == null || regionGcodeLinesOnly.Count == 0)
                throw new ArgumentException("Region gcode lines are empty.", nameof(regionGcodeLinesOnly));

            // Create a unique name within TurnSets
            string uniqueName = MakeUniqueTurnSetName(main, regionName.Trim());

            // Build set
            var rs = new RegionSet(RegionSetKind.Turn, uniqueName)
            {
                PageSnapshot = new UiStateSnapshot(),

                // NEW: caller-controlled defaults
                ShowInViewAll = showInViewAll,
                ExportEnabled = exportEnabled
            };



            // Defaults (caller controls these via args if needed)
            rs.PageSnapshot.Values[KEY_TxtZExt] = defaultZExt;
            rs.PageSnapshot.Values[KEY_NRad] = defaultNRad;
            rs.PageSnapshot.Values[KEY_ToolUsage] = defaultToolUsage;
            rs.PageSnapshot.Values[KEY_Quadrant] = defaultQuadrant;





            // Store region lines in the SAME canonical form CodeCleanup/TextSearching expects:
            // - strip optional "1234:" prefix
            // - strip optional "#...#" anchor block
            // - remove ALL whitespace
            // - uppercase
            // - KEEP the unique end-tag "(...:A0000)" etc intact
            var normLines = new List<string>(regionGcodeLinesOnly.Count);

            rs.RegionLines.Clear();
            for (int i = 0; i < regionGcodeLinesOnly.Count; i++)
            {
                string raw = regionGcodeLinesOnly[i] ?? "";
                string norm = TextSearching.NormalizeTextLineAsIs(raw);

                // skip empties (prevents marker keys becoming blank)
                if (string.IsNullOrWhiteSpace(norm))
                    continue;

                normLines.Add(norm);
                rs.RegionLines.Add(norm);
            }

            // Marker lines MUST be stored in the SAME canonical form as RegionLines
            StoreStartEndMarkers(
                rs,
                normLines,
                KEY_StartXLine,
                KEY_StartZLine,
                KEY_EndXLine,
                KEY_EndZLine
            );


            // Marker lines (first/last X and Z within the region)
            StoreStartEndMarkers(
                rs,
                regionGcodeLinesOnly,
                KEY_StartXLine,
                KEY_StartZLine,
                KEY_EndXLine,
                KEY_EndZLine
            );

            // Add to model
            main.TurnSets.Add(rs);

            try { main.SelectedTurnSet = rs; } catch { /* ignore */ }

            return rs;
        }

        /// <summary>
        /// Convenience helper for callers who have the formatted block:
        /// [0]=(NAME ST), [1..n-2]=gcode, [n-1]=(NAME END)
        /// Returns ONLY [1..n-2]. If block is too short, returns empty.
        /// </summary>
        public static List<string> StripWrapper_FirstLast(IReadOnlyList<string> formattedBlockLines)
        {
            var inner = new List<string>();
            if (formattedBlockLines == null) return inner;
            if (formattedBlockLines.Count < 3) return inner;

            for (int i = 1; i <= formattedBlockLines.Count - 2; i++)
                inner.Add(formattedBlockLines[i] ?? "");

            return inner;
        }

        // ----------------------------
        // Internal helpers
        // ----------------------------
        private static string MakeUniqueTurnSetName(MainWindow main, string baseName)
        {
            // exact match only (your project uses ordinal)
            bool Exists(string nm)
            {
                for (int i = 0; i < main.TurnSets.Count; i++)
                {
                    var s = main.TurnSets[i];
                    if (s != null && string.Equals(s.Name, nm, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }

            if (!Exists(baseName)) return baseName;

            int k = 2;
            while (true)
            {
                string candidate = baseName + "_" + k.ToString(CultureInfo.InvariantCulture);
                if (!Exists(candidate)) return candidate;
                k++;
            }
        }

        private static void StoreStartEndMarkers(
            RegionSet rs,
            IReadOnlyList<string> lines,
            string KEY_StartXLine,
            string KEY_StartZLine,
            string KEY_EndXLine,
            string KEY_EndZLine)
        {
            int firstX = -1, firstZ = -1, lastX = -1, lastZ = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i] ?? "";

                if (firstX < 0 && HasAxisTokenWithNumber(s, 'X')) firstX = i;
                if (firstZ < 0 && HasAxisTokenWithNumber(s, 'Z')) firstZ = i;

                if (HasAxisTokenWithNumber(s, 'X')) lastX = i;
                if (HasAxisTokenWithNumber(s, 'Z')) lastZ = i;
            }

            rs.PageSnapshot.Values[KEY_StartXLine] = (firstX >= 0) ? TextSearching.NormalizeTextLineAsIs(lines[firstX]) : "";
            rs.PageSnapshot.Values[KEY_StartZLine] = (firstZ >= 0) ? TextSearching.NormalizeTextLineAsIs(lines[firstZ]) : "";
            rs.PageSnapshot.Values[KEY_EndXLine] = (lastX >= 0) ? TextSearching.NormalizeTextLineAsIs(lines[lastX]) : "";
            rs.PageSnapshot.Values[KEY_EndZLine] = (lastZ >= 0) ? TextSearching.NormalizeTextLineAsIs(lines[lastZ]) : "";
        }

        // No regex. Looks for axis letter followed by optional spaces then a number char.
        private static bool HasAxisTokenWithNumber(string line, char axis)
        {
            if (string.IsNullOrEmpty(line)) return false;

            // quick scan
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == axis || c == char.ToLowerInvariant(axis))
                {
                    int j = i + 1;

                    // skip spaces
                    while (j < line.Length && (line[j] == ' ' || line[j] == '\t'))
                        j++;

                    if (j >= line.Length) return false;

                    // valid number start: digit, +, -, .
                    char n = line[j];
                    bool ok =
                        (n >= '0' && n <= '9') ||
                        n == '+' || n == '-' || n == '.';

                    if (ok) return true;
                }
            }

            return false;
        }

        
    }
}
