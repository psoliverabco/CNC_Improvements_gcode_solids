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

            // Marker keys (prefer anchored; fallback to blank if index invalid)
            rs.PageSnapshot.Values["PlaneZLineText"] = GetAnchoredOrEmpty(rs, planeZIndex);
            rs.PageSnapshot.Values["StartXLineText"] = GetAnchoredOrEmpty(rs, startXIndex);
            rs.PageSnapshot.Values["StartYLineText"] = GetAnchoredOrEmpty(rs, startYIndex);
            rs.PageSnapshot.Values["EndXLineText"] = GetAnchoredOrEmpty(rs, endXIndex);
            rs.PageSnapshot.Values["EndYLineText"] = GetAnchoredOrEmpty(rs, endYIndex);

            // Params
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
            string? planeZLineRaw = null,
            string? startXLineRaw = null,
            string? startYLineRaw = null,
            string? endXLineRaw = null,
            string? endYLineRaw = null,
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

            rs.PageSnapshot ??= new UiStateSnapshot();

            // defaults first (optional; never replaces the dictionary)
            if (snapshotDefaults != null)
            {
                foreach (var kv in snapshotDefaults)
                    rs.PageSnapshot.Values[kv.Key] = kv.Value ?? string.Empty;
            }

            // Preserve existing UID if present; create when we need identity/anchors.
            string uid = "";
            if (rs.PageSnapshot.Values.TryGetValue("__RegionUid", out string existingUid))
                uid = existingUid ?? "";

            bool needUid =
                (regionLines != null)
                || planeZIndex.HasValue || startXIndex.HasValue || startYIndex.HasValue || endXIndex.HasValue || endYIndex.HasValue
                || planeZLineRaw != null || startXLineRaw != null || startYLineRaw != null || endXLineRaw != null || endYLineRaw != null;

            if (needUid && string.IsNullOrWhiteSpace(uid))
            {
                uid = BuiltRegionNormalizers.NewUidN();
                rs.PageSnapshot.Values["__RegionUid"] = uid;
            }

            // If region text provided, rebuild anchored RegionLines.
            if (regionLines != null)
            {
                rs.RegionLines.Clear();

                for (int i = 0; i < regionLines.Count; i++)
                {
                    string norm = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(regionLines[i]);
                    rs.RegionLines.Add(BuiltRegionNormalizers.BuildAnchoredLine(uid, i + 1, norm));
                }

                // Keep it canonical
                rs.PageSnapshot.Values["__RegionUid"] = uid;
            }

            // Marker keys:
            // - Prefer anchored RegionLines when local index valid
            // - Otherwise store normalized raw (allows PlaneZ outside region span)
            SetMarker(rs, "PlaneZLineText", planeZIndex, planeZLineRaw);
            SetMarker(rs, "StartXLineText", startXIndex, startXLineRaw);
            SetMarker(rs, "StartYLineText", startYIndex, startYLineRaw);
            SetMarker(rs, "EndXLineText", endXIndex, endXLineRaw);
            SetMarker(rs, "EndYLineText", endYIndex, endYLineRaw);

            // Params (only update those provided)
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

        private static void SetMarker(RegionSet rs, string key, int? localIndex0Based, string? rawLine)
        {
            if (rs == null || rs.PageSnapshot == null)
                return;

            if (localIndex0Based.HasValue)
            {
                int idx = localIndex0Based.Value;
                if (idx >= 0 && idx < rs.RegionLines.Count)
                {
                    rs.PageSnapshot.Values[key] = rs.RegionLines[idx] ?? string.Empty;
                    return;
                }

                // local index invalid/outside region
                if (rawLine != null)
                {
                    rs.PageSnapshot.Values[key] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(rawLine);
                    return;
                }

                rs.PageSnapshot.Values[key] = string.Empty;
                return;
            }

            if (rawLine != null)
            {
                rs.PageSnapshot.Values[key] = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(rawLine);
            }
        }

        private static string GetAnchoredOrEmpty(RegionSet rs, int index0Based)
        {
            if (rs == null) return string.Empty;
            if (index0Based < 0 || index0Based >= rs.RegionLines.Count) return string.Empty;
            return rs.RegionLines[index0Based] ?? string.Empty;
        }
    }
}
