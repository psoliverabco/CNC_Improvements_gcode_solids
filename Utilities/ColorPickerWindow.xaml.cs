using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Utilities
{
    public partial class ColorPickerWindow : Window
    {
        private bool _updating;

        public string SelectedHex { get; private set; } = "#FF000000";

        public ColorPickerWindow(string initialHex)
        {
            InitializeComponent();

            // default
            SetFromHex(string.IsNullOrWhiteSpace(initialHex) ? "#FF000000" : initialHex.Trim());
        }

        private void SetFromHex(string hex)
        {
            try
            {
                var brush = UiUtilities.HexBrush(hex);
                if (brush is not SolidColorBrush scb)
                    throw new Exception();

                var c = scb.Color;

                _updating = true;

                SlA.Value = c.A;
                SlR.Value = c.R;
                SlG.Value = c.G;
                SlB.Value = c.B;

                TxtHex.Text = ToHex(c);
                UpdatePreview(c);

                _updating = false;

                SelectedHex = TxtHex.Text.Trim();
            }
            catch
            {
                // ignore bad input, keep current
            }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating) return;

            byte a = (byte)Math.Round(SlA.Value);
            byte r = (byte)Math.Round(SlR.Value);
            byte g = (byte)Math.Round(SlG.Value);
            byte b = (byte)Math.Round(SlB.Value);

            TxtA.Text = a.ToString(CultureInfo.InvariantCulture);
            TxtR.Text = r.ToString(CultureInfo.InvariantCulture);
            TxtG.Text = g.ToString(CultureInfo.InvariantCulture);
            TxtB.Text = b.ToString(CultureInfo.InvariantCulture);

            var c = Color.FromArgb(a, r, g, b);

            _updating = true;
            TxtHex.Text = ToHex(c);
            _updating = false;

            UpdatePreview(c);
            SelectedHex = TxtHex.Text.Trim();
        }

        private void TxtHex_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_updating) return;

            string hex = TxtHex.Text?.Trim() ?? "";
            if (hex.Length == 0) return;

            // Only react when it looks complete
            // Accept #RGB/#ARGB/#RRGGBB/#AARRGGBB via UiUtilities
            try
            {
                var brush = UiUtilities.HexBrush(hex);
                if (brush is not SolidColorBrush scb)
                    return;

                var c = scb.Color;

                _updating = true;

                SlA.Value = c.A;
                SlR.Value = c.R;
                SlG.Value = c.G;
                SlB.Value = c.B;

                UpdatePreview(c);

                _updating = false;

                SelectedHex = ToHex(c); // force canonical output
            }
            catch
            {
                // ignore while typing
            }
        }

        private void UpdatePreview(Color c)
        {
            RectPreview.Fill = new SolidColorBrush(c);
        }

        private static string ToHex(Color c)
        {
            return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            // Force canonical output if possible
            try
            {
                var b = UiUtilities.HexBrush(TxtHex.Text.Trim());
                if (b is SolidColorBrush scb)
                    SelectedHex = ToHex(scb.Color);
            }
            catch { /* handled by SettingsPage validation */ }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
