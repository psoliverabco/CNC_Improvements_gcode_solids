// File: Utilities/CodeCleanup.cs
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// One-shot cleanup for TURN + MILL + DRILL:
    /// - Strip CNC comments "( ... )" everywhere EXCEPT the unique end-tag we own.
    /// - Rebuild anchors "#uid,n#" (n starts at 1 and increments by 1).
    /// - Rebuild unique end-tags using the canonical scheme:
    ///     Turn: (t:a0000..), next turn set (t:b0000..), etc.
    ///     Mill: (m:a0000..), next mill set (m:b0000..), etc.
    ///     Drill:(d:a0000..), next drill set(d:b0000..), etc.
    ///
    /// IMPORTANT:
    /// - Updates RegionSets IN-PLACE (RegionLines + snapshot anchor values if present).
    /// - NO SEARCH: snapshot anchor values are remapped by original anchor N -> new anchor N (positional).
    /// - Emits editor text with "(NAME ST)" / "(NAME END)" markers (output only).
    /// </summary>
    internal static class CodeCleanup
    {
        // Match a trailing unique tag we own: (u:...), (t:...), (m:...), (d:...)
        // NOTE: we accept any digit count on input so we can clean legacy formats too.
        private static readonly Regex RxTrailingUniqueTag =
            new Regex(@"\((u|t|m|d):[a-z]+[0-9]+\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Match any "( ... )" block (CNC comments, etc.)
        private static readonly Regex RxAnyParenBlock =
            new Regex(@"\([^)]*\)", RegexOptions.Compiled);

        // Optional numbered prefix like "1234:"
        private static readonly Regex RxLineNumberPrefix =
            new Regex(@"^\s*\d+\s*:\s*", RegexOptions.Compiled);

        // Leading anchor "#uid,n#"
        private static readonly Regex RxLeadingAnchor =
            new Regex(@"^\s*#([^#]+)#", RegexOptions.Compiled);

        public static string BuildAndApplyAllCleanup(
            IList<RegionSet> turnSets,
            IList<RegionSet> millSets,
            IList<RegionSet> drillSets,
            out string newEditorText)
        {
            var report = new StringBuilder(32_768);
            var editor = new StringBuilder(512_000);

            report.AppendLine("=== ONE-SHOT CLEANUP: TURN + MILL + DRILL ===");
            report.AppendLine("Rules:");
            report.AppendLine(" - Strip CNC comment parens ( ... ) except our unique end-tag");
            report.AppendLine(" - Rebuild anchors as #uid,n# (n starts at 1)");
            report.AppendLine(" - Rebuild end-tags as:");
            report.AppendLine("     TURN: (t:a0000..), next set (t:b0000..), etc.");
            report.AppendLine("     MILL: (m:a0000..), next set (m:b0000..), etc.");
            report.AppendLine("     DRILL:(d:a0000..), next set (d:b0000..), etc.");
            report.AppendLine("------------------------------------------------------------");
            report.AppendLine();

            int turnTouched = CleanupSetList(turnSets, typePrefix: 't', report, editor, "TURN");
            int millTouched = CleanupSetList(millSets, typePrefix: 'm', report, editor, "MILL");
            int drillTouched = CleanupSetList(drillSets, typePrefix: 'd', report, editor, "DRILL");

            report.AppendLine();
            report.AppendLine("------------------------------------------------------------");
            report.AppendLine($"DONE. Sets touched: TURN={turnTouched}, MILL={millTouched}, DRILL={drillTouched}");
            report.AppendLine("------------------------------------------------------------");

            newEditorText = editor.ToString().Replace("\r\n", "\n");
            return report.ToString();
        }

        private static int CleanupSetList(
            IList<RegionSet> sets,
            char typePrefix,
            StringBuilder report,
            StringBuilder editor,
            string title)
        {
            if (sets == null || sets.Count == 0)
            {
                report.AppendLine($"[{title}] no sets.");
                return 0;
            }

            int touched = 0;

            for (int setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                var set = sets[setIndex];
                if (set == null) continue;

                string setName = (set.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(setName))
                    setName = $"{title}-{setIndex + 1}";

                // Per-type set letter: a, b, c... (excel-style if needed)
                string setLetter = ToAlphaIndex(setIndex); // a, b, ... z, aa, ab...

                int beforeCount = set.RegionLines?.Count ?? 0;
                if (beforeCount == 0)
                {
                    report.AppendLine($"[{title}] {setName}: RegionLines empty.");
                    continue;
                }

                // Keep uid if present, else new
                string uid = ExtractUidFromRegionLines(set.RegionLines);
                if (string.IsNullOrWhiteSpace(uid))
                    uid = Guid.NewGuid().ToString("N");

                // Clean + rebuild region lines
                int removedCommentBlocks = 0;
                int removedBlank = 0;

                var cleanedCore = new List<string>(beforeCount);
                var keptOldNs = new List<int>(beforeCount); // original anchor N for each kept line (or -1)

                foreach (var raw in set.RegionLines)
                {
                    string line = StripNumberPrefix(raw ?? "");

                    // capture original anchor N (positional identity)
                    int oldN = TryParseAnchorN(line, out int parsedN) ? parsedN : -1;

                    line = StripLeadingAnchor(line).Trim();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        removedBlank++;
                        continue;
                    }

                    // preserve trailing unique tag if present so it doesn't get removed as a comment block
                    ExtractTrailingUniqueTag(line, out bool hadTag);

                    // remove *all* other ( ... ) blocks (this will also remove any legacy inline comments)
                    // but if a trailing unique tag exists, we remove it first then append later (we rebuild anyway)
                    line = StripTrailingUniqueTag(line).TrimEnd();

                    int beforeParenCount = CountParenBlocks(line);
                    line = RemoveAllParenBlocks(line);
                    int afterParenCount = CountParenBlocks(line);
                    removedCommentBlocks += Math.Max(0, beforeParenCount - afterParenCount);

                    line = line.Trim();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        removedBlank++;
                        continue;
                    }

                    cleanedCore.Add(line);
                    keptOldNs.Add(oldN);
                }

                // Rebuild anchored lines + new tags
                var rebuilt = new List<string>(cleanedCore.Count);
                var oldNToNewN = new Dictionary<int, int>(); // oldN -> newN (both 1-based)

                for (int i = 0; i < cleanedCore.Count; i++)
                {
                    string core = cleanedCore[i];

                    // numeric 0000.. increments by 1 per line in this set
                    string num = i.ToString("0000");
                    string newTag = $"({typePrefix}:{setLetter}{num})";

                    // Ensure no legacy tag remains
                    core = StripTrailingUniqueTag(core).TrimEnd();

                    // Rebuild canonical anchor "#uid,n#"
                    int newN = i + 1;
                    string anchored = $"#{uid},{newN}#{core}{newTag}";
                    rebuilt.Add(anchored);

                    int oldN = (i >= 0 && i < keptOldNs.Count) ? keptOldNs[i] : -1;
                    if (oldN > 0 && !oldNToNewN.ContainsKey(oldN))
                        oldNToNewN.Add(oldN, newN);
                }

                // Apply in-place (RegionLines is read-only property, but the collection is mutable)
                if (set.RegionLines == null)
                    throw new InvalidOperationException("RegionSet.RegionLines is null (expected an initialized mutable list/collection).");

                set.RegionLines.Clear();
                for (int i = 0; i < rebuilt.Count; i++)
                    set.RegionLines.Add(rebuilt[i]);

                // Snapshot anchor remap: NO SEARCH. Use old anchor N -> new anchor N mapping.
                RetagSnapshotAnchorsByIndexMap(set, oldNToNewN, rebuilt);

                touched++;

                // Emit editor block (output-only markers)
                editor.AppendLine($"({setName} ST)");
                foreach (var line in rebuilt)
                    editor.AppendLine(GeneralNormalizers.NormalizeInsertLineAlignEndTag(line, 75));
                editor.AppendLine($"({setName} END)");
                editor.AppendLine();

                report.AppendLine($"[{title}] {setName}:");
                report.AppendLine($"  Lines in : {beforeCount}");
                report.AppendLine($"  Lines out: {rebuilt.Count}");
                report.AppendLine($"  Removed blank: {removedBlank}");
                report.AppendLine($"  Removed comment blocks: {removedCommentBlocks}");
                report.AppendLine($"  UID: {uid}");
                report.AppendLine($"  Tag: ({typePrefix}:{setLetter}0000..)  (numeric increments by 1)");
                report.AppendLine();
            }

            return touched;
        }

        /// <summary>
        /// Remap any snapshot value(s) that contain anchored lines "#uid,n#..."
        /// using positional mapping oldN->newN. No text search.
        ///
        /// - For each anchored snapshot line, we replace it with rebuilt[newN-1] (full anchored line with new tag).
        /// - If oldN not found in map, we leave that line unchanged.
        /// </summary>
        private static void RetagSnapshotAnchorsByIndexMap(RegionSet set, Dictionary<int, int> oldNToNewN, List<string> rebuilt)
        {
            if (set?.PageSnapshot?.Values == null) return;
            if (oldNToNewN == null || oldNToNewN.Count == 0) return;
            if (rebuilt == null || rebuilt.Count == 0) return;

            var keys = set.PageSnapshot.Values.Keys.ToList();
            for (int k = 0; k < keys.Count; k++)
            {
                string key = keys[k];
                if (key == null) continue;

                if (!set.PageSnapshot.Values.TryGetValue(key, out string raw)) continue;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // If this snapshot value isn't an anchored-line string, skip quickly
                // (we only touch values containing "#", but we still validate anchor parse before changing).
                if (!raw.Contains("#"))
                    continue;

                string norm = raw.Replace("\r\n", "\n").Replace("\r", "\n");

                // MULTI-LINE: rewrite each anchored line independently
                if (norm.Contains("\n"))
                {
                    var lines = norm.Split('\n');
                    bool changed = false;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string s = (lines[i] ?? "").Trim();
                        if (s.Length == 0) continue;

                        if (!TryParseAnchorN(s, out int oldN)) continue;
                        if (oldN <= 0) continue;

                        if (!oldNToNewN.TryGetValue(oldN, out int newN)) continue;
                        if (newN <= 0 || newN > rebuilt.Count) continue;

                        lines[i] = rebuilt[newN - 1];
                        changed = true;
                    }

                    if (changed)
                        set.PageSnapshot.Values[key] = string.Join("\n", lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
                }
                else
                {
                    // SINGLE-LINE
                    string s = norm.Trim();
                    if (!TryParseAnchorN(s, out int oldN)) continue;
                    if (oldN <= 0) continue;

                    if (!oldNToNewN.TryGetValue(oldN, out int newN)) continue;
                    if (newN <= 0 || newN > rebuilt.Count) continue;

                    set.PageSnapshot.Values[key] = rebuilt[newN - 1];
                }
            }
        }

        private static bool TryParseAnchorN(string s, out int n)
        {
            n = -1;
            if (string.IsNullOrWhiteSpace(s)) return false;

            string t = s.TrimStart();
            if (!t.StartsWith("#", StringComparison.Ordinal)) return false;

            int hash2 = t.IndexOf('#', 1);
            if (hash2 < 0) return false;

            // between first # and second # we expect "uid,n" or "uid"
            string inside = t.Substring(1, hash2 - 1);
            int comma = inside.LastIndexOf(',');
            if (comma < 0) return false;

            string nText = inside.Substring(comma + 1).Trim();
            if (!int.TryParse(nText, out n)) return false;
            return n > 0;
        }

        private static string StripNumberPrefix(string s)
        {
            if (s == null) return "";
            return RxLineNumberPrefix.Replace(s, "");
        }

        private static string StripLeadingAnchor(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (!s.TrimStart().StartsWith("#", StringComparison.Ordinal))
                return s;

            var m = RxLeadingAnchor.Match(s);
            if (!m.Success) return s;

            // remove "#...#" (first anchor only)
            int end = m.Index + m.Length;
            if (end >= 0 && end <= s.Length)
                return s.Substring(end);
            return s;
        }

        private static string ExtractUidFromRegionLines(IList<string> regionLines)
        {
            if (regionLines == null || regionLines.Count == 0) return "";
            for (int i = 0; i < regionLines.Count; i++)
            {
                string s = regionLines[i] ?? "";
                s = s.Trim();
                if (!s.StartsWith("#", StringComparison.Ordinal)) continue;

                // "#uid,n#..."
                int comma = s.IndexOf(',', 1);
                int hash2 = s.IndexOf('#', 1);
                if (comma > 1 && hash2 > comma)
                    return s.Substring(1, comma - 1).Trim();

                // fallback: "#uid#..."
                if (hash2 > 1)
                    return s.Substring(1, hash2 - 1).Trim();
            }
            return "";
        }

        private static void ExtractTrailingUniqueTag(string s, out bool hadTag)
        {
            hadTag = false;
            if (string.IsNullOrEmpty(s)) return;
            var m = RxTrailingUniqueTag.Match(s);
            hadTag = m.Success;
        }

        private static string StripTrailingUniqueTag(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return RxTrailingUniqueTag.Replace(s, "").TrimEnd();
        }

        private static int CountParenBlocks(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return RxAnyParenBlock.Matches(s).Count;
        }

        private static string RemoveAllParenBlocks(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // remove all "(...)" blocks
            return RxAnyParenBlock.Replace(s, "");
        }

        // 0->a, 1->b ... 25->z, 26->a ... (ROLL OVER, NOT excel-style aa/ab)
        private static string ToAlphaIndex(int index)
        {
            if (index < 0) index = 0;
            index %= 26; // roll over after z
            return ((char)('a' + index)).ToString();
        }

    }
}
