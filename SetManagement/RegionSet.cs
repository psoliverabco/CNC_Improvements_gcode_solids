using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CNC_Improvements_gcode_solids.SetManagement
{
    public enum RegionSetKind
    {
        Turn,
        Mill,
        Drill
    }

    public enum RegionResolveStatus
    {
        Unset,
        Ok,
        Missing,
        Ambiguous
    }

    /// <summary>
    /// One user-created named "set" (Turn/Mill/Drill).
    /// Stores:
    /// - Name + Kind
    /// - RegionLines (text capture of selected region)
    /// - UI snapshot for the right-hand page controls (generic by control Name)
    /// </summary>
    public sealed class RegionSet : INotifyPropertyChanged
    {
        private string _name = "";
        private RegionResolveStatus _status = RegionResolveStatus.Unset;
        private int? _resolvedStartLine = null;
        private int? _resolvedEndLine = null;

        public Guid Id { get; } = Guid.NewGuid();

        public RegionSetKind Kind { get; }

        public string Name
        {
            get => _name;
            set
            {
                if (value == _name) return;
                _name = value ?? "";
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The stored region as plain text lines (no line numbers).
        /// This is the matching key after editor text edits.
        /// </summary>
        public ObservableCollection<string> RegionLines { get; } = new ObservableCollection<string>();

        public UiStateSnapshot? PageSnapshot { get; set; }

        public RegionResolveStatus Status
        {
            get => _status;
            set
            {
                if (value == _status) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }



        // =========================
        // View/Export toggles
        // =========================

        private bool _showInViewAll = true;
        public bool ShowInViewAll
        {
            get => _showInViewAll;
            set
            {
                if (_showInViewAll == value) return;
                _showInViewAll = value;
                OnPropertyChanged(nameof(ShowInViewAll));
            }
        }

        private bool _exportEnabled = true;
        public bool ExportEnabled
        {
            get => _exportEnabled;
            set
            {
                if (_exportEnabled == value) return;
                _exportEnabled = value;
                OnPropertyChanged(nameof(ExportEnabled));
            }
        }





        public int? ResolvedStartLine
        {
            get => _resolvedStartLine;
            set
            {
                if (value == _resolvedStartLine) return;
                _resolvedStartLine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public int? ResolvedEndLine
        {
            get => _resolvedEndLine;
            set
            {
                if (value == _resolvedEndLine) return;
                _resolvedEndLine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public string StatusText
        {
            get
            {
                return Status switch
                {
                    RegionResolveStatus.Unset => "Unset",
                    RegionResolveStatus.Ok => (ResolvedStartLine.HasValue && ResolvedEndLine.HasValue)
                        ? $"OK (L{ResolvedStartLine.Value + 1}..L{ResolvedEndLine.Value + 1})"
                        : "OK",
                    RegionResolveStatus.Missing => "Missing",
                    RegionResolveStatus.Ambiguous => "Ambiguous",
                    _ => "?"
                };
            }
        }

        public RegionSet(RegionSetKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
