// File: Pages/TransMatrixCard/MatrixCard.xaml.cs
// Position: REPLACE THE ENTIRE FILE CONTENT WITH THIS
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CNC_Improvements_gcode_solids.Pages.TransMatrixCard
{
    public partial class MatrixCard : UserControl
    {
        // Typing-friendly: allows intermediate states ("", "-", ".", "-.", "12", "12.", "12.3")
        private static readonly Regex _numericTypingRegex = new Regex(@"^-?\d*\.?\d*$");

        // Paste-friendly: must be a real number (allows ".5", "-.5", "0.5", "-12", "-12.3")
        private static readonly Regex _numericPasteRegex = new Regex(@"^-?(\d+(\.\d*)?|\.\d+)$");

        public MatrixCard()
        {
            InitializeComponent();
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox tb)
            {
                e.Handled = true;
                return;
            }

            string incoming = e.Text ?? "";
            if (incoming.Length == 0)
            {
                e.Handled = true;
                return;
            }

            int start = tb.SelectionStart;
            int len = tb.SelectionLength;

            string current = tb.Text ?? "";
            string proposed = current.Remove(start, len).Insert(start, incoming);

            e.Handled = !_numericTypingRegex.IsMatch(proposed);
        }

        private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (sender is not TextBox tb)
            {
                e.CancelCommand();
                return;
            }

            if (!e.SourceDataObject.GetDataPresent(DataFormats.Text, true))
            {
                e.CancelCommand();
                return;
            }

            string paste = (e.SourceDataObject.GetData(DataFormats.Text) as string ?? "").Trim();
            if (paste.Length == 0 || !_numericPasteRegex.IsMatch(paste))
            {
                e.CancelCommand();
                return;
            }

            int start = tb.SelectionStart;
            int len = tb.SelectionLength;

            string current = tb.Text ?? "";
            string proposed = current.Remove(start, len).Insert(start, paste);

            if (!_numericTypingRegex.IsMatch(proposed))
            {
                e.CancelCommand();
                return;
            }
        }
    }
}
