using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Pages
{
    class UniqueTagColor
    {

        // Unique tag styling: (u:xxxx) — light blue @ ~50% opacity
        public static readonly Brush UniqueTagBrush = CreateUniqueTagBrush();

        private static Brush CreateUniqueTagBrush()
        {
            var b = new SolidColorBrush(Color.FromArgb(128, 135, 206, 250)); // LightSkyBlue @ ~50%
            b.Freeze();
            return b;
        }




    }
}
