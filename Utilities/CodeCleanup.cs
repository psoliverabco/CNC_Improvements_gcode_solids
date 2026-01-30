// File: Utilities/CodeCleanup.cs
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// SIMPLE RETAG ONLY (NO PAYLOAD LOGIC):
    ///
    /// For each set:
    /// 1) Read RegionLines and start/end snapshot keys.
    /// 2) Strip existing unique tag using TextSearching.TryRemoveUniqueTag().
    /// 3) Canonicalize using TextSearching.NormalizeTextLineAsIs() (removes ALL whitespace, uppercases).
    /// 4) Append new unique tag (no spaces) and store back into RegionLines.
    /// 5) Rewrite start/end keys by exact canonical mapping to the new RegionLines.
    /// 6) Build editor text from RegionLines using NormalizeInsertLineAlignEndTag() (tag aligned to col 75).
    /// 7) Wrap with "(NAME ST)" "(NAME END)".
    /// 8) Return editor text to caller.
    ///
    /// Notes:
    /// - No regex, no anchor parsing, no payload queue matching.
    /// - Works only if keys/region are stored in the SAME canonical form (NormalizeTextLineAsIs).
    /// </summary>
    internal static class CodeCleanup
    {
        private const int TAG_PAD_COLUMN = 75;

        // These are the ONLY snapshot motion-line keys we retag here.
        // If a set doesn't have them, nothing happens.
        private static readonly string[] StartEndKeys = new[]
        {
            "__StartXLine",
            "__StartZLine",
            "__EndXLine",
            "__EndZLine",
        };

        public static string BuildAndApplyAllCleanup(
            IList<RegionSet> turnSets,
            IList<RegionSet> millSets,
            IList<RegionSet> drillSets,
            out string newEditorText)
        {
            var report = new StringBuilder(16384);
            var editor = new StringBuilder(65536);

            report.AppendLine("=== ONE-SHOT CLEANUP: TURN + MILL + DRILL (RETAG ONLY) ===");
            report.AppendLine("Rules:");
            report.AppendLine(" - Strip old unique tag via TryRemoveUniqueTag()");
            report.AppendLine(" - Canonicalize via NormalizeTextLineAsIs() (no whitespace, uppercase)");
            report.AppendLine(" - Append new tag (no spaces)");
            report.AppendLine(" - Rewrite Start/End keys by exact canonical mapping");
            report.AppendLine(" - Editor output uses NormalizeInsertLineAlignEndTag() (tag col 75)");
            report.AppendLine();

            char setId = 'A';

            ProcessKind('T', "TURN", turnSets, ref setId, report, editor);
            ProcessKind('M', "MILL", millSets, ref setId, report, editor);
            ProcessKind('D', "DRILL", drillSets, ref setId, report, editor);

            newEditorText = editor.ToString();
            return report.ToString();
        }

        private static void ProcessKind(
            char kindTag,          // 'T' / 'M' / 'D'
            string kindName,
            IList<RegionSet> sets,
            ref char setId,
            StringBuilder report,
            StringBuilder editor)
        {
            report.AppendLine($"--- {kindName} SETS ---");

            if (sets == null || sets.Count == 0)
            {
                report.AppendLine($"No {kindName} sets.");
                report.AppendLine();
                return;
            }

            int processed = 0;

            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                if (set == null)
                    continue;

                if (set.RegionLines == null || set.RegionLines.Count == 0)
                    continue;

                string name = (set.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = $"{kindName}_SET_{i + 1}";

                int inCount = set.RegionLines.Count;

                // Map: canonical(no tag) -> newStoredLine(with new tag)
                // This is the ONLY thing we use to rewrite Start/End keys.
                var canonMap = new Dictionary<string, string>(StringComparer.Ordinal);

                var newStoredLines = new List<string>(inCount);

                // 1) Retag RegionLines
                for (int r = 0; r < set.RegionLines.Count; r++)
                {
                    string raw = set.RegionLines[r] ?? "";

                    // strip existing unique tag
                    if (TextSearching.TryRemoveUniqueTag(raw, out string noTag))
                        raw = noTag;

                    // canonicalize (removes ALL whitespace, uppercase)
                    string canon = TextSearching.NormalizeTextLineAsIs(raw);
                    if (string.IsNullOrWhiteSpace(canon))
                        continue;

                    // build and append new tag (no spaces)
                    string newTag = BuildUniqueTag(kindTag, setId, newStoredLines.Count);
                    string stored = canon + newTag;

                    newStoredLines.Add(stored);

                    // store first mapping only (stable)
                    if (!canonMap.ContainsKey(canon))
                        canonMap[canon] = stored;
                }

                // 2) DRILL depth line: retag it too (if present) and include if not already present
                bool drillDepthIncluded = false;
                if (kindTag == 'D')
                {
                    string depth = GetSnapValue(set, "DrillDepthLineText");
                    if (!string.IsNullOrWhiteSpace(depth))
                    {
                        if (TextSearching.TryRemoveUniqueTag(depth, out string noTagDepth))
                            depth = noTagDepth;

                        string canonDepth = TextSearching.NormalizeTextLineAsIs(depth);

                        if (!string.IsNullOrWhiteSpace(canonDepth) && !canonMap.ContainsKey(canonDepth))
                        {
                            string newTag = BuildUniqueTag(kindTag, setId, newStoredLines.Count);
                            string stored = canonDepth + newTag;

                            newStoredLines.Add(stored);
                            canonMap[canonDepth] = stored;
                            drillDepthIncluded = true;
                        }
                    }
                }

                if (newStoredLines.Count == 0)
                {
                    report.AppendLine($"[{kindName}] {name}: SKIP (no lines after retag).");
                    continue;
                }

                // 3) Rewrite Start/End snapshot keys using EXACT mapping
                int keysRemapped = RemapStartEndKeys(set, canonMap);

                // 4) Store RegionLines back into the set
                set.RegionLines.Clear();
                for (int k = 0; k < newStoredLines.Count; k++)
                    set.RegionLines.Add(newStoredLines[k]);

                // 5) Build editor output from the set RegionLines (align tag for display)
                editor.AppendLine($"({(name ?? "").Trim().ToUpperInvariant()} ST)");
                for (int k = 0; k < newStoredLines.Count; k++)
                    editor.AppendLine(TextSearching.NormalizeInsertLineAlignEndTag(newStoredLines[k], TAG_PAD_COLUMN));
                editor.AppendLine($"({(name ?? "").Trim().ToUpperInvariant()} END)");
                editor.AppendLine();

                report.AppendLine($"[{kindName}] {name}:");
                report.AppendLine($"  Motion lines in: {inCount}");
                report.AppendLine($"  Motion lines out: {newStoredLines.Count}");
                report.AppendLine($"  Start/End keys remapped: {keysRemapped}");
                if (kindTag == 'D')
                    report.AppendLine($"  Drill depth included: {drillDepthIncluded}");
                report.AppendLine();

                processed++;
                if (setId < 'Z') setId++;
            }

            if (processed == 0)
                report.AppendLine($"No {kindName} sets contained region lines.");

            report.AppendLine();
        }

        private static int RemapStartEndKeys(RegionSet set, Dictionary<string, string> canonMap)
        {
            if (set?.PageSnapshot?.Values == null)
                return 0;

            int changed = 0;

            for (int i = 0; i < StartEndKeys.Length; i++)
            {
                string key = StartEndKeys[i];

                if (!set.PageSnapshot.Values.TryGetValue(key, out string oldVal))
                    continue;

                oldVal ??= "";
                string raw = oldVal;

                // strip existing unique tag
                if (TextSearching.TryRemoveUniqueTag(raw, out string noTag))
                    raw = noTag;

                // canonicalize exactly the same way as RegionLines
                string canon = TextSearching.NormalizeTextLineAsIs(raw);
                if (string.IsNullOrWhiteSpace(canon))
                    continue;

                // exact mapping to new stored line (with new tag)
                if (canonMap.TryGetValue(canon, out string newLine) && !string.IsNullOrWhiteSpace(newLine))
                {
                    if (!string.Equals(oldVal, newLine, StringComparison.Ordinal))
                    {
                        set.PageSnapshot.Values[key] = newLine;
                        changed++;
                    }
                }
            }

            return changed;
        }

        private static string BuildUniqueTag(char kindTag, char setId, int seq)
        {
            // "(T:A0000)" etc — no spaces
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0}:{1}{2:0000})",
                char.ToUpperInvariant(kindTag),
                char.ToUpperInvariant(setId),
                seq);
        }

        private static string GetSnapValue(RegionSet set, string key)
        {
            if (set?.PageSnapshot?.Values == null)
                return "";

            return set.PageSnapshot.Values.TryGetValue(key, out string v) ? (v ?? "") : "";
        }
    }
}
