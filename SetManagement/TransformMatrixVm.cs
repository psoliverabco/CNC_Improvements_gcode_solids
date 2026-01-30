using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CNC_Improvements_gcode_solids.SetManagement
{
    public sealed class TransformMatrixVm : INotifyPropertyChanged
    {
        private string _matrixName = "";
        private string _rotZ = "0";
        private string _rotY = "0";
        private string _tx = "0";
        private string _ty = "0";
        private string _tz = "0";
        private bool _isLocked;

        public string MatrixName { get => _matrixName; set { if (_matrixName == value) return; _matrixName = value; OnPropertyChanged(); } }
        public string RotZ { get => _rotZ; set { if (_rotZ == value) return; _rotZ = value; OnPropertyChanged(); } }
        public string RotY { get => _rotY; set { if (_rotY == value) return; _rotY = value; OnPropertyChanged(); } }
        public string Tx { get => _tx; set { if (_tx == value) return; _tx = value; OnPropertyChanged(); } }
        public string Ty { get => _ty; set { if (_ty == value) return; _ty = value; OnPropertyChanged(); } }
        public string Tz { get => _tz; set { if (_tz == value) return; _tz = value; OnPropertyChanged(); } }

        public bool IsLocked { get => _isLocked; set { if (_isLocked == value) return; _isLocked = value; OnPropertyChanged(); } }

        public ObservableCollection<string> Regions { get; } = new ObservableCollection<string>();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static TransformMatrixVm CreateDefaultLocked()
        {
            return new TransformMatrixVm
            {
                MatrixName = "No Transformation",
                RotZ = "0",
                RotY = "0",
                Tx = "0",
                Ty = "0",
                Tz = "0",
                IsLocked = true
            };
        }
    }
}
