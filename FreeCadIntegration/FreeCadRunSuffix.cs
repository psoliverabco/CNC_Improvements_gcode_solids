using System.Threading;

namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    /// <summary>
    /// Hands out monotonically increasing suffix numbers for FreeCAD scripts.
    /// Goal: never overwrite the script file that another FreeCADCmd process is still reading.
    /// </summary>
    internal static class FreeCadRunSuffix
    {
        private static int _turn;
        private static int _mill;
        private static int _millclipper;
        private static int _drill;
        private static int _merge;

        public static void ResetAll()
        {
            _turn = 0;
            _mill = 0;
            _millclipper = 0;
            _drill = 0;
            _merge = 0;
        }

        public static void ResetTurn() => _turn = 0;
        public static void ResetMill() => _mill = 0;

        public static void ResetMillClipper() => _millclipper = 0;
        public static void ResetDrill() => _drill = 0;
        public static void ResetMerge() => _merge = 0;

        // Returns 0,1,2,3...
        public static int NextTurn() => Interlocked.Increment(ref _turn) - 1;
        public static int NextMill() => Interlocked.Increment(ref _mill) - 1;

        public static int NextMillClipper() => Interlocked.Increment(ref _millclipper) - 1;
        public static int NextDrill() => Interlocked.Increment(ref _drill) - 1;
        public static int NextMerge() => Interlocked.Increment(ref _merge) - 1;
    }
}
