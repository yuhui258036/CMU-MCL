using System.ComponentModel;
using System.Runtime.CompilerServices;
using CMUmcl.Content;

namespace CMUmcl
{
    /// <summary>View model backing one server-pack card in the main window.</summary>
    public sealed class PackCardViewModel : INotifyPropertyChanged
    {
        private readonly ManifestInstance _instance;

        public PackCardViewModel(ManifestInstance instance, int? installedRevision)
        {
            _instance = instance;

            Name = instance.Name;
            Description = string.IsNullOrWhiteSpace(instance.Description)
                ? "（无描述）"
                : instance.Description!;
            RevisionText = $"版本 r{instance.Revision}";
            GameText = $"Minecraft {instance.Minecraft?.GameVersion ?? "?"} · {instance.Minecraft?.Loader ?? "?"} {instance.Minecraft?.LoaderVersion ?? ""}".Trim();
            ServerText = $"服务器 {(string.IsNullOrWhiteSpace(instance.Server?.Address) ? "—" : instance.Server.Address)}:{instance.Server?.Port ?? 0}";
            SizeText = $"大小 {FormatUtils.FormatBytes(instance.Pack?.Size ?? 0)}";

            string loader = instance.Minecraft?.Loader?.Trim().ToLowerInvariant() ?? string.Empty;
            if (loader is "forge" or "neoforge" or "quilt")
            {
                HasLoaderNote = true;
                LoaderNote = "当前先支持原版/Fabric";
            }

            UpdateButtonState(installedRevision);
        }

        public ManifestInstance Instance => _instance;
        public string InstanceId => _instance.Id;

        public string Name { get; }
        public string Description { get; }
        public string RevisionText { get; }
        public string GameText { get; }
        public string ServerText { get; }
        public string SizeText { get; }

        public bool HasLoaderNote { get; }
        public string LoaderNote { get; } = string.Empty;

        /// <summary>True when the instance is installed at the current revision and can be launched.</summary>
        public bool IsLaunchable { get; private set; }

        private string _buttonText = "下载并安装";
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

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
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

        /// <summary>Sets the card into an idle state reflecting the installed revision.</summary>
        public void UpdateButtonState(int? installedRevision)
        {
            IsProgressVisible = false;
            IsIndeterminate = false;
            ProgressValue = 0;

            if (installedRevision == null)
            {
                ButtonText = "下载并安装";
                ButtonEnabled = true;
                StatusText = "尚未安装";
                IsLaunchable = false;
            }
            else if (installedRevision.Value >= _instance.Revision)
            {
                ButtonText = "启动游戏";
                ButtonEnabled = true;
                StatusText = $"已安装（r{installedRevision.Value}）";
                IsLaunchable = true;
            }
            else
            {
                ButtonText = "更新";
                ButtonEnabled = true;
                StatusText = $"可更新到 r{_instance.Revision}";
                IsLaunchable = false;
            }
        }

        /// <summary>Enters the busy (installing) state.</summary>
        public void BeginInstall()
        {
            ButtonEnabled = false;
            ButtonText = "安装中…";
            IsProgressVisible = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusText = "准备安装…";
        }

        /// <summary>Marks the install as failed and re-enables the button.</summary>
        public void Fail(string message)
        {
            IsProgressVisible = false;
            ButtonEnabled = true;
            ButtonText = "重试";
            StatusText = message;
        }

        /// <summary>Marks the install as complete (now launchable).</summary>
        public void Complete()
        {
            IsProgressVisible = false;
            ButtonText = "启动游戏";
            ButtonEnabled = true;
            IsLaunchable = true;
            StatusText = $"已安装（r{_instance.Revision}）";
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

        /// <summary>Applies a progress report to the card.</summary>
        public void ReportProgress(PackInstallProgress p)
        {
            IsProgressVisible = true;
            StatusText = p.Message;

            if (p.Stage == "下载整合包")
            {
                if (p.TotalBytes is long total && total > 0)
                {
                    IsIndeterminate = false;
                    ProgressValue = p.BytesDownloaded * 100.0 / total;
                }
                else
                {
                    IsIndeterminate = true;
                }
            }
            else if (p.Stage == "下载文件")
            {
                if (p.TotalFiles > 0)
                {
                    IsIndeterminate = false;
                    ProgressValue = p.CurrentFile * 100.0 / p.TotalFiles;
                }
                else
                {
                    IsIndeterminate = true;
                }
            }
            else
            {
                IsIndeterminate = true;
            }
        }

        /// <summary>Applies a launch progress report to the card.</summary>
        public void ReportLaunchProgress(Launch.LaunchProgress p)
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
