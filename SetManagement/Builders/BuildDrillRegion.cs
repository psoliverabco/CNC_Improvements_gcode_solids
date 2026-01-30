// File: SetManagement/Builders/BuildDrillRegion.cs
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;

namespace CNC_Improvements_gcode_solids.SetManagement.Builders
{
    public static class BuildDrillRegion
    {
        // marker indices are 0-based indices into regionLines
        public static RegionSet Create(
            string regionName,
            IReadOnlyList<string> regionLines,
            int drillDepthIndex,
            string coordMode,
            string txtChamfer,
            string txtHoleDia,
            string txtPointAngle,
            string txtZHoleTop,
            string txtZPlusExt,
            IReadOnlyDictionary<string, string>? snapshotDefaults = null)
        {
            regionLines ??= Array.Empty<string>();

            string uid = BuiltRegionNormalizers.NewUidN();

            var rs = new RegionSet(RegionSetKind.Drill, regionName ?? string.Empty);

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

            // DRILL keys
            rs.PageSnapshot.Values["CoordMode"] = coordMode ?? string.Empty;
            rs.PageSnapshot.Values["DrillDepthLineText"] = GetAnchored(rs, drillDepthIndex); // anchored

            rs.PageSnapshot.Values["TxtChamfer"] = txtChamfer ?? string.Empty;
            rs.PageSnapshot.Values["TxtHoleDia"] = txtHoleDia ?? string.Empty;
            rs.PageSnapshot.Values["TxtPointAngle"] = txtPointAngle ?? string.Empty;
            rs.PageSnapshot.Values["TxtZHoleTop"] = txtZHoleTop ?? string.Empty;
            rs.PageSnapshot.Values["TxtZPlusExt"] = txtZPlusExt ?? string.Empty;

            return rs;
        }

        public static void EditExisting(
    RegionSet rs,
    IReadOnlyList<string>? regionLines = null,
    int? drillDepthIndex = null,
    string? coordMode = null,
    string? txtChamfer = null,
    string? txtHoleDia = null,
    string? txtPointAngle = null,
    string? txtZHoleTop = null,
    string? txtZPlusExt = null,
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
            // Drill has no required stored UID key right now, so we preserve any existing one if you add it later.
            // For now, we generate a new UID only when we rebuild RegionLines.
            if (regionLines != null)
            {
                string uid = BuiltRegionNormalizers.NewUidN();

                rs.RegionLines.Clear();

                for (int i = 0; i < regionLines.Count; i++)
                {
                    string norm = BuiltRegionNormalizers.NormalizeTextLineToGcodeAndEndTag(regionLines[i]);
                    rs.RegionLines.Add(BuiltRegionNormalizers.BuildAnchoredLine(uid, i + 1, norm));
                }
            }

            // Drill depth marker is stored as an ANCHORED region line (must exist to anchor)
            if (drillDepthIndex.HasValue)
                rs.PageSnapshot.Values["DrillDepthLineText"] = GetAnchored(rs, drillDepthIndex.Value);

            // Params (only update those provided)
            if (coordMode != null)
                rs.PageSnapshot.Values["CoordMode"] = coordMode;

            if (txtChamfer != null)
                rs.PageSnapshot.Values["TxtChamfer"] = txtChamfer;

            if (txtHoleDia != null)
                rs.PageSnapshot.Values["TxtHoleDia"] = txtHoleDia;

            if (txtPointAngle != null)
                rs.PageSnapshot.Values["TxtPointAngle"] = txtPointAngle;

            if (txtZHoleTop != null)
                rs.PageSnapshot.Values["TxtZHoleTop"] = txtZHoleTop;

            if (txtZPlusExt != null)
                rs.PageSnapshot.Values["TxtZPlusExt"] = txtZPlusExt;
        }

        private static string GetAnchored(RegionSet rs, int index0Based)
        {
            if (rs == null) return string.Empty;
            if (index0Based < 0 || index0Based >= rs.RegionLines.Count) return string.Empty;
            return rs.RegionLines[index0Based] ?? string.Empty;
        }
    }
}
