using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// Centralized debug/diagnostic colours (NOT user settings).
    /// Used by MillViewWindow clipper/step visualization.
    /// </summary>
    internal static class DebugPalette
    {
        public static readonly Brush StrokeSelected = Freeze(Brushes.Orange);

        // Clipper step visualisation (keep fixed)
        public static readonly Brush ClipperStrokeOutside = Freeze(Brushes.BlueViolet);
        public static readonly Brush ClipperStrokeIsland = Freeze(Brushes.BlueViolet);
        public static readonly Brush ClipperFillOutside = Freeze(Brushes.WhiteSmoke);
        public static readonly Brush ClipperFillIslandInside = Freeze(Brushes.Black);

        public static readonly Brush ClipperGood = Freeze(Brushes.Lime);
        public static readonly Brush ClipperBad = Freeze(Brushes.Red);

        public static readonly Brush WireOuterStroke = Freeze(Brushes.White);
        public static readonly Brush WireOuterFill = Freeze(Brushes.SlateGray);
        public static readonly Brush WireIslandStroke = Freeze(Brushes.White);
        public static readonly Brush WireIslandFill = Freeze(Brushes.Black);

        private static Brush Freeze(Brush b)
        {
            if (b.CanFreeze) b.Freeze();
            return b;
        }
    }
}
