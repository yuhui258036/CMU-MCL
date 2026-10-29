using System.ComponentModel;
using System.Runtime.CompilerServices;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl
{
    /// <summary>View model backing one installed-instance card on the 实例 page.</summary>
    public sealed class InstanceCardViewModel : INotifyPropertyChanged
    {
        public string InstanceId { get; }
        public string InstallPath { get; }
        public string DisplayName { get; }
        public string SourceText { get; }
        public string GameText { get; }
        public string PathText { get; }

        /// <summary>
        /// Creates a card from an installed-state record plus its on-disk metadata.
        /// A null <paramref name="metadata"/> (missing/corrupt instance.json) yields a card
        /// whose launch action is disabled instead of crashing the list.
        /// </summary>
        public InstanceCardViewModel(
            string id,
            string displayName,
            string sourceText,
            InstanceMetadata? metadata,
            string instanceDir)
        {
            InstanceId = id;
            InstallPath = instanceDir;
            DisplayName = displayName;
            SourceText = sourceText;
            PathText = instanceDir;

            if (metadata == null)
            {
                GameText = "实例信息缺失或损坏";
                StatusText = "实例信息缺失或损坏";
                ButtonEnabled = false;
            }
            else
            {
                GameText = $"Minecraft {metadata.Game ?? "?"} · {FormatLoader(metadata.Loader, metadata.LoaderVersion)}";
                StatusText = "已就绪";
            }
        }

        /// <summary>Formats a loader/version pair for display, with a friendly label for vanilla.</summary>
        private static string FormatLoader(string? loader, string? loaderVersion)
        {
            string normalized = (loader ?? string.Empty).Trim().ToLowerInvariant();
            string label = normalized switch
            {
                "" or "vanilla" => "原版",
                "fabric" => "Fabric",
                "forge" => "Forge",
                "neoforge" => "NeoForge",
                "quilt" => "Quilt",
                _ => (loader ?? string.Empty).Trim(),
            };

            string version = (loaderVersion ?? string.Empty).Trim();
            return string.IsNullOrEmpty(version) ? label : $"{label} {version}";
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        private string _buttonText = "启动游戏";
        public string ButtonText
        {
            get => _buttonText;
            private set { _buttonText = value; OnPropertyChanged(); }
        }

        private bool _buttonEnabled = true;
        public bool ButtonEnabled
        {
            get => _buttonEnabled;
            private set { _buttonEnabled = value; OnPropertyChanged(); }
        }

        private bool _isProgressVisible;
        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            private set { _isProgressVisible = value; OnPropertyChanged(); }
        }

        private bool _isIndeterminate;
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            private set { _isIndeterminate = value; OnPropertyChanged(); }
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            private set { _progressValue = value; OnPropertyChanged(); }
        }

        /// <summary>Enters the busy (launching) state.</summary>
        public void BeginLaunch()
        {
            ButtonEnabled = false;
            ButtonText = "启动中…";
            IsProgressVisible = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusText = "准备启动…";
        }

        /// <summary>Marks the launch as failed and re-enables the button.</summary>
        public void LaunchFail(string message)
        {
            IsProgressVisible = false;
            ButtonEnabled = true;
            ButtonText = "启动游戏";
            StatusText = message;
        }

        /// <summary>Marks the launch as started (game process running).</summary>
        public void LaunchStarted()
        {
            IsProgressVisible = false;
            ButtonEnabled = true;
            ButtonText = "启动游戏";
            StatusText = "游戏已启动";
        }

        /// <summary>
        /// Marks the launch as pending: the process is running but no game window was
        /// detected yet. Re-enables the button without claiming the game has started.
        /// </summary>
        public void LaunchWindowPending()
        {
            IsProgressVisible = false;
            ButtonEnabled = true;
            ButtonText = "启动游戏";
            StatusText = "游戏进程已启动，等待窗口/请查看日志";
        }

        /// <summary>Applies a launch progress report to the card.</summary>
        public void ReportLaunchProgress(LaunchProgress p)
        {
            IsProgressVisible = true;
            if (!string.IsNullOrEmpty(p.Message))
                StatusText = p.Message;

            if (p.TotalTasks > 0)
            {
                IsIndeterminate = false;
                ProgressValue = p.ProgressedTasks * 100.0 / p.TotalTasks;
            }
            else if (p.TotalBytes > 0)
            {
                IsIndeterminate = false;
                ProgressValue = p.ProgressedBytes * 100.0 / p.TotalBytes;
            }
            else
            {
                IsIndeterminate = true;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
