using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Pages
{
    class UniqueTagColor
    {

        // Unique tag styling: (u:xxxx) — light blue @ ~50% opacity
        public static readonly Brush UniqueTagBrush = CreateUniqueTagBrush();

        private static Brush CreateUniqueTagBrush()
        {
            var b = new SolidColorBrush(Color.FromArgb(255, 255, 61, 245)); // LightSkyBlue-ish 50%
            b.Freeze();
            return b;
        }




    }
}
