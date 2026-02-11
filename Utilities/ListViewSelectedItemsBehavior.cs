// File: Utilities/ListViewSelectedItemsBehavior.cs
// Position: NEW FILE
using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace CNC_Improvements_gcode_solids.Utilities
{
    public static class ListViewSelectedItemsBehavior
    {
        public static readonly DependencyProperty SelectedItemsProperty =
            DependencyProperty.RegisterAttached(
                "SelectedItems",
                typeof(IList),
                typeof(ListViewSelectedItemsBehavior),
                new PropertyMetadata(null, OnSelectedItemsChanged));

        public static void SetSelectedItems(DependencyObject element, IList value)
            => element.SetValue(SelectedItemsProperty, value);

        public static IList GetSelectedItems(DependencyObject element)
            => (IList)element.GetValue(SelectedItemsProperty);

        private static readonly DependencyProperty IsHookedProperty =
            DependencyProperty.RegisterAttached(
                "IsHooked",
                typeof(bool),
                typeof(ListViewSelectedItemsBehavior),
                new PropertyMetadata(false));

        private static void OnSelectedItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ListView lv)
                return;

            bool hooked = (bool)lv.GetValue(IsHookedProperty);
            if (!hooked)
            {
                lv.SelectionChanged += Lv_SelectionChanged;
                lv.SetValue(IsHookedProperty, true);
            }

            SyncFromListView(lv);
        }

        private static void Lv_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListView lv)
                return;

            SyncFromListView(lv);
        }

        private static void SyncFromListView(ListView lv)
        {
            var bound = GetSelectedItems(lv);
            if (bound == null)
                return;

            bound.Clear();
            foreach (var item in lv.SelectedItems)
                bound.Add(item);
        }
    }
}
