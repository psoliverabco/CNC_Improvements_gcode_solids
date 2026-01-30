// File: SetManagement/Builders/BuildMillRegion.cs
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;

namespace CNC_Improvements_gcode_solids.SetManagement.Builders
{
    public static class BuildMillRegion
    {
        // marker indices are 0-based indices into regionLines
        public static RegionSet Create(
            string regionName,
            IReadOnlyList<string> regionLines,
            int planeZIndex,
            int startXIndex,
            int startYIndex,
            int endXIndex,
            int endYIndex,
            string txtToolDia,
            string txtToolLen,
            string fuseAll,
            string removeSplitter,
            string clipper,
            string clipperIsland,
            IReadOnlyDictionary<string, string>? snapshotDefaults = null)
        {
            regionLines ??= Array.Empty<string>();

            string uid = BuiltRegionNormalizers.NewUidN();

            var rs = new RegionSet(RegionSetKind.Mill, regionName ?? string.Empty);

            // RegionLines anchored
            for (int i = 0; i < regionLines.Count; i++)
            {
                string norm = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(regionLines[i]);
                rs.RegionLines.Add(BuiltRegionNormalizers.BuildAnchoredLine(uid, i + 1, norm));
            }

            rs.PageSnapshot = new UiStateSnapshot();

            // defaults first
            if (snapshotDefaults != null)
            {
                foreach (var kv in snapshotDefaults)
                    rs.PageSnapshot.Values[kv.Key] = kv.Value ?? string.Empty;
            }

            // canonical MILL keys
            rs.PageSnapshot.Values["__RegionUid"] = uid;

            // Going forward: PlaneZLineText anchored too
            rs.PageSnapshot.Values["PlaneZLineText"] = GetAnchored(rs, planeZIndex);

            rs.PageSnapshot.Values["StartXLineText"] = GetAnchored(rs, startXIndex);
            rs.PageSnapshot.Values["StartYLineText"] = GetAnchored(rs, startYIndex);
            rs.PageSnapshot.Values["EndXLineText"] = GetAnchored(rs, endXIndex);
            rs.PageSnapshot.Values["EndYLineText"] = GetAnchored(rs, endYIndex);

            rs.PageSnapshot.Values["TxtToolDia"] = txtToolDia ?? string.Empty;
            rs.PageSnapshot.Values["TxtToolLen"] = txtToolLen ?? string.Empty;

            rs.PageSnapshot.Values["Fuseall"] = fuseAll ?? string.Empty;
            rs.PageSnapshot.Values["RemoveSplitter"] = removeSplitter ?? string.Empty;
            rs.PageSnapshot.Values["Clipper"] = clipper ?? string.Empty;
            rs.PageSnapshot.Values["ClipperIsland"] = clipperIsland ?? string.Empty;

            return rs;
        }

        public static void EditExisting(
    RegionSet rs,
    IReadOnlyList<string>? regionLines = null,
    int? planeZIndex = null,
    int? startXIndex = null,
    int? startYIndex = null,
    int? endXIndex = null,
    int? endYIndex = null,
    string? txtToolDia = null,
    string? txtToolLen = null,
    string? fuseAll = null,
    string? removeSplitter = null,
    string? clipper = null,
    string? clipperIsland = null,
    IReadOnlyDictionary<string, string>? snapshotDefaults = null)
        {
            if (rs == null)
                throw new ArgumentNullException(nameof(rs));

            // Ensure snapshot exists (Values is a live dictionary; do NOT assign it)
            rs.PageSnapshot ??= new UiStateSnapshot();

            // defaults first (optional; never replaces the dictionary)
            if (snapshotDefaults != null)
            {
                foreach (var kv in snapshotDefaults)
                    rs.PageSnapshot.Values[kv.Key] = kv.Value ?? string.Empty;
            }

            // If region text provided, rebuild anchored RegionLines.
            // Preserve existing UID if present, otherwise create one.
            if (regionLines != null)
            {
                string uid = "";
                if (rs.PageSnapshot.Values.TryGetValue("__RegionUid", out string existingUid))
                    uid = existingUid ?? "";

                if (string.IsNullOrWhiteSpace(uid))
                    uid = BuiltRegionNormalizers.NewUidN();

                rs.RegionLines.Clear();

                for (int i = 0; i < regionLines.Count; i++)
                {
                    string norm = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(regionLines[i]);
                    rs.RegionLines.Add(BuiltRegionNormalizers.BuildAnchoredLine(uid, i + 1, norm));
                }

                // Keep it canonical
                rs.PageSnapshot.Values["__RegionUid"] = uid;
            }

            // Marker lines are stored as ANCHORED region lines (must exist to anchor)
            if (planeZIndex.HasValue)
                rs.PageSnapshot.Values["PlaneZLineText"] = GetAnchored(rs, planeZIndex.Value);

            if (startXIndex.HasValue)
                rs.PageSnapshot.Values["StartXLineText"] = GetAnchored(rs, startXIndex.Value);

            if (startYIndex.HasValue)
                rs.PageSnapshot.Values["StartYLineText"] = GetAnchored(rs, startYIndex.Value);

            if (endXIndex.HasValue)
                rs.PageSnapshot.Values["EndXLineText"] = GetAnchored(rs, endXIndex.Value);

            if (endYIndex.HasValue)
                rs.PageSnapshot.Values["EndYLineText"] = GetAnchored(rs, endYIndex.Value);

            // Param keys (only update those provided)
            if (txtToolDia != null)
                rs.PageSnapshot.Values["TxtToolDia"] = txtToolDia;

            if (txtToolLen != null)
                rs.PageSnapshot.Values["TxtToolLen"] = txtToolLen;

            if (fuseAll != null)
                rs.PageSnapshot.Values["Fuseall"] = fuseAll;

            if (removeSplitter != null)
                rs.PageSnapshot.Values["RemoveSplitter"] = removeSplitter;

            if (clipper != null)
                rs.PageSnapshot.Values["Clipper"] = clipper;

            if (clipperIsland != null)
                rs.PageSnapshot.Values["ClipperIsland"] = clipperIsland;
        }



        private static string GetAnchored(RegionSet rs, int index0Based)
        {
            if (rs == null) return string.Empty;
            if (index0Based < 0 || index0Based >= rs.RegionLines.Count) return string.Empty;
            return rs.RegionLines[index0Based] ?? string.Empty;
        }
    }
}
