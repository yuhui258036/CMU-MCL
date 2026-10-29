using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

using CMUmcl.Content;

namespace CMUmcl
{
    /// <summary>One editable row in the migration conflict dialog.</summary>
    public sealed class ConflictRowViewModel : INotifyPropertyChanged
    {
        private string _targetError = string.Empty;
        private string _displayError = string.Empty;

        public ConflictRowViewModel(MigrationConflict conflict)
        {
            Conflict = conflict;
            TargetName = conflict.ProposedTargetName;
            DisplayName = conflict.ProposedDisplayName;
        }

        public MigrationConflict Conflict { get; }

        public string SourceDisplayName => Conflict.SourceDisplayName;

        public string Hint => $"来源目录：{Conflict.SourceRelativePath}";

        public string TargetName { get; set; }

        public string DisplayName { get; set; }

        public string TargetError
        {
            get => _targetError;
            set
            {
                if (_targetError == value) return;
                _targetError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTargetError));
            }
        }

        public bool HasTargetError => !string.IsNullOrEmpty(_targetError);

        public string DisplayError
        {
            get => _displayError;
            set
            {
                if (_displayError == value) return;
                _displayError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDisplayError));
            }
        }

        public bool HasDisplayError => !string.IsNullOrEmpty(_displayError);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Modal dialog that lets the user manually rename instances whose target folder or display
    /// name collides during an instance-storage migration. Returns a dictionary of
    /// instance-id → <see cref="MigrationNameOverride"/> on OK, or null on cancel.
    /// </summary>
    public partial class MigrationConflictWindow : Window
    {
        private readonly string _targetRoot;
        private readonly ObservableCollection<ConflictRowViewModel> _rows = new();

        public IReadOnlyDictionary<string, MigrationNameOverride>? Result { get; private set; }

        public MigrationConflictWindow(IReadOnlyList<MigrationConflict> conflicts, string targetRoot)
        {
            InitializeComponent();
            _targetRoot = targetRoot ?? throw new ArgumentNullException(nameof(targetRoot));

            foreach (MigrationConflict conflict in conflicts)
                _rows.Add(new ConflictRowViewModel(conflict));

            ConflictsList.ItemsSource = _rows;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var overrides = new Dictionary<string, MigrationNameOverride>(StringComparer.Ordinal);
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDisplays = new HashSet<string>(StringComparer.Ordinal);
            bool valid = true;

            foreach (ConflictRowViewModel row in _rows)
            {
                row.TargetError = string.Empty;
                row.DisplayError = string.Empty;

                string target = row.TargetName?.Trim() ?? string.Empty;
                string display = row.DisplayName?.Trim() ?? string.Empty;

                string? folderError = InstanceMigrationService.ValidateFolderName(target);
                if (folderError != null)
                {
                    row.TargetError = folderError;
                    valid = false;
                }
                else if (!seenTargets.Add(target))
                {
                    row.TargetError = "目录名与其他条目重复。";
                    valid = false;
                }
                else if (Directory.Exists(Path.Combine(_targetRoot, target)))
                {
                    row.TargetError = "目标目录已存在同名文件夹。";
                    valid = false;
                }

                if (display.Length == 0)
                {
                    row.DisplayError = "显示名称不能为空。";
                    valid = false;
                }
                else if (!seenDisplays.Add(display))
                {
                    row.DisplayError = "显示名称与其他条目重复。";
                    valid = false;
                }

                overrides[row.Conflict.InstanceId] = new MigrationNameOverride
                {
                    TargetName = target,
                    DisplayName = display,
                };
            }

            if (!valid)
            {
                ErrorText.Text = "请修正上方标出的错误。";
                return;
            }

            Result = overrides;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
