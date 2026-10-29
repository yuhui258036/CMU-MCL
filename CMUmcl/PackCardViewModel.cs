using System.ComponentModel;
using System.Runtime.CompilerServices;
using CMUmcl.Content;

namespace CMUmcl
{
    /// <summary>View model backing one server-pack or local-instance card in the main window.</summary>
    public sealed class PackCardViewModel : INotifyPropertyChanged
    {
        private readonly ManifestInstance? _instance;
        private readonly string _instanceId;

        /// <summary>Server-pack card backed by a manifest entry.</summary>
        public PackCardViewModel(ManifestInstance instance, int? installedRevision, string? displayName = null)
        {
            _instance = instance;
            _instanceId = instance.Id;

            Name = string.IsNullOrWhiteSpace(displayName) ? instance.Name : displayName!;
            Description = string.IsNullOrWhiteSpace(instance.Description)
                ? "（无描述）"
                : instance.Description!;
            RevisionText = $"版本 r{instance.Revision}";
            GameText = $"Minecraft {instance.Minecraft?.GameVersion ?? "?"} · {instance.Minecraft?.Loader ?? "?"} {instance.Minecraft?.LoaderVersion ?? ""}".Trim();
            ServerText = $"服务器 {(string.IsNullOrWhiteSpace(instance.Server?.Address) ? "—" : instance.Server.Address)}:{instance.Server?.Port ?? 0}";
            SizeText = $"大小 {FormatUtils.FormatBytes(instance.Pack?.Size ?? 0)}";

            string loader = instance.Minecraft?.Loader?.Trim().ToLowerInvariant() ?? string.Empty;
            if (loader is not ("" or "vanilla" or "fabric" or "forge" or "neoforge" or "quilt"))
            {
                HasLoaderNote = true;
                LoaderNote = "不支持的加载器类型，启动时可能失败。";
            }

            UpdateButtonState(installedRevision);
        }

        /// <summary>Local-instance card backed by an on-disk instance.json (no manifest entry).</summary>
        public PackCardViewModel(string localId, InstanceMetadata metadata, string displayName)
        {
            _instance = null;
            _instanceId = localId;
            IsLocal = true;

            Name = displayName;
            Description = "本地实例";
            RevisionText = string.Empty;
            GameText = $"Minecraft {metadata.Game ?? "?"} · {FormatLoader(metadata.Loader, metadata.LoaderVersion)}";
            ServerText = string.Empty;
            SizeText = "本地实例";
            HasLoaderNote = false;

            ButtonText = "启动游戏";
            ButtonEnabled = true;
            IsLaunchable = true;
            StatusText = "已就绪";
        }

        public ManifestInstance? Instance => _instance;
        public string InstanceId => _instanceId;

        /// <summary>True for locally-created instances (no manifest entry, no pack install/update path).</summary>
        public bool IsLocal { get; }

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            private set { _name = value; OnPropertyChanged(); }
        }
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

        /// <summary>Updates the displayed name from the installed-state record (falls back to the manifest name).</summary>
        public void SetDisplayName(string? displayName)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                Name = displayName!;
            }
            else if (_instance != null)
            {
                Name = _instance.Name;
            }
            // Local cards keep their recorded name when the store has no override.
        }

        /// <summary>Formats a loader/version pair for display (local cards), with a friendly label for vanilla.</summary>
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
            else if (installedRevision.Value >= _instance!.Revision)
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
                StatusText = $"可更新到 r{_instance!.Revision}";
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
            StatusText = $"已安装（r{_instance!.Revision}）";
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
