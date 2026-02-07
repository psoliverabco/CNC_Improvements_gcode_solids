using Microsoft.Win32;
using CNC_Improvements_gcode_solids.Utilities;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CNC_Improvements_gcode_solids.Pages
{
    public partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            LoadFromSettings();
        }

        private void LoadFromSettings()
        {
            // Paths
            TxtFreeCadPath.Text = Properties.Settings.Default.FreeCadPath ?? string.Empty;

            // Colors
            TxtProfileColor.Text = Properties.Settings.Default.ProfileColor ?? "#FF00FF00";
            TxtOffsetColor.Text = Properties.Settings.Default.OffsetColor ?? "#FFFFA500";
            TxtClosingColor.Text = Properties.Settings.Default.ClosingColor ?? "#FF808080";
            TxtGraphicTextColor.Text = Properties.Settings.Default.GraphicTextColor ?? "#FFFBFF3D";

            // NEW: CL (centreline) color
            TxtCLColor.Text = Properties.Settings.Default.CLColor ?? "#FFFF00FF";

            // Widths
            TxtProfileWidth.Text = Properties.Settings.Default.ProfileWidth.ToString("0.#####", CultureInfo.InvariantCulture);
            TxtOffsetWidth.Text = Properties.Settings.Default.OffsetWidth.ToString("0.####", CultureInfo.InvariantCulture);
            TxtClosingWidth.Text = Properties.Settings.Default.ClosingWidth.ToString("0.#####", CultureInfo.InvariantCulture);

            // NEW: CL width
            TxtCLWidth.Text = Properties.Settings.Default.CLWidth.ToString("0.#####", CultureInfo.InvariantCulture);

            // Floats
            TxtTangentAngTol.Text = Properties.Settings.Default.TangentAngTol.ToString("0.#####", CultureInfo.InvariantCulture);
            TxtSmallSegment.Text = Properties.Settings.Default.SmallSegment.ToString("0.#####", CultureInfo.InvariantCulture);

            // NEW: Tolerances / thresholds
            TxtClipperPolyTol.Text = Properties.Settings.Default.ClipperInputPolyTol.ToString("0.#####", CultureInfo.InvariantCulture);
            TxtMinArcPoints.Text = Properties.Settings.Default.MinArcPoints.ToString(CultureInfo.InvariantCulture);
            TxtSnapTol.Text = Properties.Settings.Default.SnapRad.ToString("0.#####", CultureInfo.InvariantCulture);

            // NEW: Sew tolerance (FreeCAD sewing / closure tol)
            TxtSewTol.Text = Properties.Settings.Default.SewTol.ToString("0.#####", CultureInfo.InvariantCulture);

            // Bool
            ChkLogWindowShow.IsChecked = Properties.Settings.Default.LogWindowShow;

            UpdateColorPreviews();
        }



        private void BtnBrowseFreeCad_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select FreeCAD executable",
                Filter = "Executable Files|FreeCAD.exe;FreeCADCMD.exe;*.exe|All Files|*.*",
                FileName = "FreeCADCMD.exe"
            };

            if (dlg.ShowDialog() == true)
                TxtFreeCadPath.Text = dlg.FileName;
        }

        private void BtnPickColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn)
                return;

            string targetName = btn.Tag as string ?? "";
            var tb = FindName(targetName) as TextBox;
            if (tb == null)
                return;

            string initial = tb.Text?.Trim();
            if (string.IsNullOrWhiteSpace(initial))
                initial = "#FF000000";

            var picker = new ColorPickerWindow(initial);
            picker.Owner = Window.GetWindow(this);

            bool? ok = picker.ShowDialog();
            if (ok == true)
            {
                tb.Text = picker.SelectedHex;   // #AARRGGBB
                UpdateColorPreviews();
            }
        }

        private void ColorText_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateColorPreviews();
        }

        private void UpdateColorPreviews()
        {
            RectProfileColor.Fill = SafeBrush(TxtProfileColor.Text);
            RectOffsetColor.Fill = SafeBrush(TxtOffsetColor.Text);
            RectClosingColor.Fill = SafeBrush(TxtClosingColor.Text);
            RectGraphicTextColor.Fill = SafeBrush(TxtGraphicTextColor.Text);

            // NEW
            RectCLColor.Fill = SafeBrush(TxtCLColor.Text);
        }


        private static System.Windows.Media.Brush SafeBrush(string hex)
        {
            try
            {
                return UiUtilities.HexBrush(hex.Trim());
            }
            catch
            {
                // show invalid as dark red so it’s obvious but non-fatal
                return UiUtilities.HexBrush("#FF550000");
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate colors (must parse)
                ValidateColor(TxtProfileColor.Text, "ProfileColor");
                ValidateColor(TxtOffsetColor.Text, "OffsetColor");
                ValidateColor(TxtClosingColor.Text, "ClosingColor");
                ValidateColor(TxtGraphicTextColor.Text, "GraphicTextColor");

                // NEW: CL color
                ValidateColor(TxtCLColor.Text, "CLColor");

                // Validate doubles (widths)
                double profileWidth = ParseDoubleInv(TxtProfileWidth.Text, "ProfileWidth");
                double offsetWidth = ParseDoubleInv(TxtOffsetWidth.Text, "OffsetWidth");
                double closingWidth = ParseDoubleInv(TxtClosingWidth.Text, "ClosingWidth");

                // NEW: CL width
                double clWidth = ParseDoubleInv(TxtCLWidth.Text, "CLWidth");

                if (profileWidth <= 0) throw new Exception("ProfileWidth must be > 0.");
                if (offsetWidth <= 0) throw new Exception("OffsetWidth must be > 0.");
                if (closingWidth <= 0) throw new Exception("ClosingWidth must be > 0.");
                if (clWidth <= 0) throw new Exception("CLWidth must be > 0.");

                // Validate floats
                float tangentAngTol = ParseFloatInv(TxtTangentAngTol.Text, "TangentAngTol");
                float smallSegment = ParseFloatInv(TxtSmallSegment.Text, "SmallSegment");

                // Validate new fields
                double clipperPolyTol = ParseDoubleInv(TxtClipperPolyTol.Text, "ClipperInputPolyTol");
                int minArcPoints = ParseIntInv(TxtMinArcPoints.Text, "MinArcPoints");
                double snapRad = ParseDoubleInv(TxtSnapTol.Text, "SnapRad");

                // NEW: Sew tolerance
                double sewTol = ParseDoubleInv(TxtSewTol.Text, "SewTol");

                if (clipperPolyTol <= 0) throw new Exception("ClipperInputPolyTol must be > 0.");
                if (minArcPoints < 3) throw new Exception("MinArcPoints must be >= 3.");
                if (snapRad <= 0) throw new Exception("SnapRad must be > 0.");
                if (sewTol <= 0) throw new Exception("SewTol must be > 0.");

                // Save values
                Properties.Settings.Default.FreeCadPath = TxtFreeCadPath.Text ?? string.Empty;

                Properties.Settings.Default.ProfileColor = TxtProfileColor.Text.Trim();
                Properties.Settings.Default.OffsetColor = TxtOffsetColor.Text.Trim();
                Properties.Settings.Default.ClosingColor = TxtClosingColor.Text.Trim();
                Properties.Settings.Default.GraphicTextColor = TxtGraphicTextColor.Text.Trim();

                // NEW
                Properties.Settings.Default.CLColor = TxtCLColor.Text.Trim();

                Properties.Settings.Default.ProfileWidth = profileWidth;
                Properties.Settings.Default.OffsetWidth = offsetWidth;
                Properties.Settings.Default.ClosingWidth = closingWidth;

                // NEW
                Properties.Settings.Default.CLWidth = clWidth;

                Properties.Settings.Default.TangentAngTol = tangentAngTol;
                Properties.Settings.Default.SmallSegment = smallSegment;

                Properties.Settings.Default.ClipperInputPolyTol = clipperPolyTol;
                Properties.Settings.Default.MinArcPoints = minArcPoints;
                Properties.Settings.Default.SnapRad = snapRad;

                // NEW
                Properties.Settings.Default.SewTol = sewTol;

                Properties.Settings.Default.LogWindowShow = (ChkLogWindowShow.IsChecked == true);

                Properties.Settings.Default.Save();

                MessageBox.Show("Settings saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }



        private static void ValidateColor(string s, string fieldName)
        {
            try
            {
                _ = UiUtilities.HexBrush(s.Trim());
            }
            catch
            {
                throw new Exception($"{fieldName} is not a valid colour. Use #AARRGGBB (example: #FF123456).");
            }
        }

        private static double ParseDoubleInv(string s, string fieldName)
        {
            if (!double.TryParse(s?.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double v))
            {
                throw new Exception($"{fieldName} must be a number (Invariant).");
            }
            return v;
        }

        private static float ParseFloatInv(string s, string fieldName)
        {
            if (!float.TryParse(s?.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float v))
            {
                throw new Exception($"{fieldName} must be a number (Invariant).");
            }
            return v;
        }

        private static int ParseIntInv(string s, string fieldName)
        {
            if (!int.TryParse(s?.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int v))
            {
                throw new Exception($"{fieldName} must be an integer (Invariant).");
            }
            return v;
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Reset ALL settings to their default values?",
                "Reset Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
                return;

            Properties.Settings.Default.Reset();
            Properties.Settings.Default.Save();
            LoadFromSettings();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (this.NavigationService != null && this.NavigationService.CanGoBack)
                this.NavigationService.GoBack();
            else
                Window.GetWindow(this)?.Close();
        }
    }
}
