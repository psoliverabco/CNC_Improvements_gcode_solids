using CNC_Improvements_gcode_solids.Properties;
using System.Windows;




namespace CNC_Improvements_gcode_solids.Utilities
{
    public partial class LogWindow : Window
    {
        public LogWindow(string title, string text)
        {
            InitializeComponent();
            this.Title = title;
            TxtLog.Text = text ?? string.Empty;
        }



        // Add these inside the LogWindow class
        public new void Show()
        {
            if (!Settings.Default.LogWindowShow)
                return;

            base.Show();
        }

        public new bool? ShowDialog()
        {
            if (!Settings.Default.LogWindowShow)
                return false;

            return base.ShowDialog();
        }









    }
}
