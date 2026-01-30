using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CNC_Improvements_gcode_solids.Pages.TransMatrixCard
{
    public partial class MatrixCard : UserControl
    {
        // allow: digits, one leading '-', one '.', culture-invariant style
        private static readonly Regex _numericRegex = new Regex(@"^[0-9\.\-]+$");

        public MatrixCard()
        {
            InitializeComponent();
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text))
            {
                e.Handled = true;
                return;
            }

            e.Handled = !_numericRegex.IsMatch(e.Text);
        }

        private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.Text, true))
            {
                e.CancelCommand();
                return;
            }

            string text = e.SourceDataObject.GetData(DataFormats.Text) as string ?? "";
            text = text.Trim();

            if (text.Length == 0 || !_numericRegex.IsMatch(text))
            {
                e.CancelCommand();
                return;
            }
        }
    }
}
