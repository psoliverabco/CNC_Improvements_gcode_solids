using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.SetManagement
{
    /// <summary>
    /// Captures/restores common WPF control values by control Name.
    /// This lets us "store parameters" for Turn/Mill/Drill pages
    /// without modifying their internal logic.
    /// </summary>
    public sealed class UiStateSnapshot
    {
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public static UiStateSnapshot Capture(DependencyObject root)
        {
            var snap = new UiStateSnapshot();
            if (root == null) return snap;

            foreach (FrameworkElement fe in EnumerateNamedElements(root))
            {
                if (string.IsNullOrWhiteSpace(fe.Name))
                    continue;

                string key = fe.Name.Trim();

                // TextBox
                if (fe is TextBox tb)
                {
                    snap.Values[key] = tb.Text ?? "";
                    continue;
                }

                // ComboBox
                if (fe is ComboBox cb)
                {
                    snap.Values[key] = cb.SelectedIndex.ToString(CultureInfo.InvariantCulture);
                    continue;
                }

                // CheckBox / ToggleButton (RadioButton derives from ToggleButton too)
                if (fe is System.Windows.Controls.Primitives.ToggleButton tog)
                {
                    snap.Values[key] = (tog.IsChecked == true) ? "1" : "0";
                    continue;
                }

                // Slider
                if (fe is Slider sl)
                {
                    snap.Values[key] = sl.Value.ToString("R", CultureInfo.InvariantCulture);
                    continue;
                }
            }

            return snap;
        }

        public void ApplyTo(DependencyObject root)
        {
            if (root == null) return;

            foreach (FrameworkElement fe in EnumerateNamedElements(root))
            {
                if (string.IsNullOrWhiteSpace(fe.Name))
                    continue;

                string key = fe.Name.Trim();
                if (!Values.TryGetValue(key, out string val))
                    continue;

                // TextBox
                if (fe is TextBox tb)
                {
                    tb.Text = val ?? "";
                    continue;
                }

                // ComboBox
                if (fe is ComboBox cb)
                {
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                        cb.SelectedIndex = idx;
                    continue;
                }

                // ToggleButton
                if (fe is System.Windows.Controls.Primitives.ToggleButton tog)
                {
                    tog.IsChecked = (val == "1");
                    continue;
                }

                // Slider
                if (fe is Slider sl)
                {
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                        sl.Value = dv;
                    continue;
                }
            }
        }

        private static IEnumerable<FrameworkElement> EnumerateNamedElements(DependencyObject root)
        {
            if (root == null) yield break;

            // BFS to avoid deep recursion surprises
            var q = new Queue<DependencyObject>();
            q.Enqueue(root);

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (cur is FrameworkElement fe)
                    yield return fe;

                int count = VisualTreeHelper.GetChildrenCount(cur);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(cur, i);
                    if (child != null)
                        q.Enqueue(child);
                }
            }
        }
    }
}
