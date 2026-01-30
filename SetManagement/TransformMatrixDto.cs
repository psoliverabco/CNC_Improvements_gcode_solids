using System.Collections.Generic;

namespace CNC_Improvements_gcode_solids.SetManagement
{
    // Stored in project file as an independent block (separate from RegionSet save/load)
    public sealed class TransformMatrixDto
    {
        public string MatrixName { get; set; } = "";

        public string RotZ { get; set; } = "0";
        public string RotY { get; set; } = "0";
        public string Tx { get; set; } = "0";
        public string Ty { get; set; } = "0";
        public string Tz { get; set; } = "0";

        public bool IsLocked { get; set; } = false;

        public List<string> Regions { get; set; } = new List<string>();
    }
}
