// File: Utilities/AutoAddMillRegion.cs
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// Universal RegionSet creator for MILL regions.
    /// IMPORTANT: This class MUST be fed ONLY the actual region gcode text lines
    /// (no "(NAME ST)" / "(NAME END)" wrapper lines).
    ///
    /// Stores:
    ///  - RegionLines as anchored:  #uid,local#<normalized_gcode(+endtag_if_present)>
    ///  - Snapshot marker texts (PlaneZ/StartX/StartY/EndX/EndY) ALSO anchored to that same uid/local
    ///  - Snapshot defaults (tool dia/len + radios etc)
    /// </summary>
    internal static class AutoAddMillRegion
    {
        // ----------------------------
        // Public API
        // ----------------------------
        public static RegionSet AddMillRegionSet_FromRegionGcodeOnly(
            MainWindow main,
            string regionName,
            IReadOnlyList<string> regionGcodeLinesOnly,

            // snapshot keys
            string KEY_CoordMode,
            string KEY_ToolUsage,

            string KEY_PlaneZLineText,
            string KEY_StartXLineText,
            string KEY_StartYLineText,
            string KEY_EndXLineText,
            string KEY_EndYLineText,

            string KEY_TxtToolDia,
            string KEY_TxtToolLen,
            string KEY_FuseAll,
            string KEY_RemoveSplitter,
            string KEY_Clipper,
            string KEY_ClipperIsland,
            string KEY_RegionUid,

            // defaults
            string defaultCoordMode = "Cartesian",
            string defaultToolUsage = "OFF",
            string defaultToolDia = "",
            string defaultToolLen = "",
            string defaultFuseAll = "0",
            string defaultRemoveSplitter = "0",
            string defaultClipper = "0",
            string defaultClipperIsland = "0",

            bool showInViewAll = true,
            bool exportEnabled = false)
        {
            if (main == null) throw new ArgumentNullException(nameof(main));

            if (string.IsNullOrWhiteSpace(regionName))
                throw new ArgumentException("Region name is empty.", nameof(regionName));

            if (regionGcodeLinesOnly == null || regionGcodeLinesOnly.Count == 0)
                throw new ArgumentException("Region gcode lines are empty.", nameof(regionGcodeLinesOnly));

            // Create a unique name within MillSets
            string uniqueName = MakeUniqueMillSetName(main, regionName.Trim());

            // Build set
            var rs = new RegionSet(RegionSetKind.Mill, uniqueName)
            {
                PageSnapshot = new UiStateSnapshot(),
                ShowInViewAll = showInViewAll,
                ExportEnabled = exportEnabled
            };

            var snap = rs.PageSnapshot!.Values;

            // Ensure per-set RegionUid (used for #uid,local# anchors)
            string uid = Guid.NewGuid().ToString("N");
            snap[KEY_RegionUid] = uid;

            // Defaults (match MillPage storage style)
            snap[KEY_CoordMode] = defaultCoordMode ?? "";
            snap[KEY_ToolUsage] = defaultToolUsage ?? "";

            snap[KEY_TxtToolDia] = defaultToolDia ?? "";
            snap[KEY_TxtToolLen] = defaultToolLen ?? "";

            snap[KEY_FuseAll] = defaultFuseAll ?? "0";
            snap[KEY_RemoveSplitter] = defaultRemoveSplitter ?? "0";
            snap[KEY_Clipper] = defaultClipper ?? "0";
            snap[KEY_ClipperIsland] = defaultClipperIsland ?? "0";

            // ------------------------------------------------------------
            // Store RegionLines in the SAME canonical form MillPage expects:
            //   #uid,local# + NormalizeTextLineToGcodeAndEndTag(...)
            // ------------------------------------------------------------
            rs.RegionLines.Clear();

            // Keep also a parallel list of normalized lines (no anchor) so we can
            // build snapshot marker texts consistently.
            var normNoAnchor = new List<string>(regionGcodeLinesOnly.Count);

            for (int i = 0; i < regionGcodeLinesOnly.Count; i++)
            {
                string raw = regionGcodeLinesOnly[i] ?? "";
                string norm = TextSearching.NormalizeTextLineToGcodeAndEndTag(raw);

                // skip empties
                if (string.IsNullOrWhiteSpace(norm))
                {
                    normNoAnchor.Add(""); // keep indexing aligned
                    continue;
                }

                normNoAnchor.Add(norm);
                rs.RegionLines.Add($"#{uid},{i + 1}#{norm}");
            }

            // ------------------------------------------------------------
            // NEW: Store snapshot marker texts anchored to region (like manual MILL sets)
            //   PlaneZ  = first Z line inside region
            //   StartX  = first X line inside region
            //   StartY  = first Y line inside region
            //   EndX    = last X line inside region
            //   EndY    = last Y line inside region
            // ------------------------------------------------------------
            int firstZ = -1;
            int firstX = -1;
            int firstY = -1;
            int lastX = -1;
            int lastY = -1;

            for (int i = 0; i < normNoAnchor.Count; i++)
            {
                string s = normNoAnchor[i] ?? "";
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                if (firstZ < 0 && HasAxisTokenWithNumber(s, 'Z')) firstZ = i;
                if (firstX < 0 && HasAxisTokenWithNumber(s, 'X')) firstX = i;
                if (firstY < 0 && HasAxisTokenWithNumber(s, 'Y')) firstY = i;

                if (HasAxisTokenWithNumber(s, 'X')) lastX = i;
                if (HasAxisTokenWithNumber(s, 'Y')) lastY = i;
            }

            // helper: build "#uid,local#<norm>" or ""
            string AnchorAt(int idx)
            {
                if (idx < 0 || idx >= normNoAnchor.Count) return "";
                string s = normNoAnchor[idx] ?? "";
                if (string.IsNullOrWhiteSpace(s)) return "";
                return $"#{uid},{idx + 1}#{s}";
            }

            snap[KEY_PlaneZLineText] = AnchorAt(firstZ);
            snap[KEY_StartXLineText] = AnchorAt(firstX);
            snap[KEY_StartYLineText] = AnchorAt(firstY);
            snap[KEY_EndXLineText] = AnchorAt(lastX);
            snap[KEY_EndYLineText] = AnchorAt(lastY);

            // Add to model
            main.MillSets.Add(rs);
            try { main.SelectedMillSet = rs; } catch { /* ignore */ }

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
        private static string MakeUniqueMillSetName(MainWindow main, string baseName)
        {
            bool Exists(string nm)
            {
                for (int i = 0; i < main.MillSets.Count; i++)
                {
                    var s = main.MillSets[i];
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

        // No regex. Looks for axis letter followed by optional spaces then a number char.
        private static bool HasAxisTokenWithNumber(string line, char axis)
        {
            if (string.IsNullOrEmpty(line)) return false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == axis || c == char.ToLowerInvariant(axis))
                {
                    int j = i + 1;

                    while (j < line.Length && (line[j] == ' ' || line[j] == '\t'))
                        j++;

                    if (j >= line.Length) return false;

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
