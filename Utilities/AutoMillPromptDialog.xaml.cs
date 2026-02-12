// File: Utilities/AutoMillPromptDialog.xaml.cs
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// Single prompt dialog used for:
    ///  - Auto MILL  (base name + tool dia)
    ///  - Auto DRILL (base name + tool dia + R clearance)
    ///  - Auto TURN  (base name only)
    /// </summary>
    public partial class AutoMillPromptDialog : Window
    {
        private enum PromptMode
        {
            Mill,
            Turn,
            Drill
        }

        private PromptMode _mode = PromptMode.Mill;

        private string _baseName = "";
        private double _toolDia = 10.0;
        private double _rClear = 3.0;

        public string BaseName => _baseName;
        public double ToolDia => _toolDia;
        public double RClear => _rClear;

        public AutoMillPromptDialog()
        {
            InitializeComponent();
            // IMPORTANT (DO NOT REMOVE):
            // We do NOT auto-hook OK/Cancel click handlers here.
            //
            // Reason:
            // - The XAML already wires Click="BtnOk_Click" / Click="BtnCancel_Click".
            // - Auto-hooking adds a SECOND handler, which reintroduces the repeat offender:
            //     DialogResult can be set only after Window is created and shown as dialog.
            //   (and can also double-run OK/Cancel logic).
            //
            // Rule: Only close this window via SafeCloseDialog(true/false).
        }


        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            OnOk();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            SafeCloseDialog(false);
        }


        // ============================================================
        // PUBLIC STATIC ENTRY POINTS
        // ============================================================

        // MILL: base name + tool dia
        public static bool Show(Window owner, out string baseName, out double toolDia)
        {
            bool ok = ShowCore(
                owner: owner,
                mode: PromptMode.Mill,
                title: "Auto Mill Reg",
                defaultBaseName: "MILL",
                defaultToolDia: 10.0,
                defaultRClear: 3.0,
                out baseName,
                out toolDia,
                out _ // r ignored
            );
            return ok;
        }

        // TURN: base name only
        public static bool ShowTurn(Window owner, out string baseName)
        {
            bool ok = ShowCore(
                owner: owner,
                mode: PromptMode.Turn,
                title: "Auto Turn Reg",
                defaultBaseName: "TURN",
                defaultToolDia: 10.0,
                defaultRClear: 3.0,
                out baseName,
                out _,
                out _
            );
            return ok;
        }

        // DRILL: base name + tool dia + R clearance (defaults: DRILL, 10, 3)
        public static bool ShowDrill(Window owner, out string baseName, out double toolDia, out double rClear)
        {
            return ShowCore(
                owner: owner,
                mode: PromptMode.Drill,
                title: "Auto Drill Reg",
                defaultBaseName: "DRILL",
                defaultToolDia: 10.0,
                defaultRClear: 3.0,
                out baseName,
                out toolDia,
                out rClear
            );
        }

        // ============================================================
        // INTERNAL CORE
        // ============================================================

        private static bool ShowCore(
            Window owner,
            PromptMode mode,
            string title,
            string defaultBaseName,
            double defaultToolDia,
            double defaultRClear,
            out string baseName,
            out double toolDia,
            out double rClear)
        {
            baseName = "";
            toolDia = 0;
            rClear = 0;

            var dlg = new AutoMillPromptDialog
            {
                Title = title,
                _mode = mode
            };

            if (owner != null)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            dlg.ApplyModeUi(mode);
            dlg.SetDefaults(defaultBaseName, defaultToolDia, defaultRClear);

            bool? res = dlg.ShowDialog();
            if (res != true)
                return false;

            baseName = dlg._baseName;
            toolDia = dlg._toolDia;
            rClear = dlg._rClear;
            return true;
        }

        // ============================================================
        // UI MODE
        // ============================================================

        private void ApplyModeUi(PromptMode mode)
        {
            // Controls exist because we ship the XAML; keep it simple and explicit.
            var lblTool = FindName("LblToolDia") as TextBlock;
            var txtTool = FindName("TxtToolDia") as TextBox;

            var lblR = FindName("LblRClear") as TextBlock;
            var txtR = FindName("TxtRClear") as TextBox;

            if (mode == PromptMode.Turn)
            {
                if (lblTool != null) lblTool.Visibility = Visibility.Collapsed;
                if (txtTool != null) txtTool.Visibility = Visibility.Collapsed;

                if (lblR != null) lblR.Visibility = Visibility.Collapsed;
                if (txtR != null) txtR.Visibility = Visibility.Collapsed;

                Height = 200;
            }
            else if (mode == PromptMode.Mill)
            {
                if (lblTool != null) lblTool.Visibility = Visibility.Visible;
                if (txtTool != null) txtTool.Visibility = Visibility.Visible;

                if (lblR != null) lblR.Visibility = Visibility.Collapsed;
                if (txtR != null) txtR.Visibility = Visibility.Collapsed;

                Height = 220;
            }
            else // Drill
            {
                if (lblTool != null) lblTool.Visibility = Visibility.Visible;
                if (txtTool != null) txtTool.Visibility = Visibility.Visible;

                if (lblR != null) lblR.Visibility = Visibility.Visible;
                if (txtR != null) txtR.Visibility = Visibility.Visible;

                Height = 250;
            }
        }

        private void SetDefaults(string defaultBaseName, double defaultToolDia, double defaultRClear)
        {
            var nameBox = GetNameBox();
            if (nameBox != null)
            {
                nameBox.Text = defaultBaseName ?? "";
                nameBox.Focus();
                nameBox.SelectAll();
            }

            var diaBox = GetDiaBox();
            if (diaBox != null)
            {
                diaBox.Text = defaultToolDia.ToString("0.###", CultureInfo.InvariantCulture);
                diaBox.IsEnabled = (_mode != PromptMode.Turn);
                diaBox.Opacity = diaBox.IsEnabled ? 1.0 : 0.4;
            }

            var rBox = GetRBox();
            if (rBox != null)
            {
                rBox.Text = defaultRClear.ToString("0.###", CultureInfo.InvariantCulture);
                rBox.IsEnabled = (_mode == PromptMode.Drill);
                rBox.Opacity = rBox.IsEnabled ? 1.0 : 0.4;
            }

            _baseName = (defaultBaseName ?? "").Trim();
            _toolDia = defaultToolDia;
            _rClear = defaultRClear;
        }

        // ============================================================
        // OK / CANCEL
        // ============================================================

        private void HookOkCancelIfPresent()
        {
            // IMPORTANT: Intentionally disabled.
            // Do not reintroduce button auto-hooking in this dialog.
            // XAML already wires BtnOk_Click / BtnCancel_Click.
            // Close must go through SafeCloseDialog only.
        }




        private void OnOk()
        {
            string nm = (TxtBaseName?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nm))
            {
                MessageBox.Show("Base name is required.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double dia = 10.0;
            if (_mode != PromptMode.Turn)
            {
                string diaText = (TxtToolDia?.Text ?? "").Trim();
                if (!TryParseNumber(diaText, out dia) || dia <= 0)
                {
                    MessageBox.Show("Tool dia must be a number > 0.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            double r = 3.0;
            if (_mode == PromptMode.Drill)
            {
                string rText = (FindName("TxtRClear") as TextBox)?.Text ?? "";
                rText = (rText ?? "").Trim();

                if (!TryParseNumber(rText, out r))
                {
                    MessageBox.Show("R clearance must be a number.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _baseName = nm;
            _toolDia = dia;
            _rClear = r;

            SafeCloseDialog(true);
        }


        private void SafeCloseDialog(bool ok)
        {
            // ALWAYS safe:
            // - If shown via ShowDialog, DialogResult works.
            // - If shown via Show (or anything else), DialogResult throws → we catch and Close().
            try
            {
                DialogResult = ok;
            }
            catch (InvalidOperationException)
            {
                // Not a dialog window → just close
            }
            Close();
        }


        // ============================================================
        // UI helpers
        // ============================================================

        private TextBox GetNameBox()
        {
            return
                FindName("TbName") as TextBox ??
                FindName("TxtName") as TextBox ??
                FindName("TxtBaseName") as TextBox ??
                FindName("NameBox") as TextBox;
        }

        private TextBox GetDiaBox()
        {
            return
                FindName("TbDia") as TextBox ??
                FindName("TxtDia") as TextBox ??
                FindName("TxtToolDia") as TextBox ??
                FindName("ToolDiaBox") as TextBox;
        }

        private TextBox GetRBox()
        {
            return
                FindName("TxtRClear") as TextBox ??
                FindName("TxtR") as TextBox ??
                FindName("TbR") as TextBox ??
                FindName("RClearBox") as TextBox;
        }

        private static bool TryParseNumber(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s))
                return false;

            s = s.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
