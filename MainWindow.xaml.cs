using Microsoft.Win32;
using CNC_Improvements_gcode_solids.Pages;
using CNC_Improvements_gcode_solids.SetManagement;
using CNC_Improvements_gcode_solids.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;



namespace CNC_Improvements_gcode_solids
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {

//did this get in.

        // Tracks STEP files created during Export All (authoritative list, no scanning)
        public List<string> ExportAllCreatedStepFiles = new List<string>();

        public RichTextBox GcodeEditor => TxtGcode;
        public List<string> GcodeLines { get; } = new List<string>();
        public string CurrentGcodeFilePath { get; private set; } = "";
        public string CurrentProjectDirectory { get; private set; } = "";

        // Project name = file name (no extension) of the last Save/Load .npcproj.
        // Empty means "no project has been saved/loaded yet".
        public string projectName { get; private set; } = "";


        public bool AllowScrollToRegionStart = false;



        private void SetProjectNameFromProjectFilePath(string projectFilePath)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath))
            {
                projectName = "";
                return;
            }

            projectName = System.IO.Path.GetFileNameWithoutExtension(projectFilePath) ?? "";
        }


        // Persistent page instances
        private readonly TurningPage _turningPage;
        private readonly DrillPage _drillPage;
        private readonly MillPage _millingPage;
        private readonly TransMatrix _transMatrixPage;


        // ============================================================
        // Universal Sets (Turn/Mill/Drill)
        // ============================================================
        public ObservableCollection<RegionSet> TurnSets { get; } = new ObservableCollection<RegionSet>();
        public ObservableCollection<RegionSet> MillSets { get; } = new ObservableCollection<RegionSet>();
        public ObservableCollection<RegionSet> DrillSets { get; } = new ObservableCollection<RegionSet>();

        private RegionSet? _selectedTurnSet;
        private RegionSet? _selectedMillSet;
        private RegionSet? _selectedDrillSet;

        public RegionSetKind _activeKind = RegionSetKind.Turn;

        // prevents navigation + ApplyTurnSet storms while loading a project file
        private bool _isLoadingProject = false;
        // Suppress one automatic OnPageActivated call (used after Load Project)
        private bool _suppressNextPageActivated = false;

        public RegionSet? SelectedTurnSet
        {
            get => _selectedTurnSet;
            set
            {
                if (ReferenceEquals(_selectedTurnSet, value)) return;

                UnhookSelectedSet(_selectedTurnSet);
                _selectedTurnSet = value;
                HookSelectedSet(_selectedTurnSet);

                _activeKind = RegionSetKind.Turn;

                if (!_isLoadingProject)
                {
                    NavigateToTurn();
                    _turningPage.ApplyTurnSet(_selectedTurnSet);

                }
                AllowScrollToRegionStart = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedSetLabel));
            }
        }

        public RegionSet? SelectedMillSet
        {
            get => _selectedMillSet;
            set
            {
                if (ReferenceEquals(_selectedMillSet, value)) return;

                UnhookSelectedSet(_selectedMillSet);
                _selectedMillSet = value;
                HookSelectedSet(_selectedMillSet);

                _activeKind = RegionSetKind.Mill;

                if (!_isLoadingProject)
                {
                    NavigateToMill();
                    _millingPage.ApplyMillSet(_selectedMillSet);

                }
                AllowScrollToRegionStart = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedSetLabel));
            }
        }


        public RegionSet? SelectedDrillSet
        {
            get => _selectedDrillSet;
            set
            {
                if (ReferenceEquals(_selectedDrillSet, value)) return;

                UnhookSelectedSet(_selectedDrillSet);
                _selectedDrillSet = value;
                HookSelectedSet(_selectedDrillSet);

                _activeKind = RegionSetKind.Drill;

                if (!_isLoadingProject)
                {
                    NavigateToDrill();

                }
                AllowScrollToRegionStart = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedSetLabel));
            }
        }





        public string SelectedSetLabel
        {
            get
            {
                RegionSet? sel = _activeKind switch
                {
                    RegionSetKind.Turn => SelectedTurnSet,
                    RegionSetKind.Mill => SelectedMillSet,
                    RegionSetKind.Drill => SelectedDrillSet,
                    _ => null
                };


                Debug.WriteLine("SelectedSetLabel");


                if (sel == null)
                    return "Selected: (none)";

                string kind = _activeKind.ToString().ToUpperInvariant();
                string status = string.IsNullOrWhiteSpace(sel.StatusText) ? "" : $"  |  {sel.StatusText}";

                // Only jump when we actually have an OK(start..end) status text.
                // Prevents the "first selection doesn't find it" situation.
                if (!string.IsNullOrWhiteSpace(sel.StatusText) &&
                    sel.StatusText.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0)
                {

                    JumpEditorToRegionStart(sel.StatusText, 5);
                }



                return $"Selected: {kind}  |  {sel.Name}{status}";
            }
        }

        private void HookSelectedSet(RegionSet? set)
        {
            if (set == null) return;
            set.PropertyChanged += SelectedSet_PropertyChanged;
        }

        private void UnhookSelectedSet(RegionSet? set)
        {
            if (set == null) return;
            set.PropertyChanged -= SelectedSet_PropertyChanged;
        }

        private void SelectedSet_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RegionSet.Name) ||
                e.PropertyName == nameof(RegionSet.Status) ||
                e.PropertyName == nameof(RegionSet.ResolvedStartLine) ||
                e.PropertyName == nameof(RegionSet.ResolvedEndLine))
            {
                OnPropertyChanged(nameof(SelectedSetLabel));
            }

            // Only jump when the resolved start line becomes known/changes.
            if (_isLoadingProject)
                return;

            if (e.PropertyName != nameof(RegionSet.ResolvedStartLine))
                return;

            if (sender is not RegionSet changed)
                return;

            // Only if this is the currently active selected set
            RegionSet? activeSel = _activeKind switch
            {
                RegionSetKind.Turn => SelectedTurnSet,
                RegionSetKind.Mill => SelectedMillSet,
                RegionSetKind.Drill => SelectedDrillSet,
                _ => null
            };

            if (!ReferenceEquals(changed, activeSel))
                return;


        }


        // ============================================================
        // Constructor / navigation
        // ============================================================
        public MainWindow()
        {
            InitializeComponent();

            DataContext = this;

            _turningPage = new TurningPage();
            _drillPage = new DrillPage();
            _millingPage = new MillPage();
            _transMatrixPage = new TransMatrix();

            MainFrame.Navigated += MainFrame_Navigated;

            // Default page = Turning
            MainFrame.Navigate(_turningPage);

            OnPropertyChanged(nameof(SelectedSetLabel));
        }



        private void TxtGcode_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {

            Debug.WriteLine("TxtGcode_PreviewTextInput");



            if (string.IsNullOrEmpty(e.Text))
                return;

            string up = e.Text.ToUpperInvariant();
            if (up == e.Text)
                return;

            // Replace typed text with uppercase
            e.Handled = true;

            var rtb = (RichTextBox)sender;
            rtb.CaretPosition.InsertTextInRun(up);
            rtb.CaretPosition = rtb.CaretPosition.GetPositionAtOffset(up.Length) ?? rtb.CaretPosition;
        }

        private void TxtGcode_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true))
                return;

            string text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string ?? "";
            string up = text.ToUpperInvariant();
            if (up == text)
                return;

            e.CancelCommand();

            var rtb = (RichTextBox)sender;
            rtb.CaretPosition.InsertTextInRun(up);
        }





        // clipboard for sets (in-memory, not Windows clipboard)
        private object _millSetClipboardItem = null;
        private bool _millSetClipboardIsCut = false;


        private RegionSet _millClipboardItem = null;
        private bool _millClipboardIsCut = false;

        // where the item currently lives (at time of paste we re-check)
        private IList _millClipboardSourceList = null;

        private void MillSets_Cut_Click(object sender, RoutedEventArgs e)
        {
            if (CmbMillSets == null)
                return;

            var list = CmbMillSets.ItemsSource as IList;
            if (list == null || list.Count == 0)
                return;

            var item = CmbMillSets.SelectedItem as RegionSet;
            if (item == null)
                return;

            _millClipboardItem = item;
            _millClipboardIsCut = true;
            _millClipboardSourceList = list;

            // NO removal here (delayed cut until paste)
        }





        private void MillSets_Paste_Click(object sender, RoutedEventArgs e)
        {
            if (CmbMillSets == null)
                return;

            var targetList = CmbMillSets.ItemsSource as IList;
            if (targetList == null)
                return;

            if (_millClipboardItem == null)
                return;

            // insert AFTER current selection, else append
            int insertIndex = targetList.Count;
            var targetSel = CmbMillSets.SelectedItem as RegionSet;
            if (targetSel != null)
            {
                int selIndex = targetList.IndexOf(targetSel);
                if (selIndex >= 0)
                    insertIndex = selIndex + 1;
            }

            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > targetList.Count) insertIndex = targetList.Count;

            if (_millClipboardIsCut)
            {
                // must have a source list to move from
                var sourceList = _millClipboardSourceList;
                if (sourceList == null)
                    return;

                // find current index at paste time (so navigation / edits don't break it)
                int oldIndex = sourceList.IndexOf(_millClipboardItem);
                if (oldIndex < 0)
                    return;

                // moving within the same list: removing shifts indices
                if (ReferenceEquals(sourceList, targetList) && oldIndex < insertIndex)
                    insertIndex--;

                if (insertIndex < 0) insertIndex = 0;
                if (insertIndex > targetList.Count) insertIndex = targetList.Count;

                // do the move
                sourceList.RemoveAt(oldIndex);

                if (insertIndex > targetList.Count) insertIndex = targetList.Count;
                targetList.Insert(insertIndex, _millClipboardItem);

                CmbMillSets.SelectedItem = _millClipboardItem;

                // clear clipboard after cut-paste
                _millClipboardItem = null;
                _millClipboardIsCut = false;
                _millClipboardSourceList = null;

                return;
            }

        }



        private object _turnClipboardItem = null;
        private bool _turnClipboardIsCut = false;
        private IList _turnClipboardSourceList = null;

        private void TurnSets_Cut_Click(object sender, RoutedEventArgs e)
        {
            if (CmbTurnSets == null)
                return;

            var list = CmbTurnSets.ItemsSource as IList;
            if (list == null || list.Count == 0)
                return;

            var item = CmbTurnSets.SelectedItem;
            if (item == null)
                return;

            _turnClipboardItem = item;
            _turnClipboardIsCut = true;
            _turnClipboardSourceList = list;
        }

        private void TurnSets_Paste_Click(object sender, RoutedEventArgs e)
        {
            if (CmbTurnSets == null)
                return;

            var targetList = CmbTurnSets.ItemsSource as IList;
            if (targetList == null)
                return;

            if (_turnClipboardItem == null || !_turnClipboardIsCut)
                return;

            var sourceList = _turnClipboardSourceList;
            if (sourceList == null)
                return;

            int oldIndex = sourceList.IndexOf(_turnClipboardItem);
            if (oldIndex < 0)
                return;

            int insertIndex = targetList.Count;
            var targetSel = CmbTurnSets.SelectedItem;
            if (targetSel != null)
            {
                int selIndex = targetList.IndexOf(targetSel);
                if (selIndex >= 0)
                    insertIndex = selIndex + 1;
            }

            if (ReferenceEquals(sourceList, targetList) && oldIndex < insertIndex)
                insertIndex--;

            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > targetList.Count) insertIndex = targetList.Count;

            sourceList.RemoveAt(oldIndex);

            if (insertIndex > targetList.Count) insertIndex = targetList.Count;
            targetList.Insert(insertIndex, _turnClipboardItem);

            CmbTurnSets.SelectedItem = _turnClipboardItem;

            _turnClipboardItem = null;
            _turnClipboardIsCut = false;
            _turnClipboardSourceList = null;
        }




        private object _drillClipboardItem = null;
        private bool _drillClipboardIsCut = false;
        private IList _drillClipboardSourceList = null;


        private void DrillSets_Cut_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDrillSets == null)
                return;

            var list = CmbDrillSets.ItemsSource as IList;
            if (list == null || list.Count == 0)
                return;

            var item = CmbDrillSets.SelectedItem;
            if (item == null)
                return;

            _drillClipboardItem = item;
            _drillClipboardIsCut = true;
            _drillClipboardSourceList = list;
        }

        private void DrillSets_Paste_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDrillSets == null)
                return;

            var targetList = CmbDrillSets.ItemsSource as IList;
            if (targetList == null)
                return;

            if (_drillClipboardItem == null || !_drillClipboardIsCut)
                return;

            var sourceList = _drillClipboardSourceList;
            if (sourceList == null)
                return;

            int oldIndex = sourceList.IndexOf(_drillClipboardItem);
            if (oldIndex < 0)
                return;

            int insertIndex = targetList.Count;
            var targetSel = CmbDrillSets.SelectedItem;
            if (targetSel != null)
            {
                int selIndex = targetList.IndexOf(targetSel);
                if (selIndex >= 0)
                    insertIndex = selIndex + 1;
            }

            if (ReferenceEquals(sourceList, targetList) && oldIndex < insertIndex)
                insertIndex--;

            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > targetList.Count) insertIndex = targetList.Count;

            sourceList.RemoveAt(oldIndex);

            if (insertIndex > targetList.Count) insertIndex = targetList.Count;
            targetList.Insert(insertIndex, _drillClipboardItem);

            CmbDrillSets.SelectedItem = _drillClipboardItem;

            _drillClipboardItem = null;
            _drillClipboardIsCut = false;
            _drillClipboardSourceList = null;
        }







        private void MainFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (_suppressNextPageActivated)
            {
                _suppressNextPageActivated = false;
                return;
            }

            if (MainFrame.Content is IGcodePage gp)
                gp.OnPageActivated();
        }


        private void NavigateToTurn()
        {
            SyncGcodeLinesFromEditor();
            MainFrame.Navigate(_turningPage);
        }

        private void NavigateToMill()
        {
            SyncGcodeLinesFromEditor();
            MainFrame.Navigate(_millingPage);
        }

        private void NavigateToDrill()
        {
            SyncGcodeLinesFromEditor();
            MainFrame.Navigate(_drillPage);
        }


        // ============================================================
        // Combo/Header clicks
        // ============================================================
        private void TurnHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {

            AllowScrollToRegionStart = true;



            _activeKind = RegionSetKind.Turn;
            NavigateToTurn();
            _turningPage.ApplyTurnSet(SelectedTurnSet);

            OnPropertyChanged(nameof(SelectedSetLabel));
        }

        private void MillHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            AllowScrollToRegionStart = true;
            _activeKind = RegionSetKind.Mill;
            NavigateToMill();
            _millingPage.ApplyMillSet(SelectedMillSet);

            OnPropertyChanged(nameof(SelectedSetLabel));
        }


        private void DrillHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            AllowScrollToRegionStart = true;
            _activeKind = RegionSetKind.Drill;
            NavigateToDrill();

            OnPropertyChanged(nameof(SelectedSetLabel));
        }


        private void CmbTurnSets_DropDownOpened(object sender, EventArgs e)
        {

            _activeKind = RegionSetKind.Turn;
            NavigateToTurn();
            _turningPage.ApplyTurnSet(SelectedTurnSet);

            OnPropertyChanged(nameof(SelectedSetLabel));
        }

        private void CmbMillSets_DropDownOpened(object sender, EventArgs e)
        {

            _activeKind = RegionSetKind.Mill;
            NavigateToMill();
            _millingPage.ApplyMillSet(SelectedMillSet);

            OnPropertyChanged(nameof(SelectedSetLabel));
        }


        private void CmbDrillSets_DropDownOpened(object sender, EventArgs e)
        {

            _activeKind = RegionSetKind.Drill;
            NavigateToDrill();

            OnPropertyChanged(nameof(SelectedSetLabel));
        }

        // ============================================================
        // Editor sync logic
        // ============================================================
        private void SyncGcodeLinesFromEditor()
        {
            if (TxtGcode == null)
                return;

            TextRange tr = new TextRange(TxtGcode.Document.ContentStart, TxtGcode.Document.ContentEnd);
            string allText = tr.Text.Replace("\r\n", "\n");

            GcodeLines.Clear();

            using (StringReader reader = new StringReader(allText))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line;

                    int colonIndex = trimmed.IndexOf(':');
                    if (colonIndex >= 0 && colonIndex < 10)
                        trimmed = trimmed.Substring(colonIndex + 1).TrimStart();

                    GcodeLines.Add(trimmed);
                }
            }
        }


        // ============================================================
        // UNIQUE LINE TAGGING (prevents duplicate-line region ambiguity)
        // ============================================================

        // Set to 0 to disable padding/alignment.

        private static bool LineAlreadyTagged(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;

            int idx = line.LastIndexOf(UNIQUE_TAG_MARKER, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            // We consider it "tagged" only if the marker is near the end and the line ends with ')'
            // e.g. "... (u:a000123)"
            return line.TrimEnd().EndsWith(")", StringComparison.Ordinal) &&
                   idx >= Math.Max(0, line.Length - 32);
        }

        private static bool TryGetExistingLoadId(string line, out char loadId)
        {
            loadId = '\0';
            if (string.IsNullOrEmpty(line)) return false;

            int idx = line.LastIndexOf(UNIQUE_TAG_MARKER, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            int start = idx + UNIQUE_TAG_MARKER.Length;
            if (start >= line.Length) return false;

            char c = line[start];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                loadId = char.ToLowerInvariant(c);
                return true;
            }

            return false;
        }

        private static char GetNextLoadId(IList<string> existingLines)
        {
            char max = (char)('a' - 1);

            if (existingLines != null)
            {
                for (int i = 0; i < existingLines.Count; i++)
                {
                    if (TryGetExistingLoadId(existingLines[i] ?? "", out char id))
                    {
                        if (id > max) max = id;
                    }
                }
            }

            char next = (max < 'a') ? 'a' : (char)(max + 1);
            if (next > 'z') next = 'z'; // clamp (unlikely you’ll hit this)
            return next;
        }

        // Set this to the character column where the "(u:...)" tag should START.
        // If the line is already longer than this, we just append " (u:...)".
        private const int UNIQUE_TAG_PAD_COLUMN = 75;

        private static string EnsureUniqueTag(string rawLine, char loadId, int seq)
        {
            string line = rawLine ?? string.Empty;
            line = line.TrimEnd('\r', '\n');

            if (LineAlreadyTagged(line))
                return line;

            string tag = $"(u:{loadId}{seq:0000})";

            if (line.Length == 0)
                return tag;

            // If the line is short, pad with spaces so the TAG starts at the target column.
            // If the line is already long, just append the tag.
            if (UNIQUE_TAG_PAD_COLUMN > 0 && line.Length < UNIQUE_TAG_PAD_COLUMN)
            {
                line = line.PadRight(UNIQUE_TAG_PAD_COLUMN, ' ');
                return line + tag; // NO extra space: tag starts exactly at the pad column
            }

            return line + " " + tag;
        }









        private const string UNIQUE_TAG_MARKER = "(u:";


        // Unique tag styling: (u:xxxx) — light blue @ ~50% opacity
        private static readonly Brush UniqueTagBrush = UniqueTagColor.UniqueTagBrush;


        public void ColorizeUniqueTagsInEditor()
        {
            if (GcodeEditor?.Document == null)
                return;

            // IMPORTANT:
            // Do NOT foreach over Document.Blocks while editing Runs/Inlines.
            // Copy blocks first to avoid "Collection was modified" from WPF enumerators.
            var blocks = GcodeEditor.Document.Blocks.ToList();

            foreach (var block in blocks)
            {
                if (block is not Paragraph p)
                    continue;

                // Copy runs first so we can safely edit the InlineCollection
                var runs = p.Inlines.OfType<Run>().ToList();

                foreach (var run in runs)
                {
                    string text = run.Text ?? "";
                    int idx = text.LastIndexOf(UNIQUE_TAG_MARKER, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0)
                        continue;

                    int close = text.IndexOf(')', idx);
                    if (close < 0)
                        continue;

                    // Only colorize if the (u:....) tag is at the END of this run
                    if (close != text.Length - 1)
                        continue;

                    string baseText = text.Substring(0, idx);
                    string tagText = text.Substring(idx);

                    var baseRun = new Run(baseText)
                    {
                        Foreground = run.Foreground,
                        FontFamily = run.FontFamily,
                        FontSize = run.FontSize,
                        FontStyle = run.FontStyle,
                        FontWeight = run.FontWeight,
                        FontStretch = run.FontStretch
                    };

                    var tagRun = new Run(tagText)
                    {
                        Foreground = UniqueTagBrush,
                        FontFamily = run.FontFamily,
                        FontSize = run.FontSize,
                        FontStyle = run.FontStyle,
                        FontWeight = run.FontWeight,
                        FontStretch = run.FontStretch
                    };

                    p.Inlines.InsertBefore(run, baseRun);
                    p.Inlines.InsertAfter(baseRun, tagRun);
                    p.Inlines.Remove(run);
                }
            }
        }




        // ============================================================
        // REPLACE YOUR EXISTING METHOD WITH THIS ONE
        // ============================================================
        private void AppendGcodeFromFiles(string[] fileNames)
        {
            if (fileNames == null || fileNames.Length == 0)
                return;

            if (GcodeLines == null)
                throw new InvalidOperationException("GcodeLines is not initialized.");

            // One “load id” letter per BtnLoad click (a, then b, then c...)
            char loadId = GetNextLoadId(GcodeLines);

            // Sequence increments across ALL appended files in this one load click
            int seq = 1;

            for (int f = 0; f < fileNames.Length; f++)
            {
                string path = fileNames[f] ?? "";
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;

                string[] lines = File.ReadAllLines(path);

                for (int i = 0; i < lines.Length; i++)
                {
                    string tagged = EnsureUniqueTag(lines[i], loadId, seq++);
                    GcodeLines.Add(tagged);
                }
            }
        }

        private string GetEditorText()
        {
            if (TxtGcode == null)
                return "";

            TextRange tr = new TextRange(TxtGcode.Document.ContentStart, TxtGcode.Document.ContentEnd);
            return tr.Text.Replace("\r\n", "\n");
        }

        private void SetEditorText(string text)
        {
            text ??= "";
            string normalized = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

            TxtGcode.Document.Blocks.Clear();
            TxtGcode.Document.Blocks.Add(new Paragraph(new Run(normalized)));
        }







        // ============================================================
        // Transform Matrix support helpers
        // ============================================================

        public List<string> GetAllRegionNamesOrdered()
        {
            var outList = new List<string>();

            // Turn -> Mill -> Drill (your required ordering)
            if (TurnSets != null)
                for (int i = 0; i < TurnSets.Count; i++)
                    if (TurnSets[i] != null)
                        outList.Add(TurnSets[i].Name ?? "");

            if (MillSets != null)
                for (int i = 0; i < MillSets.Count; i++)
                    if (MillSets[i] != null)
                        outList.Add(MillSets[i].Name ?? "");

            if (DrillSets != null)
                for (int i = 0; i < DrillSets.Count; i++)
                    if (DrillSets[i] != null)
                        outList.Add(DrillSets[i].Name ?? "");

            return outList;
        }






        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SyncGcodeLinesFromEditor();
            MainFrame.Navigate(new SettingsPage());
        }



        // ============================================================
        // Buttons
        // ============================================================
        private void BtnLoad_Click(object sender, RoutedEventArgs e)
{
    var dlg = new OpenFileDialog
    {
        Filter = "NC Files|*.nc;*.nct;*.ptp;*.prg|All Files|*.*",
        Multiselect = true,
        Title = "Select one or more G-code files (they will be appended)"
    };

    if (dlg.ShowDialog() != true)
        return;

    // IMPORTANT: pull current editor text into GcodeLines first,
    // so "append" truly appends to what's already in the editor.
    SyncGcodeLinesFromEditor();

    // Append into the model list (this adds the (u:...) tags)
    AppendGcodeFromFiles(dlg.FileNames);

    // IMPORTANT: push the model list BACK into the editor so you can see it
    SetEditorText(string.Join(Environment.NewLine, GcodeLines));

    // refresh pages + recolor tags
    if (MainFrame.Content is IGcodePage gp)
        gp.OnGcodeModelLoaded();

    ColorizeUniqueTagsInEditor();
}



        private static bool TryInvokeProjectLoad(object mainWindow, string path, out string error)
        {
            error = "";

            try
            {
                var t = mainWindow.GetType();

                // try common names (add more here if you rename later)
                string[] names =
                {
            "LoadProject",
            "LoadProjectFromFile",
            "LoadProjectFile",
            "LoadProjectFromPath",
            "DoLoadProject"
        };

                foreach (string n in names)
                {
                    var mi = t.GetMethod(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (mi == null) continue;

                    var ps = mi.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    {
                        mi.Invoke(mainWindow, new object[] { path });
                        return true;
                    }
                }

                error = "No project load method found on MainWindow. Expected one of: " + string.Join(", ", names) + "(string path)";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void TrySetMainWindowStringProp(object mainWindow, string propName, string value)
        {
            try
            {
                var p = mainWindow.GetType().GetProperty(propName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                if (p == null) return;
                if (!p.CanWrite) return;
                if (p.PropertyType != typeof(string)) return;

                p.SetValue(mainWindow, value);
            }
            catch { }
        }

        private static void TrySetMainWindowTextBlock(object mainWindow, string fieldOrPropName, string text)
        {
            try
            {
                // try property first
                var p = mainWindow.GetType().GetProperty(fieldOrPropName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                object? obj = null;

                if (p != null && p.CanRead)
                    obj = p.GetValue(mainWindow);

                if (obj == null)
                {
                    // try field
                    var f = mainWindow.GetType().GetField(fieldOrPropName,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                    if (f != null)
                        obj = f.GetValue(mainWindow);
                }

                if (obj is TextBlock tb)
                    tb.Text = text;
            }
            catch { }
        }




        // ============================================================
        // Global unique set naming (Turn + Mill + Drill)
        // ============================================================
        private HashSet<string> BuildAllSetNamesIgnoreCase()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in TurnSets)
                if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                    set.Add(s.Name.Trim());

            foreach (var s in MillSets)
                if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                    set.Add(s.Name.Trim());

            foreach (var s in DrillSets)
                if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                    set.Add(s.Name.Trim());

            return set;
        }

        private static bool TryParseTrailingNumberSuffix(string name, out string baseName, out int suffixN)
        {
            baseName = name ?? "";
            suffixN = 0;

            if (string.IsNullOrWhiteSpace(name))
                return false;

            string t = name.Trim();

            // Matches: "Anything (123)"
            // We keep this local + simple to avoid pulling in Regex here.
            int close = t.LastIndexOf(')');
            int open = (close > 0) ? t.LastIndexOf('(', close) : -1;

            if (open < 0 || close < 0 || close <= open)
                return false;

            // Require a space before '(' to reduce false positives: "ABC(2)" won't be treated as suffix.
            if (open == 0 || t[open - 1] != ' ')
                return false;

            string num = t.Substring(open + 1, close - open - 1).Trim();
            if (!int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return false;

            if (n < 2)
                return false;

            baseName = t.Substring(0, open).TrimEnd();
            suffixN = n;
            return !string.IsNullOrWhiteSpace(baseName);
        }

        private string MakeUniqueSetNameGlobal(string requestedName)
        {
            string desired = (requestedName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(desired))
                return desired;

            HashSet<string> existing = BuildAllSetNamesIgnoreCase();

            // If it's already unique, use it as-is
            if (!existing.Contains(desired))
                return desired;

            // If user typed "Name (N)" and it already exists, increment from N (or 2)
            if (TryParseTrailingNumberSuffix(desired, out string parsedBaseName, out int suffixN))
            {
                int n = Math.Max(2, suffixN);
                while (true)
                {
                    string candidate = $"{parsedBaseName} ({n})";
                    if (!existing.Contains(candidate))
                        return candidate;
                    n++;
                }
            }

            // Normal case: "Name" exists -> "Name (2)", "Name (3)", ...
            string baseNameLocal = desired;
            int n2 = 2;

            while (true)
            {
                string candidate = $"{baseNameLocal} ({n2})";
                if (!existing.Contains(candidate))
                    return candidate;
                n2++;
            }
        }






        private void BtnAddTurn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryPromptForName("New TURN Region Name", "Turn Region", out string name))
                return;

            name = MakeUniqueSetNameGlobal(name);

            var s = new RegionSet(RegionSetKind.Turn, name)
            {
                PageSnapshot = new UiStateSnapshot(),

                // NEW: defaults for tickboxes
                ShowInViewAll = true,
                ExportEnabled = false
            };

            // Turn defaults MUST come from here (store), NOT XAML or TurningPage constructor
            s.PageSnapshot.Values["TxtZExt"] = "-100";
            s.PageSnapshot.Values["NRad"] = "0.8";

            // Test defaults you requested:
            s.PageSnapshot.Values["__ToolUsage"] = "RIGHT";
            s.PageSnapshot.Values["__Quadrant"] = "3";

            TurnSets.Add(s);
            SelectedTurnSet = s;

            OnPropertyChanged(nameof(SelectedSetLabel));
        }






        private void BtnAddMill_Click(object sender, RoutedEventArgs e)
        {
            if (!TryPromptForName("New MILL Region Name", "Mill Region", out string name))
                return;

            name = MakeUniqueSetNameGlobal(name);

            var s = new RegionSet(RegionSetKind.Mill, name)
            {
                PageSnapshot = new UiStateSnapshot(),

                // NEW: defaults for tickboxes
                ShowInViewAll = true,
                ExportEnabled = true
            };

            // Mill defaults (stored ONLY in snapshot)
            s.PageSnapshot.Values["TxtToolDia"] = "12";
            s.PageSnapshot.Values["TxtToolLen"] = "75";

            s.PageSnapshot.Values["Fuseall"] = "0";
            s.PageSnapshot.Values["RemoveSplitter"] = "1";
            s.PageSnapshot.Values["ClipperIsland"] = "1";
            s.PageSnapshot.Values["Clipper"] = "1";

            MillSets.Add(s);
            SelectedMillSet = s;

            OnPropertyChanged(nameof(SelectedSetLabel));
        }




        private void BtnAddDrill_Click(object sender, RoutedEventArgs e)
        {
            if (!TryPromptForName("New DRILL Set Name", "Drill Set", out string name))
                return;

            name = MakeUniqueSetNameGlobal(name);

            var s = new RegionSet(RegionSetKind.Drill, name)
            {
                PageSnapshot = new UiStateSnapshot(),

                // NEW: defaults for tickboxes
                ShowInViewAll = true,
                ExportEnabled = true
            };

            // Drill defaults (stored in snapshot)
            s.PageSnapshot.Values["TxtHoleDia"] = "10";
            s.PageSnapshot.Values["TxtZHoleTop"] = "0";
            s.PageSnapshot.Values["TxtPointAngle"] = "118";
            s.PageSnapshot.Values["TxtChamfer"] = "1";
            s.PageSnapshot.Values["TxtZPlusExt"] = "5";
            s.PageSnapshot.Values["CoordMode"] = "Cartesian";

            // No depth / no holes by default
            s.PageSnapshot.Values["DrillDepthLineText"] = "";

            DrillSets.Add(s);
            SelectedDrillSet = s;

            OnPropertyChanged(nameof(SelectedSetLabel));
        }




        private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            // DELETE FIRST (this is what you were missing)
            switch (_activeKind)
            {
                case RegionSetKind.Mill:
                    DeleteFromList(MillSets, nameof(SelectedMillSet));
                    break;

                case RegionSetKind.Drill:
                    DeleteFromList(DrillSets, nameof(SelectedDrillSet));
                    break;

                default:
                    DeleteFromList(TurnSets, nameof(SelectedTurnSet));
                    break;
            }

            // THEN navigate + apply the (new) selection
            switch (_activeKind)
            {
                case RegionSetKind.Mill:
                    NavigateToMill();
                    if (SelectedMillSet != null)
                        _millingPage.ApplyMillSet(SelectedMillSet);
                    break;

                case RegionSetKind.Drill:
                    NavigateToDrill();
                    if (SelectedDrillSet != null)
                        _drillPage.ApplyDrillSet(SelectedDrillSet);
                    break;

                default:
                    NavigateToTurn();
                    if (SelectedTurnSet != null)
                        _turningPage.ApplyTurnSet(SelectedTurnSet);
                    break;
            }

            OnPropertyChanged(nameof(SelectedSetLabel));
        }


        private void DeleteFromList(ObservableCollection<RegionSet> list, string selectedPropName)
        {
            if (list == null || list.Count == 0)
                return;

            RegionSet? current = selectedPropName switch
            {
                nameof(SelectedTurnSet) => SelectedTurnSet,
                nameof(SelectedMillSet) => SelectedMillSet,
                nameof(SelectedDrillSet) => SelectedDrillSet,
                _ => null
            };

            if (current == null)
                return;

            int idx = list.IndexOf(current);
            if (idx < 0)
                return;

            list.RemoveAt(idx);

            RegionSet? newSel = null;
            if (list.Count > 0)
            {
                int newIdx = idx;
                if (newIdx >= list.Count) newIdx = list.Count - 1;
                if (newIdx < 0) newIdx = 0;
                newSel = list[newIdx];
            }

            if (selectedPropName == nameof(SelectedTurnSet))
            {
                _selectedTurnSet = null;
                SelectedTurnSet = newSel;
            }
            else if (selectedPropName == nameof(SelectedMillSet))
            {
                _selectedMillSet = null;
                SelectedMillSet = newSel;
            }
            else if (selectedPropName == nameof(SelectedDrillSet))
            {
                _selectedDrillSet = null;
                SelectedDrillSet = newSel;
            }
        }

        // ============================================================
        // Project Save/Load (Universal)
        // ============================================================
        private const int PROJECT_VERSION = 2;

        private sealed class ProjectDto
        {
            public int Version { get; set; } = PROJECT_VERSION;
            public string SavedAtUtc { get; set; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            public string GcodeText { get; set; } = "";

            public List<RegionSetDto> TurnSets { get; set; } = new();
            public List<RegionSetDto> MillSets { get; set; } = new();
            public List<RegionSetDto> DrillSets { get; set; } = new();

            public RegionSetKind ActiveKind { get; set; } = RegionSetKind.Turn;
            public List<CNC_Improvements_gcode_solids.SetManagement.TransformMatrixDto> TransformMatrices { get; set; } = new();

            public Guid? SelectedTurnId { get; set; }
            public Guid? SelectedMillId { get; set; }
            public Guid? SelectedDrillId { get; set; }
        }

        private sealed class RegionSetDto
        {

            public bool ShowInViewAll { get; set; } = true;
            public bool ExportEnabled { get; set; } = true;



            public Guid Id { get; set; }
            public RegionSetKind Kind { get; set; }
            public string Name { get; set; } = "";
            public List<string> RegionLines { get; set; } = new();
            public Dictionary<string, string> SnapshotValues { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        }






        private static JsonSerializerOptions CreateProjectJsonOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true
            };
        }






        private static RegionSetDto ToDto(RegionSet s)
        {
            var dto = new RegionSetDto
            {
                // NEW: persist per-set toggles
                ShowInViewAll = s.ShowInViewAll,
                ExportEnabled = s.ExportEnabled,

                Id = s.Id,
                Kind = s.Kind,
                Name = s.Name ?? ""
            };

            // region lines
            if (s.RegionLines != null)
                dto.RegionLines = new List<string>(s.RegionLines);

            // snapshot values
            if (s.PageSnapshot?.Values != null)
                dto.SnapshotValues = new Dictionary<string, string>(s.PageSnapshot.Values, StringComparer.Ordinal);

            return dto;
        }


        private static RegionSet FromDto(RegionSetDto dto)
        {
            var s = new RegionSet(dto.Kind, dto.Name ?? "")
            {
                PageSnapshot = new UiStateSnapshot()
            };

            // NEW: restore per-set toggles
            s.ShowInViewAll = dto.ShowInViewAll;
            s.ExportEnabled = dto.ExportEnabled;

            // Restore region lines
            s.RegionLines.Clear();
            if (dto.RegionLines != null)
            {
                foreach (var line in dto.RegionLines)
                    s.RegionLines.Add(line ?? "");
            }

            // Restore snapshot
            if (dto.SnapshotValues != null)
            {
                foreach (var kv in dto.SnapshotValues)
                    s.PageSnapshot.Values[kv.Key] = kv.Value ?? "";
            }

            return s;
        }



        private static RegionSet? FindById(ObservableCollection<RegionSet> list, Guid? id)
        {
            if (list == null || list.Count == 0) return null;
            if (!id.HasValue) return null;

            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && s.Id == id.Value)
                    return s;
            }
            return null;
        }

        private void BtnSaveProject_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SyncGcodeLinesFromEditor();

                var dto = new ProjectDto
                {
                    Version = PROJECT_VERSION,
                    SavedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    GcodeText = GetEditorText(),

                    ActiveKind = _activeKind,

                    SelectedTurnId = SelectedTurnSet?.Id,
                    SelectedMillId = SelectedMillSet?.Id,
                    SelectedDrillId = SelectedDrillSet?.Id
                };

                foreach (var s in TurnSets) dto.TurnSets.Add(ToDto(s));
                foreach (var s in MillSets) dto.MillSets.Add(ToDto(s));
                foreach (var s in DrillSets) dto.DrillSets.Add(ToDto(s));


                // Save transformation matrices (independent block)
                dto.TransformMatrices = _transMatrixPage.ExportTransformDtos();

                string loadedFile;

                if (projectName == "")
                {
                    loadedFile = "project.npcproj";
                }
                else
                {

                    loadedFile = projectName;
                }


                var dlg = new SaveFileDialog
                {
                    Filter = "NPC Project|*.npcproj|JSON|*.json|All Files|*.*",
                    FileName = loadedFile,
                    Title = "Save NPC Project"
                };

                if (dlg.ShowDialog() != true)
                    return;





                string json = JsonSerializer.Serialize(dto, CreateProjectJsonOptions());
                File.WriteAllText(dlg.FileName, json, Encoding.UTF8);

                // NEW: remember the project folder for future exports
                CurrentProjectDirectory = Path.GetDirectoryName(dlg.FileName) ?? "";
                SetProjectNameFromProjectFilePath(dlg.FileName);
                MessageBox.Show("Project saved.", "NPC G-code Solids",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Save Project failed:\n\n" + ex.Message, "NPC G-code Solids",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void BtnLoadProject_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                const string PROJECT_EXT = ".npcproj";


                // - user can pick an existing project
                // - OR type a new name (non-existing) and we treat it as "new project"
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "NPC Project|*.npcproj;*.json|All Files|*.*",
                    Title = "Load or Create NPC Project",
                    AddExtension = true,
                    DefaultExt = PROJECT_EXT.TrimStart('.'),

                    // IMPORTANT: allow user to type a new (non-existing) filename
                    CheckFileExists = false,
                    CheckPathExists = true,

                    Multiselect = false
                };

                // Start in last-known project dir if available
                try
                {
                    if (!string.IsNullOrWhiteSpace(CurrentProjectDirectory) && Directory.Exists(CurrentProjectDirectory))
                        dlg.InitialDirectory = CurrentProjectDirectory;
                }
                catch { }

                if (dlg.ShowDialog() != true)
                    return;

                string path = (dlg.FileName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(path))
                    return;


            



                // If user typed without extension, force .npcproj
                if (!path.EndsWith(".npcproj", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    path += PROJECT_EXT;
                }

                // NEW: remember the project folder for future exports
                CurrentProjectDirectory = Path.GetDirectoryName(path) ?? "";
                SetProjectNameFromProjectFilePath(path);

                // ------------------------------------------------------------
                // NEW PROJECT (typed a name that doesn't exist):
                // clear everything (but keep the standard first transform via ImportTransformDtos(null) flow)
                // then SAVE an empty project file under the typed name.
                // ------------------------------------------------------------
                if (!File.Exists(path))
                {
                    _isLoadingProject = true;
                    try
                    {
                        // Clear editor first
                        SetEditorText("");

                        // Refresh model from editor
                        SyncGcodeLinesFromEditor();

                        // Reset pages (new model)
                        _turningPage.OnGcodeModelLoaded();
                        _millingPage.OnGcodeModelLoaded();
                        _drillPage.OnGcodeModelLoaded();

                        // Clear sets
                        TurnSets.Clear();
                        MillSets.Clear();
                        DrillSets.Clear();

                        // Reset transforms to default/standard (Import null then reconcile)
                        _transMatrixPage.ImportTransformDtos(null);
                        _transMatrixPage.RefreshFromMainWindow(this);

                        _selectedTurnSet = null;
                        _selectedMillSet = null;
                        _selectedDrillSet = null;

                        SelectedTurnSet = (TurnSets.Count > 0 ? TurnSets[0] : null);
                        SelectedMillSet = (MillSets.Count > 0 ? MillSets[0] : null);
                        SelectedDrillSet = (DrillSets.Count > 0 ? DrillSets[0] : null);

                        _activeKind = RegionSetKind.Turn;
                    }
                    finally
                    {
                        _isLoadingProject = false;
                    }

                    // Build a minimal new DTO and write it to disk
                    var newDto = new ProjectDto
                    {
                        Version = PROJECT_VERSION,
                        GcodeText = "",
                        TurnSets = new List<RegionSetDto>(),
                        MillSets = new List<RegionSetDto>(),
                        DrillSets = new List<RegionSetDto>(),
                        TransformMatrices = null,
                        SelectedTurnId = null,
                        SelectedMillId = null,
                        SelectedDrillId = null,
                        ActiveKind = _activeKind
                    };

                    string outJson = JsonSerializer.Serialize(newDto, CreateProjectJsonOptions());
                    File.WriteAllText(path, outJson, Encoding.UTF8);

                    // Navigate to TURN by default
                    NavigateToTurn();

                    OnPropertyChanged(nameof(SelectedSetLabel));

                    MessageBox.Show("New project created.", "NPC G-code Solids",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    return;
                }

                // ------------------------------------------------------------
                // EXISTING PROJECT: load as before
                // ------------------------------------------------------------
                string json = File.ReadAllText(path, Encoding.UTF8);

                var dto = JsonSerializer.Deserialize<ProjectDto>(json, CreateProjectJsonOptions());
                if (dto == null)
                    throw new Exception("Project file could not be parsed.");

                if (dto.Version != PROJECT_VERSION)
                    throw new Exception($"Unsupported project version {dto.Version}. Expected {PROJECT_VERSION}.");

                _isLoadingProject = true;
                try
                {
                    // Load editor text FIRST
                    SetEditorText(dto.GcodeText ?? "");

                    // Refresh model from editor
                    SyncGcodeLinesFromEditor();

                    // Reset pages (new model)
                    _turningPage.OnGcodeModelLoaded();
                    _millingPage.OnGcodeModelLoaded();
                    _drillPage.OnGcodeModelLoaded();

                    // Rebuild sets
                    TurnSets.Clear();
                    MillSets.Clear();
                    DrillSets.Clear();

                    if (dto.TurnSets != null)
                        foreach (var s in dto.TurnSets) TurnSets.Add(FromDto(s));

                    if (dto.MillSets != null)
                        foreach (var s in dto.MillSets) MillSets.Add(FromDto(s));

                    if (dto.DrillSets != null)
                        foreach (var s in dto.DrillSets) DrillSets.Add(FromDto(s));

                    // Load transformation matrices (independent block), then reconcile with current sets
                    _transMatrixPage.ImportTransformDtos(dto.TransformMatrices);
                    _transMatrixPage.RefreshFromMainWindow(this);

                    _selectedTurnSet = null;
                    _selectedMillSet = null;
                    _selectedDrillSet = null;

                    SelectedTurnSet = FindById(TurnSets, dto.SelectedTurnId) ?? (TurnSets.Count > 0 ? TurnSets[0] : null);
                    SelectedMillSet = FindById(MillSets, dto.SelectedMillId) ?? (MillSets.Count > 0 ? MillSets[0] : null);
                    SelectedDrillSet = FindById(DrillSets, dto.SelectedDrillId) ?? (DrillSets.Count > 0 ? DrillSets[0] : null);

                    _activeKind = dto.ActiveKind;
                }
                finally
                {
                    _isLoadingProject = false;
                }

                // Navigate to the active kind last
                switch (_activeKind)
                {
                    case RegionSetKind.Mill:
                        NavigateToMill();
                        _millingPage.ApplyMillSet(SelectedMillSet);
                        break;

                    case RegionSetKind.Drill:
                        NavigateToDrill();
                        break;

                    default:
                        NavigateToTurn();
                        _turningPage.ApplyTurnSet(SelectedTurnSet);
                        break;
                }

                OnPropertyChanged(nameof(SelectedSetLabel));

                MessageBox.Show("Project loaded.", "NPC G-code Solids",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Load Project failed:\n\n" + ex.Message, "NPC G-code Solids",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }



        // ============================================================
        // Simple name prompt
        // ============================================================
        private bool TryPromptForName(string title, string defaultValue, out string name)
        {
            name = "";

            var win = new Window
            {
                Title = title,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 420,
                Height = 160,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tb = new TextBlock
            {
                Text = "Name:",
                Margin = new Thickness(0, 0, 0, 6),
                FontWeight = FontWeights.Bold
            };
            Grid.SetRow(tb, 0);

            var box = new TextBox
            {
                Text = defaultValue ?? "",
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(box, 1);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(btnRow, 2);

            var ok = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };

            ok.Click += (_, __) => { win.DialogResult = true; win.Close(); };
            cancel.Click += (_, __) => { win.DialogResult = false; win.Close(); };

            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);

            root.Children.Add(tb);
            root.Children.Add(box);
            root.Children.Add(btnRow);

            win.Content = root;

            win.Loaded += (_, __) =>
            {
                box.Focus();
                box.SelectAll();
            };

            bool? result = win.ShowDialog();
            if (result != true)
                return false;

            string v = (box.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(v))
                return false;

            name = v;
            return true;
        }

        // ============================================================
        // INotifyPropertyChanged
        // ============================================================
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));







        private void BtnExportAll_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // One final message only.
            // Pipeline:
            //  1) Delete export artifacts (*.stp/*.step/*.txt/*.log)
            //  2) Export regions (Turn/Mill/Drill) USING THE CURRENT LIVE PAGE INSTANCES
            //     (prevents exporting stale/deleted sets from old page state)
            //     **NOW FILTERED BY set.ExportEnabled**
            //  3) Wait for region STEP files to exist + be stable
            //  4) Run _All script + wait for its .log
            //  5) Run _Fused script + wait for its .log
            //  6) Read logs, show success/fail. If fail and LogWindowShow, open log text.

            if (string.IsNullOrWhiteSpace(projectName))
            {
                MessageBox.Show(
                    "Save or load a project first so an export folder exists.",
                    "Export All",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // Reset FreeCAD script suffix counters for THIS run
            CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunSuffix.ResetAll();

            // Authoritative list for THIS run (populated by the batch exporters)
            ExportAllCreatedStepFiles.Clear();

            var failures = new System.Collections.Generic.List<string>();

            try { CNC_Improvements_gcode_solids.Utilities.UiUtilities.CloseAllToolWindows(); } catch { /* ignore */ }

            string exportDir;
            try
            {
                exportDir = GetExportDirectory();
                if (string.IsNullOrWhiteSpace(exportDir) || !System.IO.Directory.Exists(exportDir))
                    throw new Exception("Export directory is not valid.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Export All", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // ------------------------------------------------------------
            // Local helpers (delete + wait stable + log parsing)
            // ------------------------------------------------------------
            void DeleteExportArtifacts()
            {
                // Only top dir. Delete: .stp .step .txt .log
                string[] exts = new[] { ".stp", ".step", ".txt", ".log" };

                foreach (var f in System.IO.Directory.EnumerateFiles(exportDir, "*.*", System.IO.SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string ext = System.IO.Path.GetExtension(f) ?? "";
                        if (!exts.Any(e2 => ext.Equals(e2, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // Try remove RO
                        try
                        {
                            var fi = new System.IO.FileInfo(f);
                            if (fi.Exists && fi.IsReadOnly)
                                fi.IsReadOnly = false;
                        }
                        catch { /* ignore */ }

                        System.IO.File.Delete(f);
                    }
                    catch
                    {
                        // do not hard-fail export-all for delete issues; log later if needed
                    }
                }
            }

            bool IsStableOnDisk(string path, int stableChecks, int stableSleepMs)
            {
                try
                {
                    long lastLen = -1;
                    long lastTicks = -1;

                    for (int i = 0; i < stableChecks; i++)
                    {
                        var fi = new System.IO.FileInfo(path);
                        if (!fi.Exists || fi.Length <= 0)
                            return false;

                        long len = fi.Length;
                        long ticks = fi.LastWriteTimeUtc.Ticks;

                        if (i > 0)
                        {
                            if (len != lastLen || ticks != lastTicks)
                                return false;
                        }

                        lastLen = len;
                        lastTicks = ticks;

                        if (i < stableChecks - 1)
                            System.Threading.Thread.Sleep(stableSleepMs);
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }

            bool WaitForFileArriveAndStable(string path, int timeoutMs, out string reason)
            {
                reason = "";

                int sleepMs = 120;
                int attempts = Math.Max(1, timeoutMs / sleepMs);

                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    try
                    {
                        var fi = new System.IO.FileInfo(path);
                        if (fi.Exists && fi.Length > 0)
                        {
                            // Ensure settled (avoid half-written)
                            if (IsStableOnDisk(path, stableChecks: 3, stableSleepMs: 200))
                                return true;
                        }
                    }
                    catch { /* ignore */ }

                    System.Threading.Thread.Sleep(sleepMs);
                }

                reason = "Timed out waiting for file to exist and become stable.";
                return false;
            }

            static string SafeReadAllText(string path)
            {
                try
                {
                    // allow the writer to close
                    System.Threading.Thread.Sleep(50);
                    return System.IO.File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    return $"(Failed to read log file: {ex.Message})\nPATH: {path}";
                }
            }

            static bool LogLooksFail(string logText)
            {
                if (string.IsNullOrWhiteSpace(logText)) return true;

                // Fuse script: STATUS: FAIL / SUCCESS
                if (logText.IndexOf("STATUS: FAIL", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                // All script: MERGE_FAIL / MERGE_OK
                if (logText.IndexOf("MERGE_FAIL", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (logText.IndexOf("=== MERGE FAILED ===", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                return false;
            }

            static bool LogLooksSuccess(string logText)
            {
                if (string.IsNullOrWhiteSpace(logText)) return false;

                if (logText.IndexOf("STATUS: SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (logText.IndexOf("MERGE_OK", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                return false;
            }

            // ------------------------------------------------------------
            // Build deterministic "planned output filenames" for LogWindow
            // AND keep FuseAllTurn/Mill/Drill IN SCOPE for later use.
            // NOW FILTERED BY set.ExportEnabled
            // ------------------------------------------------------------
            var sbPlanned = new StringBuilder();

            var sbTurn = new StringBuilder();
            var sbMill = new StringBuilder();
            var sbDrill = new StringBuilder();

            sbPlanned.AppendLine("=== EXPORT ALL : PLANNED OUTPUT FILES ===");
            sbPlanned.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sbPlanned.AppendLine($"ExportDir: {exportDir}");
            sbPlanned.AppendLine("");

            sbPlanned.AppendLine("TURNING SETS (ExportEnabled only):");
            string FuseAllTurn = AppendPlannedExportFiles(
                sbTurn,
                this.TurnSets,
                exportDir,
                stepSuffix: "_Turn_stp.stp");
            sbPlanned.AppendLine(FuseAllTurn);
            sbPlanned.AppendLine("");

            sbPlanned.AppendLine("MILL SETS (ExportEnabled only):");
            string FuseAllMill = AppendPlannedExportFiles(
                sbMill,
                this.MillSets,
                exportDir,
                stepSuffix: "_Mill_stp.stp");
            sbPlanned.AppendLine(FuseAllMill);
            sbPlanned.AppendLine("");

            sbPlanned.AppendLine("DRILL SETS (ExportEnabled only):");
            string FuseAllDrill = AppendPlannedExportFiles(
                sbDrill,
                this.DrillSets,
                exportDir,
                stepSuffix: "_Holes_stp.stp");
            sbPlanned.AppendLine(FuseAllDrill);
            sbPlanned.AppendLine("");

            // Optional: show planned list
            if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
            {
                var owner = Window.GetWindow(this);
                var logWindow = new LogWindow("EXPORT ALL : PLANNED OUTPUT FILES", sbPlanned.ToString());
                if (owner != null)
                    logWindow.Owner = owner;
                logWindow.Show();
            }

            // -------- Project stem -> merged output name --------
            static string MakeSafeFileBase(string s)
            {
                if (s == null) s = "";
                s = s.Trim();

                foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                    s = s.Replace(c, '_');

                s = s.Trim().TrimEnd('.', ' ');
                if (string.IsNullOrWhiteSpace(s))
                    s = "All_solid_export";

                return s;
            }

            string projStem = System.IO.Path.GetFileNameWithoutExtension(projectName ?? "");
            projStem = MakeSafeFileBase(projStem);

            string mergedOutStep = System.IO.Path.Combine(exportDir, $"{projStem}_All_stp.stp");
            string fusedOutStep = System.IO.Path.Combine(exportDir, $"{projStem}_Fused_stp.stp");

            // Expected logs (your scripts write base + ".log")
            string mergedOutLog = System.IO.Path.Combine(exportDir, $"{projStem}_All_stp.log");
            string fusedOutLog = System.IO.Path.Combine(exportDir, $"{projStem}_Fused_stp.log");

            // ------------------------------------------------------------
            // 1) DELETE export artifacts FIRST (simplifies waiting)
            // ------------------------------------------------------------
            try
            {
                DeleteExportArtifacts();
            }
            catch (Exception ex)
            {
                failures.Add($"DELETE: Failed to delete old export artifacts — {ex.Message}");
                // continue anyway
            }

            // ------------------------------------------------------------
            // 2) EXPORT REGION SETS (ExportEnabled only)
            // ------------------------------------------------------------
            IsExportAllRunning = true;
            try
            {
                // Make sure model is up to date before batch export
                SyncGcodeLinesFromEditor();

                // -----------------------------
                // MILL
                // -----------------------------
                try
                {
                    if (MillSets != null)
                    {
                        for (int i = 0; i < MillSets.Count; i++)
                        {
                            var set = MillSets[i];
                            if (set == null) continue;

                            if (!set.ExportEnabled)
                                continue;

                            bool ok;
                            string reason;

                            try
                            {
                                ok = _millingPage.ExportSetBatch(set, exportDir, out reason);
                            }
                            catch (Exception ex)
                            {
                                ok = false;
                                reason = ex.Message;
                            }

                            if (!ok)
                            {
                                string nm = string.IsNullOrWhiteSpace(set.Name) ? $"(unnamed mill set #{i + 1})" : set.Name;
                                failures.Add($"MILL: {nm} — {reason}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"MILL: ExportAll failed to run — {ex.Message}");
                }

                // -----------------------------
                // DRILL
                // -----------------------------
                try
                {
                    if (DrillSets != null)
                    {
                        for (int i = 0; i < DrillSets.Count; i++)
                        {
                            var set = DrillSets[i];
                            if (set == null) continue;

                            if (!set.ExportEnabled)
                                continue;

                            bool ok;
                            string reason;

                            try
                            {
                                ok = _drillPage.ExportSetBatch(set, exportDir, out reason);
                            }
                            catch (Exception ex)
                            {
                                ok = false;
                                reason = ex.Message;
                            }

                            if (!ok)
                            {
                                string nm = string.IsNullOrWhiteSpace(set.Name) ? $"(unnamed drill set #{i + 1})" : set.Name;
                                failures.Add($"DRILL: {nm} — {reason}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"DRILL: ExportAll failed to run — {ex.Message}");
                }

                // -----------------------------
                // TURN
                // -----------------------------
                try
                {
                    if (TurnSets == null || TurnSets.Count == 0)
                    {
                        failures.Add("TURN: No turning sets.");
                    }
                    else
                    {
                        for (int i = 0; i < TurnSets.Count; i++)
                        {
                            var set = TurnSets[i];
                            if (set == null) continue;

                            if (!set.ExportEnabled)
                                continue;

                            bool ok;
                            string reason;

                            try
                            {
                                ok = _turningPage.ExportSetBatch(set, exportDir, out reason);
                            }
                            catch (Exception ex)
                            {
                                ok = false;
                                reason = ex.Message;
                            }

                            if (!ok)
                            {
                                string nm = string.IsNullOrWhiteSpace(set.Name) ? $"(unnamed turning set #{i + 1})" : set.Name;
                                failures.Add($"TURN: {nm} — {reason}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"TURN: ExportAll error — {ex.Message}");
                }
            }
            finally
            {
                IsExportAllRunning = false;
            }

            // ------------------------------------------------------------
            // 3) WAIT for region STEP files (simplified: we deleted first)
            //    Use ExportAllCreatedStepFiles (authoritative from exporters).
            // ------------------------------------------------------------
            var verifiedMergeInputs = new System.Collections.Generic.List<string>();

            try
            {
                var uniq = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var p in ExportAllCreatedStepFiles)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    if (!uniq.Add(p)) continue;

                    // Only .stp/.step region outputs
                    string ext = System.IO.Path.GetExtension(p) ?? "";
                    if (!ext.Equals(".stp", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".step", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Never allow merge/fuse outputs to become inputs
                    if (string.Equals(p, mergedOutStep, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(p, fusedOutStep, StringComparison.OrdinalIgnoreCase)) continue;

                    if (WaitForFileArriveAndStable(p, timeoutMs: 60000, out string why))
                    {
                        verifiedMergeInputs.Add(p);
                    }
                    else
                    {
                        failures.Add($"REGION_STEP_WAIT: {System.IO.Path.GetFileName(p)} — {why}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"REGION_STEP_WAIT: Failed — {ex.Message}");
            }

            // Deterministic order for merge script
            verifiedMergeInputs = verifiedMergeInputs
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ------------------------------------------------------------
            // 4) RUN _ALL script (merge) and wait for its LOG (only)
            // ------------------------------------------------------------
            string? allLogText = null;
            bool allOk = false;

            try
            {
                if (verifiedMergeInputs.Count == 0)
                {
                    failures.Add("MERGE: No region STEP files were verified, skipping _All script.");
                }
                else
                {
                    // Run script
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptExportAll.ProjectName = projStem;
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptExportAll.OutStepPath = mergedOutStep;
                    CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptExportAll.FilesToMerge =
                        new System.Collections.Generic.List<string>(verifiedMergeInputs);

                    string scriptPath = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerExportAllRunner.SaveScript();
                    _ = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerExportAllRunner.RunFreeCad(scriptPath, exportDir);

                    // Wait for LOG (exists regardless of success/fail)
                    if (WaitForFileArriveAndStable(mergedOutLog, timeoutMs: 60000, out string whyLog))
                    {
                        allLogText = SafeReadAllText(mergedOutLog);

                        // Decide success/fail by log content
                        if (LogLooksFail(allLogText))
                        {
                            failures.Add($"ALL_SCRIPT: FAIL — {System.IO.Path.GetFileName(mergedOutLog)}");
                        }
                        else if (LogLooksSuccess(allLogText))
                        {
                            allOk = true;
                        }
                        else
                        {
                            // Unknown -> treat as fail so you always see it
                            failures.Add($"ALL_SCRIPT: UNKNOWN STATUS — {System.IO.Path.GetFileName(mergedOutLog)}");
                        }
                    }
                    else
                    {
                        failures.Add($"ALL_LOG_WAIT: {System.IO.Path.GetFileName(mergedOutLog)} — {whyLog}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"ALL_SCRIPT: Failed to run — {ex.Message}");
            }

            // ------------------------------------------------------------
            // 5) RUN FUSE script and wait for its LOG (only)
            // ------------------------------------------------------------
            string? fuseLogText = null;
            bool fuseOk = false;

            try
            {
                // Run script (can fail internally; log should still be produced)
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptFuse.ProjectName = projStem;
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptFuse.OutStepPath = fusedOutStep;

                // IMPORTANT: these are the planned ordered lists you built at the top (ExportEnabled only)
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptFuse.FuseTurnFiles = FuseAllTurn;
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptFuse.FuseMillFiles = FuseAllMill;
                CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadScriptFuse.FuseDrillFiles = FuseAllDrill;

                string scriptPath = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerFuse.SaveScript();
                _ = CNC_Improvements_gcode_solids.FreeCadIntegration.FreeCadRunnerFuse.RunFreeCad(scriptPath, exportDir);

                if (WaitForFileArriveAndStable(fusedOutLog, timeoutMs: 60000, out string whyLog))
                {
                    fuseLogText = SafeReadAllText(fusedOutLog);

                    if (LogLooksFail(fuseLogText))
                    {
                        failures.Add($"FUSE_SCRIPT: FAIL — {System.IO.Path.GetFileName(fusedOutLog)}");
                    }
                    else if (LogLooksSuccess(fuseLogText))
                    {
                        fuseOk = true;
                    }
                    else
                    {
                        failures.Add($"FUSE_SCRIPT: UNKNOWN STATUS — {System.IO.Path.GetFileName(fusedOutLog)}");
                    }
                }
                else
                {
                    failures.Add($"FUSE_LOG_WAIT: {System.IO.Path.GetFileName(fusedOutLog)} — {whyLog}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"FUSE_SCRIPT: Failed to run — {ex.Message}");
            }

            // ------------------------------------------------------------
            // 6) If a script failed and LogWindowShow -> show the full failed log(s)
            // ------------------------------------------------------------
            if (CNC_Improvements_gcode_solids.Properties.Settings.Default.LogWindowShow)
            {
                var owner = Window.GetWindow(this);

                // Show ALL log if it looks failed/unknown
                if (!string.IsNullOrWhiteSpace(allLogText) && !allOk)
                {
                    var lw = new LogWindow($"EXPORT ALL : ALL SCRIPT LOG ({System.IO.Path.GetFileName(mergedOutLog)})", allLogText);
                    if (owner != null) lw.Owner = owner;
                    lw.Show();
                }

                // Show FUSE log if it looks failed/unknown
                if (!string.IsNullOrWhiteSpace(fuseLogText) && !fuseOk)
                {
                    var lw = new LogWindow($"EXPORT ALL : FUSE SCRIPT LOG ({System.IO.Path.GetFileName(fusedOutLog)})", fuseLogText);
                    if (owner != null) lw.Owner = owner;
                    lw.Show();
                }
            }

            // ------------------------------------------------------------
            // Final MessageBox (simple OK vs fail summary)
            // ------------------------------------------------------------
            if (failures.Count == 0)
            {
                MessageBox.Show(
                    "Export all complete.\n\nALL: OK\nFUSE: OK",
                    "Export All",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // cap summary, but logs are available in LogWindow when enabled
            int cap = 14;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Export all complete — {failures.Count} issue(s):");
            sb.AppendLine();

            for (int i = 0; i < Math.Min(failures.Count, cap); i++)
                sb.AppendLine(failures[i]);

            if (failures.Count > cap)
                sb.AppendLine($"\n(+{failures.Count - cap} more)");

            MessageBox.Show(sb.ToString(), "Export All", MessageBoxButton.OK, MessageBoxImage.Warning);
        }





        // ============================================================
        // ExportAll: build a deterministic filename list for fuse all
        // Uses MainWindow.SanitizeFileStem so it matches per-page exports.
        // ============================================================
        private static string AppendPlannedExportFiles(
    StringBuilder sb,
    IEnumerable<RegionSet>? sets,
    string exportDir,
    string stepSuffix)
        {
            if (sets == null)
            {
                sb.AppendLine("  (none)");
                sb.AppendLine();
                return "";
            }

            int shown = 0;

            foreach (var set in sets)
            {
                if (set == null) continue;

                // NEW: only include sets that are enabled for export
                if (!set.ExportEnabled)
                    continue;

                string setName = string.IsNullOrWhiteSpace(set.Name) ? "(unnamed)" : set.Name.Trim();
                string safe = SanitizeFileStem(setName);

                string stepName = $"{safe}{stepSuffix}";

                // This is intentionally the python-style r"dir\file", line format you already used
                sb.AppendLine($"r\"{exportDir}\\{stepName}\",");

                shown++;
            }

            if (shown == 0)
                sb.AppendLine("  (none)");

            sb.AppendLine();
            return sb.ToString();
        }




        // Reference-equality comparer for de-duping RegionSet references
        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();
            public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }









        // ============================================================
        // EXPORT ALL (batch) support
        // ============================================================

        internal bool IsExportAllRunning { get; private set; } = false;

        internal string GetExportDirectory()
        {
            // You said CurrentProjectDirectory is the directory store.
            // Fall back safely if unset.
            if (!string.IsNullOrWhiteSpace(CurrentProjectDirectory) && Directory.Exists(CurrentProjectDirectory))
                return CurrentProjectDirectory;

            return Environment.CurrentDirectory;
        }







        public static string SanitizeFileStem(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "unnamed";

            string s = name.Trim();

            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            // extra safety
            s = s.Replace(' ', '_');

            if (string.IsNullOrWhiteSpace(s))
                return "unnamed";

            return s;
        }





        private void BtnCopyRegion(object sender, RoutedEventArgs e)
        {
            // Copy the currently selected RegionSet (based on _activeKind),
            // including RegionLines + PageSnapshot values, into a new RegionSet.
            // New set name is suggested as "<SelectedName> (2)" / (3) / ... (global unique).

            RegionSet? src = _activeKind switch
            {
                RegionSetKind.Turn => SelectedTurnSet,
                RegionSetKind.Mill => SelectedMillSet,
                RegionSetKind.Drill => SelectedDrillSet,
                _ => null
            };

            if (src == null)
                return;

            string baseName = string.IsNullOrWhiteSpace(src.Name) ? "Region" : src.Name.Trim();

            // Suggest a globally-unique default copy name
            string suggestedName = MakeUniqueSetNameGlobal(baseName);

            // Let user accept/edit the suggested name
            if (!TryPromptForName($"Copy {_activeKind.ToString().ToUpperInvariant()} Name", suggestedName, out string name))
                return;

            // Enforce global uniqueness (in case user typed an existing name)
            name = MakeUniqueSetNameGlobal(name);

            var copy = new RegionSet(src.Kind, name)
            {
                PageSnapshot = new UiStateSnapshot()
            };

            // Copy RegionLines
            copy.RegionLines.Clear();
            if (src.RegionLines != null)
            {
                foreach (var line in src.RegionLines)
                    copy.RegionLines.Add(line ?? "");
            }

            // Copy Snapshot values
            if (src.PageSnapshot?.Values != null)
            {
                foreach (var kv in src.PageSnapshot.Values)
                    copy.PageSnapshot.Values[kv.Key] = kv.Value ?? "";
            }

            // Add to the correct list + select it
            switch (src.Kind)
            {
                case RegionSetKind.Turn:
                    TurnSets.Add(copy);
                    SelectedTurnSet = copy;
                    break;

                case RegionSetKind.Mill:
                    MillSets.Add(copy);
                    SelectedMillSet = copy;
                    break;

                case RegionSetKind.Drill:
                    DrillSets.Add(copy);
                    SelectedDrillSet = copy;
                    break;
            }

            OnPropertyChanged(nameof(SelectedSetLabel));
        }


        private void BtnLoadTransEditor_Click(object sender, RoutedEventArgs e)
        {
            SyncGcodeLinesFromEditor();

            // Every open: re-read current region set names (Turn -> Mill -> Drill)
            // and let the TransMatrix page reconcile + re-order its card lists.
            _transMatrixPage.RefreshFromMainWindow(this);

            MainFrame.Navigate(_transMatrixPage);
        }


        // ============================================================
        // TransformMatrix lookup (by RegionSet.Name)
        // ============================================================
        // NEW: full transform fetch (RotY, RotZ, Tx, Ty, Tz) + matrixName
        public bool TryGetTransformForRegion(
            string regionName,
            out double rotYDeg,
            out double rotZDeg,
            out double tx,
            out double ty,
            out double tz,
            out string matrixName)
        {
            rotYDeg = 0.0;
            rotZDeg = 0.0;
            tx = 0.0;
            ty = 0.0;
            tz = 0.0;
            matrixName = "No Transformation";

            if (string.IsNullOrWhiteSpace(regionName))
                return false;

            // Default fallback = card 0 name (if present)
            try
            {
                if (_transMatrixPage != null && _transMatrixPage.Matrices != null && _transMatrixPage.Matrices.Count > 0)
                {
                    string n0 = (_transMatrixPage.Matrices[0].MatrixName ?? "").Trim();
                    if (n0.Length > 0)
                        matrixName = n0;
                }
            }
            catch { /* ignore */ }

            // Helper for parsing numeric strings safely
            static double ParseNum(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return 0.0;

                string t = s.Trim();
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return v;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                    return v;

                return 0.0;
            }

            // Find which matrix contains this region name (case-insensitive)
            try
            {
                if (_transMatrixPage == null || _transMatrixPage.Matrices == null)
                    return true; // keep defaults

                for (int i = 0; i < _transMatrixPage.Matrices.Count; i++)
                {
                    var m = _transMatrixPage.Matrices[i];
                    if (m == null) continue;

                    if (m.Regions == null || m.Regions.Count == 0)
                        continue;

                    bool has = m.Regions.Any(r =>
                        string.Equals((r ?? "").Trim(), regionName.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (!has)
                        continue;

                    string nm = (m.MatrixName ?? "").Trim();
                    if (nm.Length > 0)
                        matrixName = nm;

                    rotYDeg = ParseNum(m.RotY);
                    rotZDeg = ParseNum(m.RotZ);
                    tx = ParseNum(m.Tx);
                    ty = ParseNum(m.Ty);
                    tz = ParseNum(m.Tz);

                    return true;
                }
            }
            catch
            {
                // ignore – keep defaults
            }

            // Not assigned -> default matrix (still "true" with defaults)
            return true;
        }






        private enum SplitPlane
        {
            None,
            XY,
            XZ,
            YZ
        }

        private struct AxisHit
        {
            public char Axis;
            public double Value;

            public AxisHit(char axis, double value)
            {
                Axis = axis;
                Value = value;
            }
        }

        private static string StripGcodeComments(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            // remove ';' comments
            int semi = s.IndexOf(';');
            if (semi >= 0)
                s = s.Substring(0, semi);

            // remove (...) comments
            var sb = new System.Text.StringBuilder(s.Length);
            bool inParens = false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') { inParens = true; continue; }
                if (c == ')') { inParens = false; continue; }
                if (!inParens) sb.Append(c);
            }

            return sb.ToString();
        }

        private static List<AxisHit> ParseAxesInOrder(string line)
        {
            var hits = new List<AxisHit>();
            if (string.IsNullOrWhiteSpace(line))
                return hits;

            line = StripGcodeComments(line);

            int i = 0;
            while (i < line.Length)
            {
                char c = char.ToUpperInvariant(line[i]);

                if (c == 'X' || c == 'Y' || c == 'Z')
                {
                    int j = i + 1;

                    while (j < line.Length && char.IsWhiteSpace(line[j]))
                        j++;

                    int startNum = j;

                    if (j < line.Length && (line[j] == '+' || line[j] == '-'))
                        j++;

                    bool anyDigit = false;

                    while (j < line.Length && char.IsDigit(line[j]))
                    {
                        anyDigit = true;
                        j++;
                    }

                    if (j < line.Length && line[j] == '.')
                    {
                        j++;
                        while (j < line.Length && char.IsDigit(line[j]))
                        {
                            anyDigit = true;
                            j++;
                        }
                    }

                    // optional exponent
                    if (j < line.Length && (line[j] == 'e' || line[j] == 'E'))
                    {
                        int k = j + 1;
                        if (k < line.Length && (line[k] == '+' || line[k] == '-'))
                            k++;

                        bool expDigit = false;
                        while (k < line.Length && char.IsDigit(line[k]))
                        {
                            expDigit = true;
                            k++;
                        }

                        if (expDigit)
                            j = k;
                    }

                    if (anyDigit)
                    {
                        string num = line.Substring(startNum, j - startNum);
                        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                            hits.Add(new AxisHit(c, v));
                    }

                    i = j;
                    continue;
                }

                i++;
            }

            return hits;
        }

        private static int CountDistinctXYZ(List<AxisHit> hits)
        {
            bool hasX = false, hasY = false, hasZ = false;

            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].Axis == 'X') hasX = true;
                else if (hits[i].Axis == 'Y') hasY = true;
                else if (hits[i].Axis == 'Z') hasZ = true;
            }

            int n = 0;
            if (hasX) n++;
            if (hasY) n++;
            if (hasZ) n++;
            return n;
        }

        private static bool TryPickFirstTwoDistinctAxes(List<AxisHit> hits, out char a1, out char a2)
        {
            a1 = '\0';
            a2 = '\0';

            bool got1 = false;

            for (int i = 0; i < hits.Count; i++)
            {
                char a = hits[i].Axis;
                if (a != 'X' && a != 'Y' && a != 'Z')
                    continue;

                if (!got1)
                {
                    a1 = a;
                    got1 = true;
                }
                else
                {
                    if (a != a1)
                    {
                        a2 = a;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetLastValue(List<AxisHit> hits, char axis, out double val)
        {
            val = double.NaN;

            for (int i = hits.Count - 1; i >= 0; i--)
            {
                if (hits[i].Axis == axis)
                {
                    val = hits[i].Value;
                    return true;
                }
            }

            return false;
        }

        private static SplitPlane PlaneFromAxes(char a, char b)
        {
            if ((a == 'X' && b == 'Y') || (a == 'Y' && b == 'X')) return SplitPlane.XY;
            if ((a == 'X' && b == 'Z') || (a == 'Z' && b == 'X')) return SplitPlane.XZ;
            if ((a == 'Y' && b == 'Z') || (a == 'Z' && b == 'Y')) return SplitPlane.YZ;
            return SplitPlane.None;
        }

        private static void CanonicalAxes(SplitPlane p, out char a1, out char a2)
        {
            a1 = 'X';
            a2 = 'Y';

            if (p == SplitPlane.XY) { a1 = 'X'; a2 = 'Y'; return; }
            if (p == SplitPlane.XZ) { a1 = 'X'; a2 = 'Z'; return; }
            if (p == SplitPlane.YZ) { a1 = 'Y'; a2 = 'Z'; return; }
        }





        private void BtnSplitLine(object sender, RoutedEventArgs e)
        {
            if (TxtGcode == null)
                return;

            var sel = TxtGcode.Selection;
            if (sel == null || sel.IsEmpty)
                return;

            TextPointer selStart = sel.Start;
            TextPointer selEnd = sel.End;

            if (selStart == null || selEnd == null)
                return;

            TextPointer firstLineStart = selStart.GetLineStartPosition(0);
            if (firstLineStart == null)
                return;

            // Collect FULL lines touched by the selection (not clipped segments)
            var lineStarts = new List<TextPointer>();
            var nextLineStarts = new List<TextPointer>();
            var lineTexts = new List<string>();

            TextPointer cur = firstLineStart;
            while (cur != null)
            {
                TextPointer next = cur.GetLineStartPosition(1);
                if (next == null)
                    next = TxtGcode.Document.ContentEnd;

                string fullLine = new TextRange(cur, next).Text;
                fullLine = fullLine.Replace("\r", "").Replace("\n", "");

                lineStarts.Add(cur);
                nextLineStarts.Add(next);
                lineTexts.Add(fullLine);

                // stop once we've included the line that contains selEnd
                if (next.CompareTo(selEnd) >= 0)
                    break;

                if (next == TxtGcode.Document.ContentEnd)
                    break;

                cur = next;
            }

            // Need at least: P1 built from lines[0..n-2], P2 from last line lines[n-1]
            if (lineTexts.Count < 2)
                return;

            int lastIdx = lineTexts.Count - 1;

            // Mode lock:
            // 0=Unknown, 1=XY, 2=XZ, 3=YZ, 4=XYZ
            int mode = 0;

            double curX = double.NaN, curY = double.NaN, curZ = double.NaN;

            // Build P1 using ALL lines before the last line (modal)
            for (int i = 0; i < lastIdx; i++)
            {
                string rawLine = lineTexts[i];
                List<AxisHit> hits = ParseAxesInOrder(rawLine);

                double vxLine, vyLine, vzLine;
                bool hasX = TryGetLastValue(hits, 'X', out vxLine);
                bool hasY = TryGetLastValue(hits, 'Y', out vyLine);
                bool hasZ = TryGetLastValue(hits, 'Z', out vzLine);

                if (mode == 0)
                {
                    // 3D only if a SINGLE line provides XYZ together before 2D locks
                    if (hasX && hasY && hasZ)
                    {
                        curX = vxLine;
                        curY = vyLine;
                        curZ = vzLine;
                        mode = 4;
                        continue;
                    }

                    // otherwise update whatever we saw (modal)
                    if (hasX) curX = vxLine;
                    if (hasY) curY = vyLine;
                    if (hasZ) curZ = vzLine;

                    // lock to first 2-axis pair that becomes available
                    if (!double.IsNaN(curX) && !double.IsNaN(curY))
                    {
                        mode = 1; // XY
                        continue;
                    }
                    if (!double.IsNaN(curX) && !double.IsNaN(curZ))
                    {
                        mode = 2; // XZ
                        continue;
                    }
                    if (!double.IsNaN(curY) && !double.IsNaN(curZ))
                    {
                        mode = 3; // YZ
                        continue;
                    }
                }
                else if (mode == 1) // XY
                {
                    if (hasX) curX = vxLine;
                    if (hasY) curY = vyLine;
                }
                else if (mode == 2) // XZ
                {
                    if (hasX) curX = vxLine;
                    if (hasZ) curZ = vzLine;
                }
                else if (mode == 3) // YZ
                {
                    if (hasY) curY = vyLine;
                    if (hasZ) curZ = vzLine;
                }
                else // XYZ
                {
                    if (hasX) curX = vxLine;
                    if (hasY) curY = vyLine;
                    if (hasZ) curZ = vzLine;
                }
            }

            if (mode == 0)
                return;

            double p1x = curX, p1y = curY, p1z = curZ;

            // Validate P1 exists for the chosen mode
            if (mode == 1) { if (double.IsNaN(p1x) || double.IsNaN(p1y)) return; }       // XY
            if (mode == 2) { if (double.IsNaN(p1x) || double.IsNaN(p1z)) return; }       // XZ
            if (mode == 3) { if (double.IsNaN(p1y) || double.IsNaN(p1z)) return; }       // YZ
            if (mode == 4) { if (double.IsNaN(p1x) || double.IsNaN(p1y) || double.IsNaN(p1z)) return; } // XYZ

            // Build P2 from LAST line only, using modal values from P1 for missing axes
            List<AxisHit> lastHits = ParseAxesInOrder(lineTexts[lastIdx]);

            double lastX, lastY, lastZ;
            bool lastHasX = TryGetLastValue(lastHits, 'X', out lastX);
            bool lastHasY = TryGetLastValue(lastHits, 'Y', out lastY);
            bool lastHasZ = TryGetLastValue(lastHits, 'Z', out lastZ);

            double p2x = p1x, p2y = p1y, p2z = p1z;
            bool touched = false;

            if (mode == 1) // XY
            {
                if (lastHasX) { p2x = lastX; touched = true; }
                if (lastHasY) { p2y = lastY; touched = true; }
            }
            else if (mode == 2) // XZ
            {
                if (lastHasX) { p2x = lastX; touched = true; }
                if (lastHasZ) { p2z = lastZ; touched = true; }
            }
            else if (mode == 3) // YZ
            {
                if (lastHasY) { p2y = lastY; touched = true; }
                if (lastHasZ) { p2z = lastZ; touched = true; }
            }
            else // XYZ
            {
                if (lastHasX) { p2x = lastX; touched = true; }
                if (lastHasY) { p2y = lastY; touched = true; }
                if (lastHasZ) { p2z = lastZ; touched = true; }
            }

            // last line must actually define at least one axis for the locked mode
            if (!touched)
                return;

            // Midpoint output
            string midLine;

            if (mode == 1) // XY
            {
                double midX = (p1x + p2x) * 0.5;
                double midY = (p1y + p2y) * 0.5;

                midLine =
                    "G01 " +
                    "X" + midX.ToString("0.###", CultureInfo.InvariantCulture) + " " +
                    "Y" + midY.ToString("0.###", CultureInfo.InvariantCulture);
            }
            else if (mode == 2) // XZ
            {
                double midX = (p1x + p2x) * 0.5;
                double midZ = (p1z + p2z) * 0.5;

                midLine =
                    "G01 " +
                    "X" + midX.ToString("0.###", CultureInfo.InvariantCulture) + " " +
                    "Z" + midZ.ToString("0.###", CultureInfo.InvariantCulture);
            }
            else if (mode == 3) // YZ
            {
                double midY = (p1y + p2y) * 0.5;
                double midZ = (p1z + p2z) * 0.5;

                midLine =
                    "G01 " +
                    "Y" + midY.ToString("0.###", CultureInfo.InvariantCulture) + " " +
                    "Z" + midZ.ToString("0.###", CultureInfo.InvariantCulture);
            }
            else // XYZ
            {
                double midX = (p1x + p2x) * 0.5;
                double midY = (p1y + p2y) * 0.5;
                double midZ = (p1z + p2z) * 0.5;

                midLine =
                    "G01 " +
                    "X" + midX.ToString("0.###", CultureInfo.InvariantCulture) + " " +
                    "Y" + midY.ToString("0.###", CultureInfo.InvariantCulture) + " " +
                    "Z" + midZ.ToString("0.###", CultureInfo.InvariantCulture);
            }

            // Insert BEFORE the last line (so last line becomes the 2nd segment, modal from the inserted midpoint)
            TextPointer insertPtr = lineStarts[lastIdx].GetInsertionPosition(LogicalDirection.Forward);
            new TextRange(insertPtr, insertPtr).Text = midLine + Environment.NewLine;
        }









        private void BtnSplitArc(object sender, RoutedEventArgs e)
        {
            if (TxtGcode == null)
                return;

            var sel = TxtGcode.Selection;
            if (sel == null || sel.IsEmpty)
                return;

            TextPointer selStart = sel.Start;
            TextPointer selEnd = sel.End;

            if (selStart == null || selEnd == null)
                return;

            TextPointer firstLineStart = selStart.GetLineStartPosition(0);
            if (firstLineStart == null)
                return;

            // Collect FULL lines touched by the selection (not clipped segments)
            var lineStarts = new List<TextPointer>();
            var nextLineStarts = new List<TextPointer>();
            var lineTexts = new List<string>();

            TextPointer cur = firstLineStart;
            while (cur != null)
            {
                TextPointer next = cur.GetLineStartPosition(1);
                if (next == null)
                    next = TxtGcode.Document.ContentEnd;

                string fullLine = new TextRange(cur, next).Text;
                fullLine = fullLine.Replace("\r", "").Replace("\n", "");

                lineStarts.Add(cur);
                nextLineStarts.Add(next);
                lineTexts.Add(fullLine);

                if (next.CompareTo(selEnd) >= 0)
                    break;

                if (next == TxtGcode.Document.ContentEnd)
                    break;

                cur = next;
            }

            if (lineTexts.Count < 2)
                return;

            int lastIdx = lineTexts.Count - 1;

            // Plane lock from LEADING lines (modal):
            // 0=None, 1=XY, 2=XZ, 3=YZ
            int plane = 0;

            double curX = double.NaN, curY = double.NaN, curZ = double.NaN;

            // Build START point from all lines BEFORE the last (modal)
            for (int i = 0; i < lastIdx; i++)
            {
                List<AxisHit> hits = ParseAxesInOrder(lineTexts[i]);

                if (TryGetLastValue(hits, 'X', out double vx)) curX = vx;
                if (TryGetLastValue(hits, 'Y', out double vy)) curY = vy;
                if (TryGetLastValue(hits, 'Z', out double vz)) curZ = vz;

                if (plane == 0)
                {
                    // first pair that becomes available locks plane
                    if (!double.IsNaN(curX) && !double.IsNaN(curY)) { plane = 1; continue; }
                    if (!double.IsNaN(curX) && !double.IsNaN(curZ)) { plane = 2; continue; }
                    if (!double.IsNaN(curY) && !double.IsNaN(curZ)) { plane = 3; continue; }
                }
            }

            // Last line MUST be the arc to split
            string arcRaw = lineTexts[lastIdx];
            if (string.IsNullOrWhiteSpace(arcRaw))
                return;

            // Strip ';' comment and '(...)' comment for parsing
            string arcClean = arcRaw.Replace("\r", "").Replace("\n", "");
            int semi = arcClean.IndexOf(';');
            if (semi >= 0)
                arcClean = arcClean.Substring(0, semi);

            {
                var sb = new System.Text.StringBuilder(arcClean.Length);
                bool inPar = false;
                for (int i = 0; i < arcClean.Length; i++)
                {
                    char c = arcClean[i];
                    if (c == '(') { inPar = true; continue; }
                    if (c == ')') { inPar = false; continue; }
                    if (!inPar) sb.Append(c);
                }
                arcClean = sb.ToString();
            }

            string arcUpper = arcClean.ToUpperInvariant();

            // Detect direction (ARC_CW/ARC_CCW or G2/G3)
            bool isCCW = false;
            bool haveDir = false;

            if (arcUpper.Contains("ARC_CCW"))
            {
                isCCW = true;
                haveDir = true;
            }
            else if (arcUpper.Contains("ARC_CW"))
            {
                isCCW = false;
                haveDir = true;
            }

            // Robust token scan for concatenated tokens like "G3X..Y..I..J..F.."
            // We keep the LAST value for each letter.
            double xVal = double.NaN, yVal = double.NaN, zVal = double.NaN;
            double iVal = double.NaN, jVal = double.NaN, kVal = double.NaN;
            double rVal = double.NaN;

            bool hasX = false, hasY = false, hasZ = false;
            bool hasI = false, hasJ = false, hasK = false;
            bool hasR = false;

            var extraTokens = new List<string>();

            int p = 0;
            while (p < arcClean.Length)
            {
                // skip spaces/tabs
                while (p < arcClean.Length && (arcClean[p] == ' ' || arcClean[p] == '\t'))
                    p++;

                if (p >= arcClean.Length)
                    break;

                char c0 = arcClean[p];

                // Handle "ARC_CW"/"ARC_CCW" words (skip them)
                if (char.IsLetter(c0))
                {
                    // read a word if it contains underscore
                    int w0 = p;
                    int w = p;
                    while (w < arcClean.Length && (char.IsLetter(arcClean[w]) || arcClean[w] == '_'))
                        w++;

                    string maybeWord = arcClean.Substring(w0, w - w0);
                    string maybeWordU = maybeWord.ToUpperInvariant();
                    if (maybeWordU == "ARC_CW" || maybeWordU == "ARC_CCW")
                    {
                        p = w;
                        continue;
                    }
                }

                // Token form: Letter + number (or Letter + sign/decimal), potentially concatenated
                if (!char.IsLetter(c0))
                {
                    p++;
                    continue;
                }

                char letter = char.ToUpperInvariant(c0);
                int n0 = p + 1;

                // For G token, allow "G2", "G02", "G3", etc.
                // For others, read until next letter or space
                int n = n0;
                while (n < arcClean.Length)
                {
                    char ch = arcClean[n];
                    if (char.IsLetter(ch) || ch == ' ' || ch == '\t' || ch == ';' || ch == '(')
                        break;
                    n++;
                }

                string numText = arcClean.Substring(n0, n - n0).Trim();
                p = n;

                // Direction from G token if not already set
                if (letter == 'G' && !haveDir)
                {
                    if (int.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gcode))
                    {
                        if (gcode == 2) { isCCW = false; haveDir = true; }
                        else if (gcode == 3) { isCCW = true; haveDir = true; }
                    }
                }

                // Parse numeric if possible
                if (double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                {
                    if (letter == 'X') { xVal = dv; hasX = true; continue; }
                    if (letter == 'Y') { yVal = dv; hasY = true; continue; }
                    if (letter == 'Z') { zVal = dv; hasZ = true; continue; }
                    if (letter == 'I') { iVal = dv; hasI = true; continue; }
                    if (letter == 'J') { jVal = dv; hasJ = true; continue; }
                    if (letter == 'K') { kVal = dv; hasK = true; continue; }
                    if (letter == 'R') { rVal = dv; hasR = true; continue; }

                    // extras: keep token text as-is (uppercase letter + original number formatting is fine)
                    if (letter != 'G')
                    {
                        extraTokens.Add(letter + numText);
                    }
                }
                else
                {
                    // non-numeric token: keep as extra only if it's not G
                    if (letter != 'G')
                        extraTokens.Add(letter + numText);
                }
            }

            if (!haveDir)
                return;

            // If plane not locked from leading lines, infer from arc tokens (fallback)
            if (plane == 0)
            {
                // Prefer IJK pair first
                if (hasI && hasJ) plane = 1;
                else if (hasI && hasK) plane = 2;
                else if (hasJ && hasK) plane = 3;
                else
                {
                    // else from endpoint axes
                    if (hasX && hasY) plane = 1;
                    else if (hasX && hasZ) plane = 2;
                    else if (hasY && hasZ) plane = 3;
                }
            }

            if (plane == 0)
                return;

            // Start point (from leading lines, modal)
            double sx = curX, sy = curY, sz = curZ;

            // Validate required start axes exist for the chosen plane
            if (plane == 1) { if (double.IsNaN(sx) || double.IsNaN(sy)) return; } // XY
            if (plane == 2) { if (double.IsNaN(sx) || double.IsNaN(sz)) return; } // XZ
            if (plane == 3) { if (double.IsNaN(sy) || double.IsNaN(sz)) return; } // YZ

            // End point = last line tokens, modal for missing (missing axis => same as start)
            double ex = sx, ey = sy, ez = sz;

            if (hasX) ex = xVal;
            if (hasY) ey = yVal;
            if (hasZ) ez = zVal;

            // Work in 2D plane coordinates (u,v) mapped from XYZ
            double su = 0, sv = 0, eu = 0, ev = 0;

            if (plane == 1) { su = sx; sv = sy; eu = ex; ev = ey; }         // XY
            else if (plane == 2) { su = sx; sv = sz; eu = ex; ev = ez; }    // XZ
            else { su = sy; sv = sz; eu = ey; ev = ez; }                    // YZ

            // Center (cu,cv)
            double cu = double.NaN, cv = double.NaN;

            // Prefer R if present and no plane offsets present
            bool havePlaneOffsets = false;
            if (plane == 1) havePlaneOffsets = (hasI || hasJ);
            else if (plane == 2) havePlaneOffsets = (hasI || hasK);
            else havePlaneOffsets = (hasJ || hasK);

            if (hasR && !havePlaneOffsets)
            {
                double rSigned = rVal;
                double rAbs = Math.Abs(rSigned);

                double du = eu - su;
                double dv = ev - sv;
                double cLen = Math.Sqrt(du * du + dv * dv);
                if (cLen <= 1e-12)
                    return;

                if (cLen > 2.0 * rAbs + 1e-9)
                    return;

                double mu = (su + eu) * 0.5;
                double mv = (sv + ev) * 0.5;

                double half = cLen * 0.5;
                double h2 = rAbs * rAbs - half * half;
                if (h2 < -1e-9)
                    return;
                if (h2 < 0) h2 = 0;
                double h = Math.Sqrt(h2);

                // perp unit = (-dv, du) / cLen
                double pu = -dv / cLen;
                double pv = du / cLen;

                double c1u = mu + pu * h;
                double c1v = mv + pv * h;
                double c2u = mu - pu * h;
                double c2v = mv - pv * h;

                bool wantLong = (rSigned < 0);

                // test sweep for a candidate center
                bool CenterMatches(double tu, double tv)
                {
                    double a0 = Math.Atan2(sv - tv, su - tu);
                    double a1 = Math.Atan2(ev - tv, eu - tu);

                    double sw = a1 - a0;

                    if (isCCW)
                    {
                        while (sw < 0) sw += 2.0 * Math.PI;
                    }
                    else
                    {
                        while (sw > 0) sw -= 2.0 * Math.PI;
                        sw = -sw; // magnitude
                    }

                    if (!wantLong) return sw <= Math.PI + 1e-9;
                    return sw > Math.PI - 1e-9;
                }

                bool m1 = CenterMatches(c1u, c1v);
                bool m2 = CenterMatches(c2u, c2v);

                if (m1 && !m2) { cu = c1u; cv = c1v; }
                else if (!m1 && m2) { cu = c2u; cv = c2v; }
                else if (m1) { cu = c1u; cv = c1v; }
                else if (m2) { cu = c2u; cv = c2v; }
                else
                    return;
            }
            else
            {
                // IJK center offsets (missing offset => 0)
                double offU = 0.0, offV = 0.0;

                if (plane == 1)
                {
                    if (hasI) offU = iVal;
                    if (hasJ) offV = jVal;
                }
                else if (plane == 2)
                {
                    if (hasI) offU = iVal;
                    if (hasK) offV = kVal;
                }
                else
                {
                    if (hasJ) offU = jVal; // Y maps to U
                    if (hasK) offV = kVal; // Z maps to V
                }

                // Must have at least one offset or R; if all missing -> can't
                if (!hasR && !havePlaneOffsets)
                    return;

                cu = su + offU;
                cv = sv + offV;
            }

            if (double.IsNaN(cu) || double.IsNaN(cv))
                return;

            // Compute midpoint on the arc sweep
            double r0u = su - cu;
            double r0v = sv - cv;
            double r1u = eu - cu;
            double r1v = ev - cv;

            double rad0 = Math.Sqrt(r0u * r0u + r0v * r0v);
            double rad1 = Math.Sqrt(r1u * r1u + r1v * r1v);
            if (rad0 <= 1e-12 || rad1 <= 1e-12)
                return;

            double rad = (rad0 + rad1) * 0.5;

            double a0m = Math.Atan2(r0v, r0u);
            double a1m = Math.Atan2(r1v, r1u);

            double sweep = a1m - a0m;
            if (isCCW)
            {
                while (sweep < 0) sweep += 2.0 * Math.PI;
            }
            else
            {
                while (sweep > 0) sweep -= 2.0 * Math.PI;
            }

            double amid = a0m + sweep * 0.5;

            double mu2 = cu + rad * Math.Cos(amid);
            double mv2 = cv + rad * Math.Sin(amid);

            // Map midpoint back to XYZ (only the plane axes)
            double mx = sx, my = sy, mz = sz;

            if (plane == 1) { mx = mu2; my = mv2; }       // XY
            else if (plane == 2) { mx = mu2; mz = mv2; }  // XZ
            else { my = mu2; mz = mv2; }                  // YZ

            // Output as two arcs using IJK offsets (relative to start and relative to mid)
            string gOut = isCCW ? "G03" : "G02";

            string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

            // Offsets start->center in plane space
            double o1u = cu - su;
            double o1v = cv - sv;

            // Offsets mid->center in plane space
            double o2u = cu - mu2;
            double o2v = cv - mv2;

            // Build extras (F,S,etc) only on first arc line
            string extraStr = "";
            if (extraTokens.Count > 0)
                extraStr = " " + string.Join(" ", extraTokens);

            string arc1 = "";
            string arc2 = "";

            if (plane == 1)
            {
                arc1 = gOut + " X" + Fmt(mx) + " Y" + Fmt(my) + " I" + Fmt(o1u) + " J" + Fmt(o1v) + extraStr;
                arc2 = gOut + " X" + Fmt(ex) + " Y" + Fmt(ey) + " I" + Fmt(o2u) + " J" + Fmt(o2v);
            }
            else if (plane == 2)
            {
                arc1 = gOut + " X" + Fmt(mx) + " Z" + Fmt(mz) + " I" + Fmt(o1u) + " K" + Fmt(o1v) + extraStr;
                arc2 = gOut + " X" + Fmt(ex) + " Z" + Fmt(ez) + " I" + Fmt(o2u) + " K" + Fmt(o2v);
            }
            else
            {
                arc1 = gOut + " Y" + Fmt(my) + " Z" + Fmt(mz) + " J" + Fmt(o1u) + " K" + Fmt(o1v) + extraStr;
                arc2 = gOut + " Y" + Fmt(ey) + " Z" + Fmt(ez) + " J" + Fmt(o2u) + " K" + Fmt(o2v);
            }

            // Replace the LAST line only (arc) with the two arcs
            TextPointer arcLs = lineStarts[lastIdx];
            TextPointer arcNls = nextLineStarts[lastIdx];

            new TextRange(arcLs, arcNls).Text = arc1 + Environment.NewLine + arc2 + Environment.NewLine;
        }


        private void BtnXadd(object sender, RoutedEventArgs e)
        {
            AddAxisToSelection_AllTokens('X', "X Add");
        }

        private void BtnYadd(object sender, RoutedEventArgs e)
        {
            AddAxisToSelection_AllTokens('Y', "Y Add");
        }

        private void BtnZadd(object sender, RoutedEventArgs e)
        {
            AddAxisToSelection_AllTokens('Z', "Z Add");
        }

        private void AddAxisToSelection_AllTokens(char axis, string title)
        {
            if (TxtGcode == null)
                return;

            var sel = TxtGcode.Selection;
            if (sel == null || sel.IsEmpty)
                return;

            TextPointer selStart = sel.Start;
            TextPointer selEnd = sel.End;

            if (selStart == null || selEnd == null)
                return;

            // Prompt ONCE
            string sDelta = Microsoft.VisualBasic.Interaction.InputBox("Add value to " + axis + ":", title, "0");
            if (string.IsNullOrWhiteSpace(sDelta))
                return;

            // Parse delta (Invariant first, then Current)
            if (!double.TryParse(sDelta, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
            {
                if (!double.TryParse(sDelta, NumberStyles.Float, CultureInfo.CurrentCulture, out delta))
                    return;
            }

            // Expand selection to FULL LINES (first touched line -> last touched line)
            TextPointer firstLineStart = selStart.GetLineStartPosition(0);
            if (firstLineStart == null)
                return;

            // If selection ends exactly at a line start (common when you drag-select whole lines),
            // treat that as "end belongs to previous line", otherwise you only get the first line.
            TextPointer endPtrForLine = selEnd;

            if (selEnd.CompareTo(selStart) > 0)
            {
                TextPointer endLineStart0 = selEnd.GetLineStartPosition(0);
                if (endLineStart0 != null && selEnd.CompareTo(endLineStart0) == 0)
                {
                    // move to previous line start so the "last touched line" is included
                    TextPointer prevLineStart = selEnd.GetLineStartPosition(-1);
                    if (prevLineStart != null)
                        endPtrForLine = prevLineStart;
                }
            }

            TextPointer lastLineStart = endPtrForLine.GetLineStartPosition(0);
            if (lastLineStart == null)
                return;

            TextPointer afterLastLine = lastLineStart.GetLineStartPosition(1);
            if (afterLastLine == null)
                afterLastLine = TxtGcode.Document.ContentEnd;

            // Grab the whole block text once, rewrite once (avoids pointer shifting + "deleting" the rest)
            var range = new TextRange(firstLineStart, afterLastLine);
            string rawBlock = range.Text ?? "";

            // Normalize to '\n' for processing
            rawBlock = rawBlock.Replace("\r", "");

            bool hadTrailingNewline = rawBlock.EndsWith("\n", StringComparison.Ordinal);

            var lines = rawBlock.Split('\n'); // keeps last empty element if trailing newline existed

            bool anyChanged = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string original = lines[i];

                // If this is the extra "" after a trailing newline, keep it
                // (don’t try to edit it)
                if (i == lines.Length - 1 && hadTrailingNewline && original.Length == 0)
                    continue;

                if (string.IsNullOrEmpty(original))
                    continue;

                if (TryAddToAllAxisTokensInLine(original, axis, delta, out string updated))
                {
                    lines[i] = updated;
                    anyChanged = true;
                }
            }

            if (!anyChanged)
                return;

            string newBlock = string.Join(Environment.NewLine, lines);

            // Preserve a trailing newline if the original range had one
            if (hadTrailingNewline && !newBlock.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                newBlock += Environment.NewLine;

            range.Text = newBlock;
        }


        // Adds delta to EVERY occurrence of axis token in the line.
        // Handles spaced and concatenated forms: "X1.0", "G3X1.0Y2.0", "X1 X2" etc.
        // Preserves:
        //   - text after ';' as-is
        //   - text inside (...) as-is
        private bool TryAddToAllAxisTokensInLine(string fullLine, char axis, double delta, out string updatedLine)
        {
            updatedLine = fullLine;

            if (string.IsNullOrEmpty(fullLine))
                return false;

            // Split ';' comment (keep it untouched)
            string code = fullLine;
            string comment = "";
            int semi = code.IndexOf(';');
            if (semi >= 0)
            {
                comment = code.Substring(semi);     // includes ';'
                code = code.Substring(0, semi);
            }

            char AX = char.ToUpperInvariant(axis);

            var sb = new System.Text.StringBuilder(code.Length + 16);
            bool changed = false;

            bool inParen = false;

            int i = 0;
            while (i < code.Length)
            {
                char c = code[i];

                // Preserve (...) blocks untouched
                if (c == '(')
                {
                    inParen = true;
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    inParen = false;
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (inParen)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                // Look for axis letter
                if (char.IsLetter(c) && char.ToUpperInvariant(c) == AX)
                {
                    int letterPos = i;
                    int n0 = i + 1;

                    // Read number until next letter/space/tab/';'/'('
                    int n = n0;
                    while (n < code.Length)
                    {
                        char ch = code[n];
                        if (char.IsLetter(ch) || ch == ' ' || ch == '\t' || ch == ';' || ch == '(' || ch == ')')
                            break;
                        n++;
                    }

                    string numText = code.Substring(n0, n - n0).Trim();

                    if (numText.Length > 0 &&
                        double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    {
                        double nv = v + delta;
                        string newNum = nv.ToString("0.###", CultureInfo.InvariantCulture);

                        // Keep original axis letter casing
                        sb.Append(code[letterPos]);
                        sb.Append(newNum);

                        changed = true;
                        i = n;
                        continue;
                    }
                    else
                    {
                        // Not a numeric axis token, just copy char and continue
                        sb.Append(c);
                        i++;
                        continue;
                    }
                }

                // default copy
                sb.Append(c);
                i++;
            }

            string newCode = sb.ToString().TrimEnd();

            if (!string.IsNullOrEmpty(comment))
            {
                // keep at least one space before comment if code has content
                if (newCode.Length > 0)
                    updatedLine = newCode + " " + comment;
                else
                    updatedLine = comment;
            }
            else
            {
                updatedLine = newCode;
            }

            return changed;
        }

        private async void JumpEditorToRegionStart(string statusText, int preLines = 5)
        {
            if (!AllowScrollToRegionStart)
                return;

            if (TxtGcode == null || TxtGcode.Document == null)
                return;

            if (string.IsNullOrWhiteSpace(statusText))
                return;

            string s = statusText.Trim();

            int ok = s.IndexOf("OK", StringComparison.OrdinalIgnoreCase);
            if (ok < 0)
                return;

            int i = ok + 2;
            while (i < s.Length && !char.IsDigit(s[i]))
                i++;

            if (i >= s.Length)
                return;

            int j = i;
            while (j < s.Length && char.IsDigit(s[j]))
                j++;

            if (j <= i)
                return;

            if (!int.TryParse(s.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int startLine1Based))
                return;

            if (startLine1Based <= 0)
                return;

            // Only consume the one-shot gate once we have a valid target
            AllowScrollToRegionStart = false;

            try
            {
                int targetLine = startLine1Based - Math.Max(0, preLines);
                if (targetLine < 1) targetLine = 1;

                if (UiUtilities.TryGetNumberedLineStartPointer(TxtGcode, targetLine, out TextPointer? ptr) && ptr != null)
                {
                    TxtGcode.Focus();
                    TxtGcode.CaretPosition = ptr;

                    // WPF-supported: use TextRange to bring the caret/selection into view
                    TextRange tr = new TextRange(ptr, ptr);
                    tr.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent); // no-op but forces range materialize
                    TxtGcode.Selection.Select(ptr, ptr);

                    // Scroll so caret is visible
                    Rect r = ptr.GetCharacterRect(LogicalDirection.Forward);
                    TxtGcode.ScrollToVerticalOffset(Math.Max(0, TxtGcode.VerticalOffset + r.Top - 40));

                    return;
                }

                TxtGcode.ScrollToHome();
            }
            catch
            {
                // Re-arm so user doesn't have to click twice if something failed
                AllowScrollToRegionStart = true;
            }
        }





        private void BtnRegionJoin(object sender, RoutedEventArgs e)
        {
            SyncGcodeLinesFromEditor();
            MainFrame.Navigate(new JoinRegionsPage());
        }
        private enum ArcFixResult
        {
            NoChange,
            Fixed,
            AmbiguousPlane
        }

        private static int? TryParseLeadingLineNumberFromEditorLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            string t = line.TrimStart();

            int p = 0;
            while (p < t.Length && char.IsDigit(t[p]))
                p++;

            if (p == 0)
                return null;

            if (p >= t.Length || t[p] != ':')
                return null;

            if (int.TryParse(t.Substring(0, p), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return n;

            return null;
        }






        //============arc fix stats here


        private void ScrollLineStartToTop(TextPointer lineStart)
        {

            if (TxtGcode == null || lineStart == null)
                return;

            TxtGcode.Focus();

            // Put caret at the problem line
            TxtGcode.Selection.Select(lineStart, lineStart);
            TxtGcode.CaretPosition = lineStart;

            // Move that line to the TOP of the visible area
            Rect r = lineStart.GetCharacterRect(LogicalDirection.Forward);
            double newOff = TxtGcode.VerticalOffset + r.Top;
            if (newOff < 0) newOff = 0;
            TxtGcode.ScrollToVerticalOffset(newOff);
        }






        private static ArcFixResult TryAddMissingArcPlaneOffsetsZero(string fullLine, out string updatedLine)
        {
            updatedLine = fullLine ?? "";
            if (string.IsNullOrWhiteSpace(fullLine))
                return ArcFixResult.NoChange;

            // Preserve ';' comment
            SplitSemiComment(fullLine, out string codePart, out string semiComment);

            // Preserve trailing "(...)" (your unique tags, etc.)
            string parenSuffix = "";
            string core = codePart;
            _ = TrySplitTrailingParenComment(codePart, out core, out parenSuffix);

            // Detect arc + tokens from *core only*
            ScanArcTokens(
                core,
                out bool isArc,
                out bool hasR,
                out bool hasX, out bool hasY, out bool hasZ,
                out bool hasI, out bool hasJ, out bool hasK);

            if (!isArc)
                return ArcFixResult.NoChange;

            // If arc uses R, do NOT touch it
            if (hasR)
                return ArcFixResult.NoChange;

            // Decide plane STRICTLY:
            // Must have either:
            //   - I+J  (XY), I+K (XZ), J+K (YZ)
            //   - OR endpoint axis pair: X+Y (XY), X+Z (XZ), Y+Z (YZ)
            int plane = 0; // 1=XY, 2=XZ, 3=YZ

            if (hasI && hasJ) plane = 1;
            else if (hasI && hasK) plane = 2;
            else if (hasJ && hasK) plane = 3;

            if (plane == 0)
            {
                if (hasX && hasY) plane = 1;
                else if (hasX && hasZ) plane = 2;
                else if (hasY && hasZ) plane = 3;
            }

            // Still unknown -> AMBIGUOUS (example: G3 X... I... only)
            if (plane == 0)
                return ArcFixResult.AmbiguousPlane;

            bool needI = false, needJ = false, needK = false;

            if (plane == 1) { needI = true; needJ = true; }      // XY
            else if (plane == 2) { needI = true; needK = true; } // XZ
            else { needJ = true; needK = true; }                 // YZ

            bool changed = false;

            var sb = new StringBuilder(core.TrimEnd());

            // Append missing required offsets with explicit 0
            if (needI && !hasI) { sb.Append(sb.Length > 0 ? " I0" : "I0"); changed = true; }
            if (needJ && !hasJ) { sb.Append(sb.Length > 0 ? " J0" : "J0"); changed = true; }
            if (needK && !hasK) { sb.Append(sb.Length > 0 ? " K0" : "K0"); changed = true; }

            if (!changed)
                return ArcFixResult.NoChange;

            string newCore = sb.ToString();

            // Rebuild full line
            var rebuilt = new StringBuilder(newCore.Length + (parenSuffix?.Length ?? 0) + (semiComment?.Length ?? 0) + 4);

            rebuilt.Append(newCore);

            if (!string.IsNullOrWhiteSpace(parenSuffix))
            {
                rebuilt.Append(' ');
                rebuilt.Append(parenSuffix.TrimEnd());
            }

            if (!string.IsNullOrEmpty(semiComment))
            {
                if (rebuilt.Length > 0 && !char.IsWhiteSpace(rebuilt[rebuilt.Length - 1]))
                    rebuilt.Append(' ');
                rebuilt.Append(semiComment);
            }

            updatedLine = rebuilt.ToString().TrimEnd('\r', '\n');
            return ArcFixResult.Fixed;
        }
        private static void SplitSemiComment(string line, out string codePart, out string semiComment)
        {
            codePart = line ?? "";
            semiComment = "";

            int semi = codePart.IndexOf(';');
            if (semi >= 0)
            {
                semiComment = codePart.Substring(semi);   // includes ';'
                codePart = codePart.Substring(0, semi);
            }
        }

        private static bool TrySplitTrailingParenComment(string codePart, out string core, out string parenSuffix)
        {
            core = codePart ?? "";
            parenSuffix = "";

            if (string.IsNullOrEmpty(core))
                return false;

            // last non-space must be ')'
            int end = core.Length - 1;
            while (end >= 0 && char.IsWhiteSpace(core[end]))
                end--;

            if (end < 0 || core[end] != ')')
                return false;

            // find the '(' that starts this trailing (...) block
            int open = core.LastIndexOf('(', end);
            if (open < 0)
                return false;

            // split
            parenSuffix = core.Substring(open).TrimEnd();
            core = core.Substring(0, open).TrimEnd();

            return parenSuffix.Length > 0;
        }

        private static void ScanArcTokens(
            string codePart,
            out bool isArc,
            out bool hasR,
            out bool hasX, out bool hasY, out bool hasZ,
            out bool hasI, out bool hasJ, out bool hasK)
        {
            isArc = false;

            hasR = false;
            hasX = hasY = hasZ = false;
            hasI = hasJ = hasK = false;

            if (string.IsNullOrWhiteSpace(codePart))
                return;

            // Word-based detection first
            string up = codePart.ToUpperInvariant();
            if (up.Contains("ARC_CW") || up.Contains("ARC_CCW"))
                isArc = true;

            bool inParen = false;

            int i = 0;
            while (i < codePart.Length)
            {
                char c = codePart[i];

                if (c == '(') { inParen = true; i++; continue; }
                if (c == ')') { inParen = false; i++; continue; }
                if (inParen) { i++; continue; }

                if (!char.IsLetter(c))
                {
                    i++;
                    continue;
                }

                char letter = char.ToUpperInvariant(c);
                int n0 = i + 1;

                // read token value until next letter/space/tab/paren/semicolon
                int n = n0;
                while (n < codePart.Length)
                {
                    char ch = codePart[n];
                    if (char.IsLetter(ch) || ch == ' ' || ch == '\t' || ch == '(' || ch == ')' || ch == ';')
                        break;
                    n++;
                }

                string numText = codePart.Substring(n0, n - n0).Trim();
                i = n;

                // Arc direction by G2/G3
                if (letter == 'G')
                {
                    if (int.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int g))
                    {
                        if (g == 2 || g == 3)
                            isArc = true;
                    }
                    continue;
                }

                if (letter == 'X') { hasX = true; continue; }
                if (letter == 'Y') { hasY = true; continue; }
                if (letter == 'Z') { hasZ = true; continue; }

                if (letter == 'I') { hasI = true; continue; }
                if (letter == 'J') { hasJ = true; continue; }
                if (letter == 'K') { hasK = true; continue; }

                if (letter == 'R') { hasR = true; continue; }
            }
        }

        private void BtnArcFields(object sender, RoutedEventArgs e)
        {
            if (TxtGcode == null)
                return;

            // If there is a selection: operate only on selected lines.
            // If no selection: operate on ALL lines in the editor.
            TextPointer startPtr;
            TextPointer endPtr;

            var sel = TxtGcode.Selection;
            if (sel != null && !sel.IsEmpty)
            {
                startPtr = sel.Start.GetLineStartPosition(0) ?? sel.Start;
                endPtr = sel.End;
            }
            else
            {
                startPtr = TxtGcode.Document.ContentStart;
                endPtr = TxtGcode.Document.ContentEnd;
            }

            // Collect FULL lines in range
            var lineStarts = new List<TextPointer>();
            var nextLineStarts = new List<TextPointer>();
            var lineTexts = new List<string>();

            TextPointer cur = startPtr;
            while (cur != null)
            {
                TextPointer ls = cur.GetLineStartPosition(0) ?? cur;
                TextPointer next = ls.GetLineStartPosition(1);
                if (next == null)
                    next = TxtGcode.Document.ContentEnd;

                string fullLine = new TextRange(ls, next).Text;
                fullLine = fullLine.Replace("\r", "").Replace("\n", "");

                lineStarts.Add(ls);
                nextLineStarts.Add(next);
                lineTexts.Add(fullLine);

                if (next.CompareTo(endPtr) >= 0)
                    break;

                if (next == TxtGcode.Document.ContentEnd)
                    break;

                cur = next;
            }

            if (lineTexts.Count == 0)
                return;

            int changedCount = 0;

            for (int i = 0; i < lineTexts.Count; i++)
            {
                string original = lineTexts[i] ?? "";

                ArcFixResult res = TryAddMissingArcPlaneOffsetsZero(original, out string updated);

                if (res == ArcFixResult.AmbiguousPlane)
                {
                    int? ln = TryParseLeadingLineNumberFromEditorLine(original);

                    // Leave invalid line at top and stop
                    ScrollLineStartToTop(lineStarts[i]);

                    MessageBox.Show(
                        $"Cant fix arc at line \"{(ln.HasValue ? ln.Value.ToString(CultureInfo.InvariantCulture) : "?")}\"",
                        "Arc Fields",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return; // ABORT
                }

                if (res == ArcFixResult.Fixed)
                {
                    if (!string.Equals(original, updated, StringComparison.Ordinal))
                    {
                        new TextRange(lineStarts[i], nextLineStarts[i]).Text = updated + Environment.NewLine;
                        changedCount++;
                    }
                }
            }

            if (changedCount > 0)
            {
                SyncGcodeLinesFromEditor();
                ColorizeUniqueTagsInEditor();
            }

            // Completed message ONLY for full-file iteration (no selection)
            if (sel == null || sel.IsEmpty)
            {
                MessageBox.Show(
                    $"Completed with \"{changedCount}\" arcs missing fields added.",
                    "Arc Fields",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }


        private void BtnCodeClean_Click(object sender, RoutedEventArgs e)
{
    try
    {
        // One-shot: TURN + MILL + DRILL
        bool any =
            (TurnSets != null && TurnSets.Count > 0) ||
            (MillSets != null && MillSets.Count > 0) ||
            (DrillSets != null && DrillSets.Count > 0);

        if (!any)
        {
            MessageBox.Show("No TURN/MILL/DRILL sets exist.", "Code Cleanup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // ============================================================
        // BEFORE DUMP (read-only): show snapshot keys + RegionLines EXACT
        // ============================================================
        var sb = new System.Text.StringBuilder(32768);

        sb.AppendLine("=== CODE CLEANUP - TURN + MILL + DRILL ===");
        sb.AppendLine("BEFORE (exact SetManager contents: PageSnapshot.Values + RegionLines)");
        sb.AppendLine("");

        void DumpSets(string title, System.Collections.Generic.IList<CNC_Improvements_gcode_solids.SetManagement.RegionSet> sets)
        {
            sb.AppendLine($"--- {title} ---");

            if (sets == null || sets.Count == 0)
            {
                sb.AppendLine($"No {title} sets.");
                sb.AppendLine();
                return;
            }

            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                if (set == null) continue;

                string name = (set.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = $"{title}_SET_{i + 1}";

                sb.AppendLine($"----- {title} SET {i + 1}/{sets.Count}: {name} -----");

                sb.AppendLine("SNAPSHOT KEYS (PageSnapshot.Values):");
                if (set.PageSnapshot?.Values != null && set.PageSnapshot.Values.Count > 0)
                {
                    foreach (var kv in set.PageSnapshot.Values.OrderBy(k => k.Key, StringComparer.Ordinal))
                        sb.AppendLine($"  {kv.Key} = {kv.Value}");
                }
                else
                {
                    sb.AppendLine("  (none)");
                }

                sb.AppendLine("");
                sb.AppendLine("RegionLines (as stored):");
                sb.AppendLine("------------------------------------------");

                if (set.RegionLines != null && set.RegionLines.Count > 0)
                {
                    for (int k = 0; k < set.RegionLines.Count; k++)
                        sb.AppendLine(set.RegionLines[k] ?? "");
                }
                else
                {
                    sb.AppendLine("(none)");
                }

                sb.AppendLine("------------------------------------------");
                sb.AppendLine($"----- END {title} SET: {name} -----");
                sb.AppendLine("");
            }

            sb.AppendLine();
        }

        DumpSets("TURN", TurnSets);
        DumpSets("MILL", MillSets);
        DumpSets("DRILL", DrillSets);

        // ============================================================
        // APPLY CLEANUP (in-place) + get NEW editor text
        // ============================================================
        sb.AppendLine("============================================================");
        sb.AppendLine("APPLY CLEANUP (BuildAndApplyAllCleanup updates sets in-place)");
        sb.AppendLine("============================================================");
        sb.AppendLine("");

        string newEditorText;
        string cleanupReport = CNC_Improvements_gcode_solids.Utilities.CodeCleanup.BuildAndApplyAllCleanup(
            TurnSets,
            MillSets,
            DrillSets,
            out newEditorText);

        sb.AppendLine(cleanupReport);

        // ============================================================
        // REPLACE EDITOR TEXT + REBUILD GcodeLines MODEL
        // ============================================================
        if (GcodeEditor != null)
        {
            // Replace the RichTextBox content with plain text blocks produced by cleanup
            GcodeEditor.Document.Blocks.Clear();
            GcodeEditor.Document.Blocks.Add(new Paragraph(new Run(newEditorText)) { Margin = new Thickness(0) });
        }

        // Rebuild the backing model list used everywhere else
        if (GcodeLines != null)
        {
            GcodeLines.Clear();

            string allText = (newEditorText ?? "").Replace("\r\n", "\n");
            using (var reader = new System.IO.StringReader(allText))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // keep line as-is (no "1234:" prefix here)
                    GcodeLines.Add(line);
                }
            }
        }

        // Show it
        var logWindow = new CNC_Improvements_gcode_solids.Utilities.LogWindow("CODE CLEANUP - TURN/MILL/DRILL", sb.ToString());
        logWindow.Owner = this;
        logWindow.Show();
    }
    catch (Exception ex)
    {
        MessageBox.Show(ex.Message, "Code Cleanup", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}








        




        private void BtnFapt(object sender, RoutedEventArgs e)
        {
            if (TxtGcode == null || TxtGcode.Document == null)
            {
                MessageBox.Show("No editor document.", "FAPT", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string allText = new TextRange(TxtGcode.Document.ContentStart, TxtGcode.Document.ContentEnd).Text ?? "";

            // Use Turn splitter for now (same as before)
            var lines = CNC_Improvements_gcode_solids.Utilities.FaptTurn.TextToLines_All(allText);

            // TURN regions (existing): (G112...) blocks
            var turnRegions = CNC_Improvements_gcode_solids.Utilities.FaptTurn.BuildFaptRegions(lines);

            // MILL regions (new): (G1062...) .. (G1206)
            var millRegions = CNC_Improvements_gcode_solids.Utilities.FaptMill.BuildFaptMillRegions(lines);

            // Combine for selection dialog
            var regions = new List<List<string>>();
            var regionType = new List<string>(); // "TURN" or "MILL"

            foreach (var r in turnRegions)
            {
                regions.Add(r);
                regionType.Add("TURN");
            }
            foreach (var r in millRegions)
            {
                regions.Add(r);
                regionType.Add("MILL");
            }

            if (regions.Count == 0)
            {
                MessageBox.Show("No FAPT regions found (no '(G112' TURN or '(G1062' MILL regions).", "FAPT",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // MULTI SELECT (your existing dialog)
            var sel = CNC_Improvements_gcode_solids.Utilities.FaptTurn.PickRegionIndices(this, regions);
            if (sel == null || sel.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("=== FAPT SELECTED REGIONS ===");
            sb.AppendLine("");

            // Append to editor end
            var blocksToAppend = new List<string>();

            for (int s = 0; s < sel.Count; s++)
            {
                int idx = sel[s];
                if (idx < 0 || idx >= regions.Count) continue;

                string type = (idx >= 0 && idx < regionType.Count) ? regionType[idx] : "TURN";

                string firstLine = (regions[idx].Count > 0) ? regions[idx][0] : "";
                string name = CNC_Improvements_gcode_solids.Utilities.FaptTurn.ExtractFaptRegionName(firstLine);
                if (string.IsNullOrWhiteSpace(name))
                    name = (type == "MILL") ? "FAPT_MILL" : "FAPT_TURN";

                // alpha tag: region 1 => a, region 2 => b, ...
                char alpha = CNC_Improvements_gcode_solids.Utilities.FaptTurn.IndexToAlpha(s);

                sb.AppendLine($"--- REGION {idx + 1} ({type}) ---");
                sb.AppendLine(firstLine);
                sb.AppendLine("");

                // Raw FAPT region text
                sb.AppendLine(string.Join("\n", regions[idx]));
                sb.AppendLine("");

                if (type == "TURN")
                {
                    // TURN translator (existing)
                    var rawGcode = CNC_Improvements_gcode_solids.Utilities.FaptTurn.TranslateFaptRegionToTurnGcode(regions[idx]);

                    // TURN formatting + tags + wrappers
                    var formatted = CNC_Improvements_gcode_solids.Utilities.FaptTurn.FormatTurnGcodeBlock(name, rawGcode, alpha);

                    sb.AppendLine("----- GENERATED TURN GCODE (formatted) -----");
                    if (formatted == null || formatted.Count == 0)
                    {
                        sb.AppendLine("(no gcode generated)");
                    }
                    else
                    {
                        foreach (var gl in formatted)
                            sb.AppendLine(gl);

                        blocksToAppend.AddRange(formatted);
                        blocksToAppend.Add("");

                        // Create TURN RegionSet (strip wrappers first/last)
                        var regionLinesOnly = CNC_Improvements_gcode_solids.Utilities.AutoAddTurnRegion.StripWrapper_FirstLast(formatted);

                        CNC_Improvements_gcode_solids.Utilities.AutoAddTurnRegion.AddTurnRegionSet_FromRegionGcodeOnly(
                            this,
                            name,
                            regionLinesOnly,
                            "__ToolUsage",
                            "__Quadrant",
                            "__StartXLine",
                            "__StartZLine",
                            "__EndXLine",
                            "__EndZLine",
                            "TxtZExt",
                            "NRad",
                            defaultToolUsage: "OFF",
                            defaultQuadrant: "3",
                            defaultZExt: "-100",
                            showInViewAll: true,
                            exportEnabled: false
                        );
                    }
                }
                else
                {
                    // MILL translator (placeholder: returns the fixed sample toolpath)
                    var rawGcode = CNC_Improvements_gcode_solids.Utilities.FaptMill.TranslateFaptRegionToMillGcode(regions[idx]);

                    // Format like TURN: (NAME ST)/(NAME END) + (m:a0000) tags @ col ~75
                    var formatted = CNC_Improvements_gcode_solids.Utilities.FaptMill.FormatMillGcodeBlock(name, rawGcode, alpha);

                    sb.AppendLine("----- GENERATED MILL GCODE (formatted) -----");
                    if (formatted == null || formatted.Count == 0)
                    {
                        sb.AppendLine("(no gcode generated)");
                    }
                    else
                    {
                        foreach (var gl in formatted)
                            sb.AppendLine(gl);

                        // append formatted block to editor
                        blocksToAppend.AddRange(formatted);
                        blocksToAppend.Add("");

                        // Build MILL RegionSet from INNER lines ONLY (strip wrappers first/last)
                        var regionLinesOnly = CNC_Improvements_gcode_solids.Utilities.AutoAddMillRegion.StripWrapper_FirstLast(formatted);



                        CNC_Improvements_gcode_solids.Utilities.AutoAddMillRegion.AddMillRegionSet_FromRegionGcodeOnly(
                            main: this,
                            regionName: name,
                            regionGcodeLinesOnly: regionLinesOnly,

                            KEY_CoordMode: "CoordMode",
                            KEY_ToolUsage: "__ToolUsage",

                            KEY_PlaneZLineText: "PlaneZLineText",
                            KEY_StartXLineText: "StartXLineText",
                            KEY_StartYLineText: "StartYLineText",
                            KEY_EndXLineText: "EndXLineText",
                            KEY_EndYLineText: "EndYLineText",

                            KEY_TxtToolDia: "TxtToolDia",
                            KEY_TxtToolLen: "TxtToolLen",
                            KEY_FuseAll: "Fuseall",
                            KEY_RemoveSplitter: "RemoveSplitter",
                            KEY_Clipper: "Clipper",
                            KEY_ClipperIsland: "ClipperIsland",
                            KEY_RegionUid: "__RegionUid",

                            defaultCoordMode: "Cartesian",
                            defaultToolUsage: "OFF",
                            defaultToolDia: "12",
                            defaultToolLen: "75",
                            defaultFuseAll: "0",
                            defaultRemoveSplitter: "0",
                            defaultClipper: "0",
                            defaultClipperIsland: "0",

                            showInViewAll: true,
                            exportEnabled: false
                        );













                        


                    }
                }


                sb.AppendLine("");
            }

            // Log window
            CNC_Improvements_gcode_solids.Utilities.FaptTurn.ShowTextWindow(this, "FAPT Regions", sb.ToString());

            // Append to END of the RichTextBox
            if (blocksToAppend.Count > 0)
            {
                string appendText = string.Join(Environment.NewLine, blocksToAppend) + Environment.NewLine;
                TxtGcode.Document.Blocks.Add(new Paragraph(new Run(appendText)));
                TxtGcode.ScrollToEnd();
            }
        }


        private void Btntesting(object sender, RoutedEventArgs e)
        {

            //Build3TestRegions_Static();


            try
            {
                var sb = new StringBuilder(65536);

                // ============================================================
                // TURN DUMP (matches your Turn keys)
                // ============================================================
                const string T_KEY_ToolUsage = "__ToolUsage";
                const string T_KEY_Quadrant = "__Quadrant";
                const string T_KEY_StartXLine = "__StartXLine";
                const string T_KEY_StartZLine = "__StartZLine";
                const string T_KEY_EndXLine = "__EndXLine";
                const string T_KEY_EndZLine = "__EndZLine";
                const string T_KEY_TxtZExt = "TxtZExt";
                const string T_KEY_NRad = "NRad";

                sb.AppendLine("=== TEST DUMP: TURN SETS (keys + RegionLines) ===");
                sb.AppendLine();

                if (TurnSets == null || TurnSets.Count == 0)
                {
                    sb.AppendLine("No TurnSets.");
                }
                else
                {
                    for (int i = 0; i < TurnSets.Count; i++)
                    {
                        var set = TurnSets[i];
                        if (set == null) continue;

                        sb.AppendLine($"--- TURN SET {i + 1}/{TurnSets.Count}: {set.Name ?? "(unnamed)"} ---");

                        string GetSnapT(string key)
                        {
                            if (set.PageSnapshot?.Values == null) return "";
                            return set.PageSnapshot.Values.TryGetValue(key, out string v) ? (v ?? "") : "";
                        }

                        sb.AppendLine("SNAPSHOT:");
                        sb.AppendLine($"  {T_KEY_ToolUsage} = {GetSnapT(T_KEY_ToolUsage)}");
                        sb.AppendLine($"  {T_KEY_Quadrant}  = {GetSnapT(T_KEY_Quadrant)}");
                        sb.AppendLine($"  {T_KEY_TxtZExt}   = {GetSnapT(T_KEY_TxtZExt)}");
                        sb.AppendLine($"  {T_KEY_NRad}      = {GetSnapT(T_KEY_NRad)}");
                        sb.AppendLine($"  {T_KEY_StartXLine}= {GetSnapT(T_KEY_StartXLine)}");
                        sb.AppendLine($"  {T_KEY_StartZLine}= {GetSnapT(T_KEY_StartZLine)}");
                        sb.AppendLine($"  {T_KEY_EndXLine}  = {GetSnapT(T_KEY_EndXLine)}");
                        sb.AppendLine($"  {T_KEY_EndZLine}  = {GetSnapT(T_KEY_EndZLine)}");

                        int rc = (set.RegionLines == null) ? 0 : set.RegionLines.Count;
                        sb.AppendLine($"REGIONLINES: count={rc}");

                        if (rc == 0)
                        {
                            sb.AppendLine("  (no region lines)");
                        }
                        else
                        {
                            sb.AppendLine();
                            for (int r = 0; r < rc; r++)
                                sb.AppendLine($"  [{r + 1:0000}] {set.RegionLines[r] ?? ""}");
                        }

                        sb.AppendLine();
                    }
                }

                sb.AppendLine();
                sb.AppendLine("============================================================");
                sb.AppendLine();

                // ============================================================
                // MILL DUMP (matches MillPage keys)
                // ============================================================
                const string M_KEY_PLANEZ_TEXT = "PlaneZLineText";
                const string M_KEY_STARTX_TEXT = "StartXLineText";
                const string M_KEY_STARTY_TEXT = "StartYLineText";
                const string M_KEY_ENDX_TEXT = "EndXLineText";
                const string M_KEY_ENDY_TEXT = "EndYLineText";
                const string M_KEY_TOOL_DIA = "TxtToolDia";
                const string M_KEY_TOOL_LEN = "TxtToolLen";
                const string M_KEY_FUSEALL = "Fuseall";
                const string M_KEY_REMOVE_SPLITTER = "RemoveSplitter";
                const string M_KEY_CLIPPER = "Clipper";
                const string M_KEY_CLIPPER_ISLAND = "ClipperIsland";
                const string M_KEY_REGION_UID = "__RegionUid";

                sb.AppendLine("=== TEST DUMP: MILL SETS (keys + RegionLines) ===");
                sb.AppendLine();

                if (MillSets == null || MillSets.Count == 0)
                {
                    sb.AppendLine("No MillSets.");
                }
                else
                {
                    for (int i = 0; i < MillSets.Count; i++)
                    {
                        var set = MillSets[i];
                        if (set == null) continue;

                        sb.AppendLine($"--- MILL SET {i + 1}/{MillSets.Count}: {set.Name ?? "(unnamed)"} ---");

                        string GetSnapM(string key)
                        {
                            if (set.PageSnapshot?.Values == null) return "";
                            return set.PageSnapshot.Values.TryGetValue(key, out string v) ? (v ?? "") : "";
                        }

                        sb.AppendLine("SNAPSHOT:");
                        sb.AppendLine($"  {M_KEY_REGION_UID}      = {GetSnapM(M_KEY_REGION_UID)}");
                        sb.AppendLine($"  {M_KEY_PLANEZ_TEXT}     = {GetSnapM(M_KEY_PLANEZ_TEXT)}");
                        sb.AppendLine($"  {M_KEY_STARTX_TEXT}     = {GetSnapM(M_KEY_STARTX_TEXT)}");
                        sb.AppendLine($"  {M_KEY_STARTY_TEXT}     = {GetSnapM(M_KEY_STARTY_TEXT)}");
                        sb.AppendLine($"  {M_KEY_ENDX_TEXT}       = {GetSnapM(M_KEY_ENDX_TEXT)}");
                        sb.AppendLine($"  {M_KEY_ENDY_TEXT}       = {GetSnapM(M_KEY_ENDY_TEXT)}");
                        sb.AppendLine($"  {M_KEY_TOOL_DIA}        = {GetSnapM(M_KEY_TOOL_DIA)}");
                        sb.AppendLine($"  {M_KEY_TOOL_LEN}        = {GetSnapM(M_KEY_TOOL_LEN)}");
                        sb.AppendLine($"  {M_KEY_FUSEALL}         = {GetSnapM(M_KEY_FUSEALL)}");
                        sb.AppendLine($"  {M_KEY_REMOVE_SPLITTER} = {GetSnapM(M_KEY_REMOVE_SPLITTER)}");
                        sb.AppendLine($"  {M_KEY_CLIPPER}         = {GetSnapM(M_KEY_CLIPPER)}");
                        sb.AppendLine($"  {M_KEY_CLIPPER_ISLAND}  = {GetSnapM(M_KEY_CLIPPER_ISLAND)}");

                        int rc = (set.RegionLines == null) ? 0 : set.RegionLines.Count;
                        sb.AppendLine($"REGIONLINES: count={rc}");

                        if (rc == 0)
                        {
                            sb.AppendLine("  (no region lines)");
                        }
                        else
                        {
                            sb.AppendLine();
                            for (int r = 0; r < rc; r++)
                                sb.AppendLine($"  [{r + 1:0000}] {set.RegionLines[r] ?? ""}");
                        }

                        sb.AppendLine();
                    }
                }


                sb.AppendLine();
                sb.AppendLine("============================================================");
                sb.AppendLine();

                // ============================================================
                // DRILL DUMP (matches DrillPage keys)
                // ============================================================
                const string D_KEY_COORDMODE = "CoordMode";
                const string D_KEY_DRILL_DEPTH_LINE = "DrillDepthLineText";   // not a hole, separate
                const string D_KEY_TXT_CHAMFER = "TxtChamfer";
                const string D_KEY_TXT_HOLE_DIA = "TxtHoleDia";
                const string D_KEY_TXT_POINT_ANGLE = "TxtPointAngle";
                const string D_KEY_Z_HOLE_TOP = "TxtZHoleTop";
                const string D_KEY_Z_PLUS_EXT = "TxtZPlusExt";

                sb.AppendLine("=== TEST DUMP: DRILL SETS (keys + RegionLines) ===");
                sb.AppendLine();

                if (DrillSets == null || DrillSets.Count == 0)
                {
                    sb.AppendLine("No DrillSets.");
                }
                else
                {
                    for (int i = 0; i < DrillSets.Count; i++)
                    {
                        var set = DrillSets[i];
                        if (set == null) continue;

                        sb.AppendLine($"--- DRILL SET {i + 1}/{DrillSets.Count}: {set.Name ?? "(unnamed)"} ---");

                        string GetSnapD(string key)
                        {
                            if (set.PageSnapshot?.Values == null) return "";
                            return set.PageSnapshot.Values.TryGetValue(key, out string v) ? (v ?? "") : "";
                        }

                        sb.AppendLine("SNAPSHOT:");
                        sb.AppendLine($"  {D_KEY_COORDMODE}        = {GetSnapD(D_KEY_COORDMODE)}");
                        sb.AppendLine($"  {D_KEY_DRILL_DEPTH_LINE} = {GetSnapD(D_KEY_DRILL_DEPTH_LINE)}");
                        sb.AppendLine($"  {D_KEY_TXT_CHAMFER}      = {GetSnapD(D_KEY_TXT_CHAMFER)}");
                        sb.AppendLine($"  {D_KEY_TXT_HOLE_DIA}     = {GetSnapD(D_KEY_TXT_HOLE_DIA)}");
                        sb.AppendLine($"  {D_KEY_TXT_POINT_ANGLE}  = {GetSnapD(D_KEY_TXT_POINT_ANGLE)}");
                        sb.AppendLine($"  {D_KEY_Z_HOLE_TOP}       = {GetSnapD(D_KEY_Z_HOLE_TOP)}");
                        sb.AppendLine($"  {D_KEY_Z_PLUS_EXT}       = {GetSnapD(D_KEY_Z_PLUS_EXT)}");

                        int rc = (set.RegionLines == null) ? 0 : set.RegionLines.Count;
                        sb.AppendLine($"REGIONLINES (holes): count={rc}");

                        if (rc == 0)
                        {
                            sb.AppendLine("  (no region lines)");
                        }
                        else
                        {
                            sb.AppendLine();
                            for (int r = 0; r < rc; r++)
                                sb.AppendLine($"  [{r + 1:0000}] {set.RegionLines[r] ?? ""}");
                        }

                        sb.AppendLine();
                    }
                }






                // Show in LogWindow
                var ownerW = Application.Current?.MainWindow;
                var logWindow = new CNC_Improvements_gcode_solids.Utilities.LogWindow("TEST: Turn + Mill Sets dump", sb.ToString());
                if (ownerW != null) logWindow.Owner = ownerW;
                logWindow.Show();
                logWindow.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "TEST DUMP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Add this inside MainWindow class
        // Uses the canonical builders.
        // Feeds RAW lines + marker indices/fields.
        // Builders generate UID, normalize, build anchored RegionLines, and store anchored marker snapshot strings.
        private void Build3TestRegions_Static()
        {
            if (TurnSets == null || MillSets == null || DrillSets == null)
                throw new Exception("TurnSets/MillSets/DrillSets is null.");

            TurnSets.Clear();
            MillSets.Clear();
            DrillSets.Clear();

            // =========================
            // DRILL (depth line index = 3)
            // =========================
            var drillLines = new List<string>
    {
        "991: G0X39.5Y0.                                                                 (u:c0237)",
        "992: G0Z20.                                                                     (u:c0238)",
        "993: G99                                                                        (u:c0239)",
        "994: G81Z-77.8R-29.F1000.                                                       (u:c0240)", // zdepth marker
        "995: X19.75Y34.208                                                              (u:c0241)", // holes
        "996: X-19.75Y34.208                                                             (u:c0242)",
        "997: X-39.5Y0.                                                                  (u:c0243)",
        "998: X-19.75Y-34.208                                                            (u:c0244)",
        "999: X19.75Y-34.208                                                             (u:c0245)"
    };

            var drillSet = CNC_Improvements_gcode_solids.SetManagement.Builders.BuildDrillRegion.Create(
                regionName: "TEST_DRILL",
                regionLines: drillLines,
                drillDepthIndex: 3,
                coordMode: "Cartesian",
                txtChamfer: "1",
                txtHoleDia: "12",
                txtPointAngle: "118",
                txtZHoleTop: "-32",
                txtZPlusExt: "5",
                snapshotDefaults: null
            );

            DrillSets.Add(drillSet);

            // =========================
            // MILL
            // planeZ index = 0
            // start X/Y index = 1
            // end X/Y index = 4
            // =========================
            var millLines = new List<string>
    {
        "1576: G1Z-14.F500.                                                               (u:c0822)", // zplane
        "1577: G1X194.303Y0.F1100.                                                        (u:c0823)", // start
        "1578: G2X194.298Y-1.477I-194.303J0.F1100.                                        (u:c0824)",
        "1579: G2X167.622Y-35.442I-35.302J0.268F1100.                                     (u:c0825)",
        "1580: G2X161.123Y-36.941I-40.395J160.292F1100.                                   (u:c0826)" // end
    };

            var millSet = CNC_Improvements_gcode_solids.SetManagement.Builders.BuildMillRegion.Create(
                regionName: "TEST_MILL",
                regionLines: millLines,
                planeZIndex: 0,
                startXIndex: 1,
                startYIndex: 1,
                endXIndex: 4,
                endYIndex: 4,
                txtToolDia: "10",
                txtToolLen: "75",
                fuseAll: "1",
                removeSplitter: "1",
                clipper: "1",
                clipperIsland: "0",
                snapshotDefaults: null
            );

            MillSets.Add(millSet);

            // =========================
            // TURN
            // start X/Z index = 0
            // end X/Z index = 4
            // =========================
            var turnLines = new List<string>
    {
        "383: G1X334.6576Z-2.4477F0.2                                                    (u:a0382)", // start
        "384: G1X337.5Z-3.9872F0.2                                                       (u:a0383)",
        "385: G1X337.5Z-14.5007F0.2                                                      (u:a0384)",
        "386: G3X340.9496Z-16.I-0.05K-1.7993F0.2                                         (u:a0385)",
        "387: G1X360.Z-16.F0.2                                                           (u:a0386)"  // end
    };

            var turnSet = CNC_Improvements_gcode_solids.SetManagement.Builders.BuildTurnRegion.Create(
                regionName: "TEST_TURN",
                regionLines: turnLines,
                startXIndex: 0,
                startZIndex: 0,
                endXIndex: 4,
                endZIndex: 4,
                toolUsage: "RIGHT",
                quadrant: "3",
                txtZExt: "-100",
                nRad: "0.8",
                snapshotDefaults: null
            );

            TurnSets.Add(turnSet);
        }



    }
}
