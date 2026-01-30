// File: SetManagement/Builders/BuildTurnRegion.cs
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CNC_Improvements_gcode_solids.SetManagement.Builders
{
    public static class BuildTurnRegion
    {
        // marker indices are 0-based indices into regionLines
        public static RegionSet Create(
            string regionName,
            IReadOnlyList<string> regionLines,
            int startXIndex,
            int startZIndex,
            int endXIndex,
            int endZIndex,
            string toolUsage,
            string quadrant,
            string txtZExt,
            string nRad,
            IReadOnlyDictionary<string, string>? snapshotDefaults = null)
        {
            regionLines ??= Array.Empty<string>();

            string uid = BuiltRegionNormalizers.NewUidN();

            var rs = new RegionSet(RegionSetKind.Turn, regionName ?? string.Empty);

            // RegionLines anchored: #uid,local#<Normalized>
            for (int i = 0; i < regionLines.Count; i++)
            {
                string norm = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(regionLines[i]);
                rs.RegionLines.Add(BuiltRegionNormalizers.BuildAnchoredLine(uid, i + 1, norm));
            }

            rs.PageSnapshot = new UiStateSnapshot();

            ApplySnapshotDefaultsAndRequiredKeys(
                rs,
                snapshotDefaults,
                toolUsage,
                quadrant,
                txtZExt,
                nRad,
                startXIndex,
                startZIndex,
                endXIndex,
                endZIndex);

            return rs;
        }

        /// <summary>
        /// The "only way" to change a region:
        /// - If region exists in turnSets by regionName -> edit it in-place (preserving UID)
        /// - If not -> create and add it
        /// </summary>
        public static RegionSet Edit(
            IList<RegionSet> turnSets,
            string regionName,
            IReadOnlyList<string>? regionLines = null,
            int? startXIndex = null,
            int? startZIndex = null,
            int? endXIndex = null,
            int? endZIndex = null,
            string? toolUsage = null,
            string? quadrant = null,
            string? txtZExt = null,
            string? nRad = null,
            IReadOnlyDictionary<string, string>? snapshotDefaults = null)
        {
            if (turnSets == null)
                throw new ArgumentNullException(nameof(turnSets));

            regionName ??= string.Empty;

            // Find existing by name (case-insensitive)
            RegionSet? existing = null;
            for (int i = 0; i < turnSets.Count; i++)
            {
                var rs = turnSets[i];
                if (rs == null) continue;

                // RegionSet name property is constructor-provided; most builds use RegionName or Name.
                // We avoid guessing property names by using ToString() as fallback is risky,
                // so we match by the constructor-supplied name if RegionSet exposes it via .Name.
                // If your RegionSet uses a different property, update GetRegionName(rs) only.
                if (string.Equals(GetRegionName(rs), regionName, StringComparison.OrdinalIgnoreCase))
                {
                    existing = rs;
                    break;
                }
            }

            if (existing == null)
            {
                // Create new (UID will be new)
                var created = Create(
                    regionName: regionName,
                    regionLines: regionLines ?? Array.Empty<string>(),
                    startXIndex: startXIndex ?? -1,
                    startZIndex: startZIndex ?? -1,
                    endXIndex: endXIndex ?? -1,
                    endZIndex: endZIndex ?? -1,
                    toolUsage: toolUsage ?? string.Empty,
                    quadrant: quadrant ?? string.Empty,
                    txtZExt: txtZExt ?? string.Empty,
                    nRad: nRad ?? string.Empty,
                    snapshotDefaults: snapshotDefaults);

                turnSets.Add(created);
                return created;
            }

            // Edit in-place (preserve UID)
            return EditExisting(
                existing,
                regionLines,
                startXIndex,
                startZIndex,
                endXIndex,
                endZIndex,
                toolUsage,
                quadrant,
                txtZExt,
                nRad,
                snapshotDefaults);
        }

        /// <summary>
        /// Edit an already-existing RegionSet in-place, preserving its UID if possible.
        /// Pass null for any argument you do NOT want to change.
        /// </summary>
        public static RegionSet EditExisting(
            RegionSet rs,
            IReadOnlyList<string>? regionLines = null,
            int? startXIndex = null,
            int? startZIndex = null,
            int? endXIndex = null,
            int? endZIndex = null,
            string? toolUsage = null,
            string? quadrant = null,
            string? txtZExt = null,
            string? nRad = null,
            IReadOnlyDictionary<string, string>? snapshotDefaults = null)
        {
            if (rs == null)
                throw new ArgumentNullException(nameof(rs));

            // Ensure snapshot exists
            if (rs.PageSnapshot == null)
                rs.PageSnapshot = new UiStateSnapshot();

            // 1) Update RegionLines (if caller provided new list)
            if (regionLines != null)
            {
                // Try to preserve UID from existing anchored lines
                string uid = TryExtractUidFromRegion(rs) ?? BuiltRegionNormalizers.NewUidN();

                rs.RegionLines.Clear();

                for (int i = 0; i < regionLines.Count; i++)
                {
                    string norm = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(regionLines[i]);
                    rs.RegionLines.Add(BuiltRegionNormalizers.BuildAnchoredLine(uid, i + 1, norm));
                }
            }

            // 2) Apply snapshot defaults (if provided)
            if (snapshotDefaults != null)
            {
                foreach (var kv in snapshotDefaults)
                    rs.PageSnapshot.Values[kv.Key] = kv.Value ?? string.Empty;
            }

            // 3) Apply required TURN keys only if provided (null means "leave as-is")
            if (toolUsage != null) rs.PageSnapshot.Values["__ToolUsage"] = toolUsage;
            if (quadrant != null) rs.PageSnapshot.Values["__Quadrant"] = quadrant;
            if (txtZExt != null) rs.PageSnapshot.Values["TxtZExt"] = txtZExt;
            if (nRad != null) rs.PageSnapshot.Values["NRad"] = nRad;

            // 4) Marker snapshot lines (only if indices provided)
            if (startXIndex.HasValue) rs.PageSnapshot.Values["__StartXLine"] = GetAnchored(rs, startXIndex.Value);
            if (startZIndex.HasValue) rs.PageSnapshot.Values["__StartZLine"] = GetAnchored(rs, startZIndex.Value);
            if (endXIndex.HasValue) rs.PageSnapshot.Values["__EndXLine"] = GetAnchored(rs, endXIndex.Value);
            if (endZIndex.HasValue) rs.PageSnapshot.Values["__EndZLine"] = GetAnchored(rs, endZIndex.Value);

            return rs;
        }

        // -------------------- helpers --------------------

        private static void ApplySnapshotDefaultsAndRequiredKeys(
            RegionSet rs,
            IReadOnlyDictionary<string, string>? snapshotDefaults,
            string toolUsage,
            string quadrant,
            string txtZExt,
            string nRad,
            int startXIndex,
            int startZIndex,
            int endXIndex,
            int endZIndex)
        {
            // defaults first
            if (snapshotDefaults != null)
            {
                foreach (var kv in snapshotDefaults)
                    rs.PageSnapshot.Values[kv.Key] = kv.Value ?? string.Empty;
            }

            // required TURN keys
            rs.PageSnapshot.Values["__ToolUsage"] = toolUsage ?? string.Empty;
            rs.PageSnapshot.Values["__Quadrant"] = quadrant ?? string.Empty;
            rs.PageSnapshot.Values["TxtZExt"] = txtZExt ?? string.Empty;
            rs.PageSnapshot.Values["NRad"] = nRad ?? string.Empty;

            // anchored marker lines (must be anchored)
            rs.PageSnapshot.Values["__StartXLine"] = GetAnchored(rs, startXIndex);
            rs.PageSnapshot.Values["__StartZLine"] = GetAnchored(rs, startZIndex);
            rs.PageSnapshot.Values["__EndXLine"] = GetAnchored(rs, endXIndex);
            rs.PageSnapshot.Values["__EndZLine"] = GetAnchored(rs, endZIndex);
        }

        private static string GetAnchored(RegionSet rs, int index0Based)
        {
            if (rs == null) return string.Empty;
            if (index0Based < 0 || index0Based >= rs.RegionLines.Count) return string.Empty;
            return rs.RegionLines[index0Based] ?? string.Empty;
        }

        /// <summary>
        /// Extract UID from the first anchored line: "#uid,local#..."
        /// Returns null if not parseable.
        /// </summary>
        private static string? TryExtractUidFromRegion(RegionSet rs)
        {
            if (rs == null || rs.RegionLines == null || rs.RegionLines.Count == 0)
                return null;

            // Prefer first non-empty line
            string? first = null;
            for (int i = 0; i < rs.RegionLines.Count; i++)
            {
                var s = rs.RegionLines[i];
                if (!string.IsNullOrWhiteSpace(s)) { first = s; break; }
            }
            if (string.IsNullOrWhiteSpace(first))
                return null;

            return TryExtractUidFromAnchoredLine(first);
        }

        private static string? TryExtractUidFromAnchoredLine(string anchored)
        {
            // Expected: "#<uid>,<n>#<payload>"
            if (string.IsNullOrWhiteSpace(anchored))
                return null;

            anchored = anchored.Trim();
            if (!anchored.StartsWith("#", StringComparison.Ordinal))
                return null;

            int comma = anchored.IndexOf(',', 1);
            if (comma <= 1)
                return null;

            // sanity: must have a second '#'
            int hash2 = anchored.IndexOf('#', comma + 1);
            if (hash2 < 0)
                return null;

            string uid = anchored.Substring(1, comma - 1).Trim();
            if (uid.Length == 0)
                return null;

            return uid;
        }

        /// <summary>
        /// Centralize how we read the RegionSet name, so if your RegionSet uses a different property,
        /// you only edit this one method.
        /// </summary>
        private static string GetRegionName(RegionSet rs)
        {
            // Common patterns: rs.Name or rs.RegionName.
            // We avoid reflection (keeps it clean + fast). If your property name differs, change this.
            // If your RegionSet does NOT expose a name property, you must add one.
            try
            {
                // If RegionSet has Name
                var nameProp = rs.GetType().GetProperty("Name");
                if (nameProp != null && nameProp.PropertyType == typeof(string))
                {
                    return (string?)nameProp.GetValue(rs) ?? string.Empty;
                }

                // If RegionSet has RegionName
                var rnProp = rs.GetType().GetProperty("RegionName");
                if (rnProp != null && rnProp.PropertyType == typeof(string))
                {
                    return (string?)rnProp.GetValue(rs) ?? string.Empty;
                }
            }
            catch
            {
                // ignore
            }

            return string.Empty;
        }
    }
}
