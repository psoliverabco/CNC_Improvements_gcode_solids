using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace CNC_Improvements_gcode_solids.Pages
{
    public partial class TransMatrix : Page, INotifyPropertyChanged
    {
        public ObservableCollection<TransformMatrixVm> Matrices { get; } = new();

        private TransformMatrixVm? _selectedMatrix;
        public TransformMatrixVm? SelectedMatrix
        {
            get => _selectedMatrix;
            set
            {
                if (ReferenceEquals(_selectedMatrix, value)) return;
                _selectedMatrix = value;
                OnPropertyChanged();
                ApplySelectedToCard();
            }
        }

        public TransformMatrixVm DefaultMatrix { get; private set; }

        // --- Cut/Paste clipboard ---
        private string _clipboardRegion = "";
        private bool _clipboardHasValue = false;

        // Cached master order (Turn -> Mill -> Drill) from last RefreshFromMainWindow call
        private List<string> _masterRegionOrder = new List<string>();

        public TransMatrix()
        {
            InitializeComponent();
            DataContext = this;

            DefaultMatrix = TransformMatrixVm.CreateDefaultLocked();
            Matrices.Add(DefaultMatrix);

            SelectedMatrix = DefaultMatrix;
            ApplySelectedToCard();
        }

        // ------------------------------------------------------------
        // REQUIRED BY MainWindow Save/Load
        // ------------------------------------------------------------
        public List<TransformMatrixDto> ExportTransformDtos()
        {
            var list = new List<TransformMatrixDto>();

            for (int i = 0; i < Matrices.Count; i++)
            {
                var m = Matrices[i];
                if (m == null) continue;

                bool isDefault = ReferenceEquals(m, DefaultMatrix) || m.IsLocked;

                // IMPORTANT:
                // Persist the default card under a stable INTERNAL name.
                // UI can still display "Base. No Transformation" via CreateDefaultLocked().
                string persistName = isDefault ? "No Transformation" : (m.MatrixName ?? "");

                var dto = new TransformMatrixDto
                {
                    MatrixName = persistName.Trim().Length == 0
                        ? (isDefault ? "No Transformation" : "Transform")
                        : persistName.Trim(),

                    RotZ = m.RotZ ?? "0",
                    RotY = m.RotY ?? "0",
                    Tx = m.Tx ?? "0",
                    Ty = m.Ty ?? "0",
                    Tz = m.Tz ?? "0",

                    // Only the default should be locked on disk.
                    IsLocked = isDefault,

                    Regions = new List<string>(
                        m.Regions.Select(r => (r ?? "").Trim())
                                 .Where(r => r.Length > 0))
                };

                list.Add(dto);
            }

            return list;
        }


        public void ImportTransformDtos(List<TransformMatrixDto>? dtos)
        {
            Matrices.Clear();

            DefaultMatrix = TransformMatrixVm.CreateDefaultLocked();
            Matrices.Add(DefaultMatrix);

            if (dtos == null || dtos.Count == 0)
            {
                SelectedMatrix = DefaultMatrix;
                ApplySelectedToCard();
                return;
            }

            // Treat any of these as "default" (supports old broken saves too)
            static bool IsDefaultDto(TransformMatrixDto? d)
            {
                if (d == null) return false;
                string nm = (d.MatrixName ?? "").Trim();

                return d.IsLocked
                       || nm.Equals("No Transformation", StringComparison.OrdinalIgnoreCase)
                       || nm.Equals("Base. No Transformation", StringComparison.OrdinalIgnoreCase)
                       || nm.Equals("Base", StringComparison.OrdinalIgnoreCase);
            }

            // Use the FIRST default-like dto to populate default regions (collapses multi-base files)
            TransformMatrixDto? savedDefault = dtos.FirstOrDefault(IsDefaultDto);
            if (savedDefault != null)
            {
                DefaultMatrix.Regions.Clear();
                foreach (var r in savedDefault.Regions ?? new List<string>())
                {
                    string name = (r ?? "").Trim();
                    if (name.Length > 0)
                        DefaultMatrix.Regions.Add(name);
                }
            }

            // Import the rest, skipping anything default-like
            foreach (var d in dtos)
            {
                if (d == null) continue;
                if (IsDefaultDto(d)) continue;

                string nm = (d.MatrixName ?? "").Trim();

                var vm = new TransformMatrixVm
                {
                    MatrixName = nm.Length == 0 ? "Transform" : nm,
                    RotZ = d.RotZ ?? "0",
                    RotY = d.RotY ?? "0",
                    Tx = d.Tx ?? "0",
                    Ty = d.Ty ?? "0",
                    Tz = d.Tz ?? "0",

                    // Never import locked non-defaults (avoid creating undeletable junk)
                    IsLocked = false
                };

                vm.Regions.Clear();
                if (d.Regions != null)
                {
                    foreach (var r in d.Regions)
                    {
                        string name = (r ?? "").Trim();
                        if (name.Length > 0)
                            vm.Regions.Add(name);
                    }
                }

                Matrices.Add(vm);
            }

            SelectedMatrix = DefaultMatrix;
            ApplySelectedToCard();
        }

        // ------------------------------------------------------------
        // Refresh from MainWindow region sets
        // ------------------------------------------------------------
        public void RefreshFromMainWindow(CNC_Improvements_gcode_solids.MainWindow main)
        {
            if (main == null)
                return;

            List<string> master = main.GetAllRegionNamesOrdered()
                                      .Select(s => (s ?? "").Trim())
                                      .Where(s => s.Length > 0)
                                      .ToList();

            _masterRegionOrder = new List<string>(master);

            var masterSet = new HashSet<string>(master, StringComparer.OrdinalIgnoreCase);

            // Remove deleted regions
            foreach (var m in Matrices)
            {
                if (m == null) continue;

                for (int i = m.Regions.Count - 1; i >= 0; i--)
                {
                    string r = (m.Regions[i] ?? "").Trim();
                    if (r.Length == 0 || !masterSet.Contains(r))
                        m.Regions.RemoveAt(i);
                }
            }

            // Enforce unique assignment: keep first, remove duplicates
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in Matrices)
            {
                if (m == null) continue;

                for (int i = m.Regions.Count - 1; i >= 0; i--)
                {
                    string r = (m.Regions[i] ?? "").Trim();
                    if (r.Length == 0)
                    {
                        m.Regions.RemoveAt(i);
                        continue;
                    }

                    if (seen.Contains(r))
                        m.Regions.RemoveAt(i);
                    else
                        seen.Add(r);
                }
            }

            // Unassigned -> default
            foreach (var r in master)
            {
                if (!seen.Contains(r))
                    DefaultMatrix.Regions.Add(r);
            }

            // Order each card to master
            foreach (var m in Matrices)
            {
                if (m == null) continue;
                ReorderRegionsToMaster(m.Regions, _masterRegionOrder);
            }

            if (SelectedMatrix == null || !Matrices.Contains(SelectedMatrix))
                SelectedMatrix = DefaultMatrix;

            ApplySelectedToCard();
        }

        private static void ReorderRegionsToMaster(ObservableCollection<string> regions, List<string> master)
        {
            if (regions == null) return;
            if (master == null || master.Count == 0) return;

            var set = new HashSet<string>(
                regions.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            var ordered = new List<string>();
            for (int i = 0; i < master.Count; i++)
            {
                string r = master[i];
                if (set.Contains(r))
                    ordered.Add(r);
            }

            regions.Clear();
            for (int i = 0; i < ordered.Count; i++)
                regions.Add(ordered[i]);
        }

        private void ReorderAllCards()
        {
            if (_masterRegionOrder == null || _masterRegionOrder.Count == 0)
                return;

            for (int i = 0; i < Matrices.Count; i++)
            {
                var m = Matrices[i];
                if (m == null) continue;
                ReorderRegionsToMaster(m.Regions, _masterRegionOrder);
            }
        }

        private void RemoveRegionFromAllMatrices(string regionName)
        {
            string r = (regionName ?? "").Trim();
            if (r.Length == 0) return;

            for (int mi = 0; mi < Matrices.Count; mi++)
            {
                var m = Matrices[mi];
                if (m == null) continue;

                for (int i = m.Regions.Count - 1; i >= 0; i--)
                {
                    if (string.Equals((m.Regions[i] ?? "").Trim(), r, StringComparison.OrdinalIgnoreCase))
                        m.Regions.RemoveAt(i);
                }

                if (string.Equals((m.SelectedRegion ?? "").Trim(), r, StringComparison.OrdinalIgnoreCase))
                    m.SelectedRegion = "";
            }
        }

        // ------------------------------------------------------------
        // Cut/Paste buttons (fixes your CS1061 errors)
        // ------------------------------------------------------------
        private void BtnCutRegion_Click(object sender, RoutedEventArgs e)
        {
            var selMatrix = SelectedMatrix;
            if (selMatrix == null)
                return;

            string region = (selMatrix.SelectedRegion ?? "").Trim();
            if (region.Length == 0)
            {
                MessageBox.Show("Select a region in the card first.", "Cut Region",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // If region no longer exists in the master list, refuse and clear it out
            if (_masterRegionOrder != null && _masterRegionOrder.Count > 0)
            {
                bool exists = _masterRegionOrder.Any(x => string.Equals((x ?? "").Trim(), region, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    RemoveRegionFromAllMatrices(region);
                    MessageBox.Show("That region no longer exists in the set lists.", "Cut Region",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Remove from current matrix
            for (int i = selMatrix.Regions.Count - 1; i >= 0; i--)
            {
                if (string.Equals((selMatrix.Regions[i] ?? "").Trim(), region, StringComparison.OrdinalIgnoreCase))
                    selMatrix.Regions.RemoveAt(i);
            }
            selMatrix.SelectedRegion = "";

            // Clipboard
            _clipboardRegion = region;
            _clipboardHasValue = true;

            // Unassigned => goes to Default card immediately (per your rule)
            if (!DefaultMatrix.Regions.Any(x => string.Equals((x ?? "").Trim(), region, StringComparison.OrdinalIgnoreCase)))
                DefaultMatrix.Regions.Add(region);

            ReorderAllCards();
        }

        private void BtnPasteRegion_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedMatrix;
            if (target == null)
                return;

            if (!_clipboardHasValue || string.IsNullOrWhiteSpace(_clipboardRegion))
            {
                MessageBox.Show("Clipboard is empty. Cut a region first.", "Paste Region",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string region = _clipboardRegion.Trim();

            // If region no longer exists in the master list, refuse and clear clipboard
            if (_masterRegionOrder != null && _masterRegionOrder.Count > 0)
            {
                bool exists = _masterRegionOrder.Any(x => string.Equals((x ?? "").Trim(), region, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    _clipboardRegion = "";
                    _clipboardHasValue = false;
                    MessageBox.Show("That region no longer exists in the set lists.", "Paste Region",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Move into target: remove from every other matrix, then add to target
            RemoveRegionFromAllMatrices(region);

            if (!target.Regions.Any(x => string.Equals((x ?? "").Trim(), region, StringComparison.OrdinalIgnoreCase)))
                target.Regions.Add(region);

            ReorderAllCards();

            target.SelectedRegion = region;
        }

        // ------------------------------------------------------------
        // Existing UI
        // ------------------------------------------------------------
        private void ApplySelectedToCard()
        {
            if (CardHost != null)
                CardHost.DataContext = SelectedMatrix;
        }

        private void BtnAddMatrix_Click(object sender, RoutedEventArgs e)
        {
            if (!TryPromptForMatrixName("New Matrix Name", "Transform 1", out string name))
                return;

            while (Matrices.Any(m => string.Equals((m.MatrixName ?? "").Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("That matrix name already exists. Choose a unique name.", "Add Matrix",
                    MessageBoxButton.OK, MessageBoxImage.Warning);

                if (!TryPromptForMatrixName("New Matrix Name", name, out name))
                    return;
            }

            var vm = new TransformMatrixVm
            {
                MatrixName = name.Trim(),
                RotZ = "0",
                RotY = "0",
                Tx = "0",
                Ty = "0",
                Tz = "0",
                IsLocked = false
            };

            Matrices.Add(vm);
            SelectedMatrix = vm;
            LstMatrixNames?.ScrollIntoView(vm);
        }

        private void BtnRemoveMatrix_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedMatrix;
            if (sel == null) return;

            if (ReferenceEquals(sel, DefaultMatrix) || sel.IsLocked)
                return;

            int idx = Matrices.IndexOf(sel);
            if (idx <= 0) return;

            // When deleting, move its regions back to Default
            var toMove = sel.Regions.Select(r => (r ?? "").Trim()).Where(r => r.Length > 0).ToList();
            Matrices.RemoveAt(idx);

            foreach (var r in toMove)
            {
                if (!DefaultMatrix.Regions.Any(x => string.Equals((x ?? "").Trim(), r, StringComparison.OrdinalIgnoreCase)))
                    DefaultMatrix.Regions.Add(r);
            }

            int newIdx = idx;
            if (newIdx >= Matrices.Count) newIdx = Matrices.Count - 1;
            if (newIdx < 0) newIdx = 0;

            SelectedMatrix = Matrices[newIdx];
            LstMatrixNames?.ScrollIntoView(SelectedMatrix);

            ReorderAllCards();
        }

        private bool TryPromptForMatrixName(string title, string defaultValue, out string name)
        {
            name = "";

            var win = new Window
            {
                Title = title,
                Owner = Window.GetWindow(this),
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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class TransformMatrixVm : INotifyPropertyChanged
    {
        private string _matrixName = "";
        private string _rotZ = "0";
        private string _rotY = "0";
        private string _tx = "0";
        private string _ty = "0";
        private string _tz = "0";
        private bool _isLocked;

        private string _selectedRegion = "";

        public string MatrixName { get => _matrixName; set { if (_matrixName == value) return; _matrixName = value ?? ""; OnPropertyChanged(); } }
        public string RotZ { get => _rotZ; set { if (_rotZ == value) return; _rotZ = value ?? "0"; OnPropertyChanged(); } }
        public string RotY { get => _rotY; set { if (_rotY == value) return; _rotY = value ?? "0"; OnPropertyChanged(); } }
        public string Tx { get => _tx; set { if (_tx == value) return; _tx = value ?? "0"; OnPropertyChanged(); } }
        public string Ty { get => _ty; set { if (_ty == value) return; _ty = value ?? "0"; OnPropertyChanged(); } }
        public string Tz { get => _tz; set { if (_tz == value) return; _tz = value ?? "0"; OnPropertyChanged(); } }

        public bool IsLocked { get => _isLocked; set { if (_isLocked == value) return; _isLocked = value; OnPropertyChanged(); } }

        public ObservableCollection<string> Regions { get; } = new ObservableCollection<string>();

        // Used by MatrixCard ListView SelectedItem binding
        public string SelectedRegion
        {
            get => _selectedRegion;
            set
            {
                if (_selectedRegion == value) return;
                _selectedRegion = value ?? "";
                OnPropertyChanged();
            }
        }





















        public static TransformMatrixVm CreateDefaultLocked()
        {
            return new TransformMatrixVm
            {
                MatrixName = "Base. No Transformation",
                RotZ = "0",
                RotY = "0",
                Tx = "0",
                Ty = "0",
                Tz = "0",
                IsLocked = true
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
