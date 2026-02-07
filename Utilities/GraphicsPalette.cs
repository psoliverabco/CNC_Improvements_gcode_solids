using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// Single source of truth for user-configurable graphics colours and widths.
    /// </summary>
    internal static class GraphicsPalette
    {
        private static double ClampWidth(double w, double fallback)
        {
            if (!double.IsFinite(w)) return fallback;
            if (w <= 0) return fallback;
            return w;
        }

        private static Brush SafeHexBrush(string? value, Brush fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            try
            {
                var b = UiUtilities.HexBrush(value);
                if (b.CanFreeze) b.Freeze();
                return b;
            }
            catch
            {
                return fallback;
            }
        }

        // -------------------- Brushes --------------------

        public static Brush ProfileBrush => SafeHexBrush(Settings.Default.ProfileColor, Brushes.Yellow);
        public static Brush OffsetBrush => SafeHexBrush(Settings.Default.OffsetColor, Brushes.Orange);
        public static Brush ClosingBrush => SafeHexBrush(Settings.Default.ClosingColor, Brushes.Gray);
        public static Brush GraphicTextBrush => SafeHexBrush(Settings.Default.GraphicTextColor, Brushes.Yellow);
        public static Brush CLBrush => SafeHexBrush(Settings.Default.CLColor, Brushes.Magenta);
        public static Brush GridBrush => SafeHexBrush(Settings.Default.GridColor, Brushes.Gray);

        // -------------------- Widths --------------------

        public static double ProfileWidth => ClampWidth(Settings.Default.ProfileWidth, 1.0);
        public static double OffsetWidth => ClampWidth(Settings.Default.OffsetWidth, 1.0);
        public static double ClosingWidth => ClampWidth(Settings.Default.ClosingWidth, 1.0);
        public static double CLWidth => ClampWidth(Settings.Default.CLWidth, 2.0);
        public static double GridWidth => ClampWidth(Settings.Default.GridWidth, 1.0);

        // -------------------- Pens --------------------

        public static Pen ProfilePen => CreatePen(ProfileBrush, ProfileWidth);
        public static Pen OffsetPen => CreatePen(OffsetBrush, OffsetWidth);
        public static Pen ClosingPen => CreatePen(ClosingBrush, ClosingWidth);
        public static Pen CLPen => CreatePen(CLBrush, CLWidth);
        public static Pen GridPen => CreatePen(GridBrush, GridWidth);

        private static Pen CreatePen(Brush brush, double width)
        {
            var p = new Pen(brush, width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            if (p.CanFreeze) p.Freeze();
            return p;
        }
    }
}
