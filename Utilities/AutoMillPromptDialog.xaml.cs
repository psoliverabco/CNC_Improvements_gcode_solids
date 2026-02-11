// File: Utilities/AutoMillPromptDialog.xaml.cs
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// Single prompt dialog used for:
    ///  - Auto MILL (base name + tool dia)
    ///  - Auto DRILL (base name + tool dia)
    ///  - Auto TURN  (base name only)
    ///
    /// IMPORTANT:
    /// - This code-behind does NOT depend on specific XAML control names at compile-time.
    /// - It uses FindName() to locate TextBoxes/Buttons by common names.
    /// - So it compiles even if your XAML names differ.
    /// </summary>
    public partial class AutoMillPromptDialog : Window
    {
        private string _baseName = "";
        private double _toolDia = 10.0;

        public string BaseName => _baseName;
        public double ToolDia => _toolDia;

        public AutoMillPromptDialog()
        {
            InitializeComponent();

            // If XAML already wires button handlers, this is harmless.
            // If not, we attach to common OK/Cancel names.
            HookOkCancelIfPresent();
        }

        // ============================================================
        // PUBLIC STATIC ENTRY POINTS (these are what MainWindow calls)
        // ============================================================

        // Existing MILL usage (base name + tool dia)
        public static bool Show(Window owner, out string baseName, out double toolDia)
        {
            return ShowCore(
                owner: owner,
                title: "Auto Mill Reg",
                headerText: "Create Auto MILL regions from highlighted text",
                defaultBaseName: "MILL_AUTO",
                defaultToolDia: 10.0,
                allowToolDiaEdit: true,
                out baseName,
                out toolDia
            );
        }

        // TURN usage (base name only)
        public static bool ShowTurn(Window owner, out string baseName)
        {
            bool ok = ShowCore(
                owner: owner,
                title: "Auto Turn Reg",
                headerText: "Create Auto TURN regions from highlighted text",
                defaultBaseName: "TURN",
                defaultToolDia: 10.0,
                allowToolDiaEdit: false,
                out baseName,
                out _ // ignored
            );
            return ok;
        }

        // DRILL usage (base name + tool dia) with required defaults: name=DRILL, dia=10
        public static bool ShowDrill(Window owner, out string baseName, out double toolDia)
        {
            return ShowCore(
                owner: owner,
                title: "Auto Drill Reg",
                headerText: "Create Auto DRILL regions from highlighted text",
                defaultBaseName: "DRILL",
                defaultToolDia: 10.0,
                allowToolDiaEdit: true,
                out baseName,
                out toolDia
            );
        }

        // ============================================================
        // INTERNAL CORE
        // ============================================================

        private static bool ShowCore(
            Window owner,
            string title,
            string headerText,
            string defaultBaseName,
            double defaultToolDia,
            bool allowToolDiaEdit,
            out string baseName,
            out double toolDia)
        {
            baseName = "";
            toolDia = 0;

            var dlg = new AutoMillPromptDialog
            {
                Title = title
            };

            if (owner != null)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            dlg.SetHeaderText(headerText);
            dlg.SetDefaults(defaultBaseName, defaultToolDia, allowToolDiaEdit);

            bool? res = dlg.ShowDialog();
            if (res != true)
                return false;

            // Read results (dialog validated before setting DialogResult=true)
            baseName = dlg._baseName;
            toolDia = dlg._toolDia;
            return true;
        }

        // ============================================================
        // OK / CANCEL (works even if XAML has no handlers)
        // ============================================================

        private void HookOkCancelIfPresent()
        {
            var okBtn =
                FindName("BtnOk") as Button ??
                FindName("OK") as Button ??
                FindName("ButtonOk") as Button;

            var cancelBtn =
                FindName("BtnCancel") as Button ??
                FindName("Cancel") as Button ??
                FindName("ButtonCancel") as Button;

            if (okBtn != null)
                okBtn.Click += (_, __) => OnOk();

            if (cancelBtn != null)
                cancelBtn.Click += (_, __) => DialogResult = false;
        }

        private void OnOk()
        {
            string nm = (GetNameBox()?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nm))
            {
                MessageBox.Show("Base name is required.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // TURN mode disables the dia box; in that case we accept a safe default.
            double dia = 10.0;

            var diaBox = GetDiaBox();
            bool diaEditable = (diaBox != null && diaBox.IsEnabled);

            if (diaEditable)
            {
                string diaText = (diaBox.Text ?? "").Trim();
                if (!TryParseToolDia(diaText, out dia) || dia <= 0)
                {
                    MessageBox.Show("Tool dia must be a number > 0.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _baseName = nm;
            _toolDia = dia;

            // DialogResult only legal when shown with ShowDialog()
            if (Owner != null && IsActive)
            {
                try
                {
                    DialogResult = true;
                    return;
                }
                catch
                {
                    // fall through to Close()
                }
            }

            Close();
        }



        // XAML wires these by name (AutoMillPromptDialog.xaml line 58/59).
        // Keep them as thin wrappers.
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            OnOk();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            // DialogResult only legal when shown with ShowDialog()
            if (Owner != null && IsActive)
            {
                try
                {
                    DialogResult = false;
                    return;
                }
                catch
                {
                    // fall through to Close()
                }
            }

            Close();
        }




        // ============================================================
        // UI helpers (NO hard dependency on XAML names)
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

        private TextBlock GetHeaderBlock()
        {
            return
                FindName("TxtHeader") as TextBlock ??
                FindName("HeaderText") as TextBlock ??
                FindName("LblHeader") as TextBlock;
        }

        private void SetHeaderText(string headerText)
        {
            var tb = GetHeaderBlock();
            if (tb != null)
                tb.Text = headerText ?? "";
        }

        private void SetDefaults(string defaultBaseName, double defaultToolDia, bool allowToolDiaEdit)
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
                diaBox.IsEnabled = allowToolDiaEdit;
                diaBox.Opacity = allowToolDiaEdit ? 1.0 : 0.4;
            }

            _baseName = (defaultBaseName ?? "").Trim();
            _toolDia = defaultToolDia;
        }

        private static bool TryParseToolDia(string s, out double dia)
        {
            dia = 0;
            if (string.IsNullOrWhiteSpace(s))
                return false;

            // accept comma decimals
            s = s.Replace(',', '.');

            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out dia);
        }
    }
}
