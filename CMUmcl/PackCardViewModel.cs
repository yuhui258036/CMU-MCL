using System.ComponentModel;
using System.Runtime.CompilerServices;
using CMUmcl.Content;

namespace CMUmcl
{
    /// <summary>View model backing one server-pack card on the 服务器整合包 page.</summary>
    public sealed class PackCardViewModel : INotifyPropertyChanged
    {
        private readonly ManifestInstance _instance;

        /// <summary>Server-pack card backed by a manifest entry.</summary>
        public PackCardViewModel(ManifestInstance instance, int? installedRevision, string? displayName = null)
        {
            _instance = instance;

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

        public ManifestInstance Instance => _instance;
        public string InstanceId => _instance.Id;

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

        /// <summary>True when the primary (install/update) button is visible.</summary>
        private bool _isInstallVisible = true;
        public bool IsInstallVisible
        {
            get => _isInstallVisible;
            private set { _isInstallVisible = value; OnPropertyChanged(); }
        }

        /// <summary>True when the secondary (查看实例) button is visible (installed current).</summary>
        private bool _isViewInstancesVisible;
        public bool IsViewInstancesVisible
        {
            get => _isViewInstancesVisible;
            private set { _isViewInstancesVisible = value; OnPropertyChanged(); }
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
            else
            {
                Name = _instance.Name;
            }
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
                IsInstallVisible = true;
                IsViewInstancesVisible = false;
                StatusText = "尚未安装";
            }
            else if (installedRevision.Value >= _instance.Revision)
            {
                ButtonText = string.Empty;
                ButtonEnabled = false;
                IsInstallVisible = false;
                IsViewInstancesVisible = true;
                StatusText = $"已安装 r{installedRevision.Value}";
            }
            else
            {
                ButtonText = $"更新到 r{_instance.Revision}";
                ButtonEnabled = true;
                IsInstallVisible = true;
                IsViewInstancesVisible = false;
                StatusText = $"已安装 r{installedRevision.Value}，可更新到 r{_instance.Revision}";
            }
        }

        /// <summary>Enters the busy (installing) state.</summary>
        public void BeginInstall()
        {
            ButtonEnabled = false;
            ButtonText = "安装中…";
            IsInstallVisible = true;
            IsViewInstancesVisible = false;
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

        /// <summary>Marks the install as complete (installed current; now offers 查看实例).</summary>
        public void Complete()
        {
            IsProgressVisible = false;
            ButtonText = string.Empty;
            ButtonEnabled = false;
            IsInstallVisible = false;
            IsViewInstancesVisible = true;
            StatusText = $"已安装 r{_instance.Revision}";
        }

        /// <summary>Applies an install progress report to the card.</summary>
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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
