namespace CNC_Improvements_gcode_solids.Pages
{
    /// <summary>
    /// Pages that share the MainWindow G-code editor should implement this.
    /// - OnGcodeModelLoaded(): called ONLY when a NEW file is loaded (reset page state).
    /// - OnPageActivated(): called when switching pages / returning from Settings (do NOT reset state).
    /// </summary>
    public interface IGcodePage
    {
        void OnGcodeModelLoaded();
        void OnPageActivated();
    }
}
