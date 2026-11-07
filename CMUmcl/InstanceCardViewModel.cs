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
        public string DisplayName { get; private set; }
        public string? IconSource { get; private set; }
        public string SourceText { get; }
        public string GameText { get; private set; }
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
            IconSource=LoaderIcon(metadata);
            try { var options=InstanceOptionsStore.Load(instanceDir);if(!string.IsNullOrWhiteSpace(options.Name))DisplayName=options.Name!;if(options.Icon!=null)IconSource="/Assets/InstanceIcons/"+options.Icon; } catch { /* Settings editor reports invalid settings; retain original card. */ }
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

        internal void RefreshPresentation(InstanceCardViewModel latest)
        {
            DisplayName=latest.DisplayName;IconSource=latest.IconSource;GameText=latest.GameText;
            OnPropertyChanged(nameof(DisplayName));OnPropertyChanged(nameof(IconSource));OnPropertyChanged(nameof(GameText));
        }

        private static string? LoaderIcon(InstanceMetadata? metadata)
        {
            if(metadata==null)return null;
            string? file=(metadata.Loader??"").Trim().ToLowerInvariant() switch
            {
                "fabric"=>"Fabric.png",
                "forge"=>"Forge.png",
                "neoforge"=>"NeoForge.png",
                "quilt"=>"Quilt.png",
                "optifine"=>"OptiFine.png",
                "" or "vanilla"=>"grass-block.png",
                _=>null,
            };
            return file==null?null:"/Assets/InstanceIcons/"+file;
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

        private string _launchActionText = "";
        public string LaunchActionText { get=>_launchActionText; private set { _launchActionText=value;OnPropertyChanged();OnPropertyChanged(nameof(HasLaunchAction)); } }
        public bool HasLaunchAction => LaunchActionText.Length>0;
        internal void ClearLaunchActionIfIdle(bool running) { if(!running)LaunchActionText=""; }
        internal const string CrashReportAction="导出崩溃报告";
        internal void OfferCrashReport() { LaunchActionText=CrashReportAction; }
        internal void GameExited(int? exitCode=null,string? hint=null,bool stoppedByUser=false)
        {
            LaunchActionText="";
            if(stoppedByUser || exitCode is null or 0){LaunchFail(stoppedByUser?"游戏已结束":"游戏已退出");return;}
            LaunchFail($"游戏异常退出（代码 {exitCode}）"+(hint==null?"":"："+hint));
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
            LaunchActionText="取消启动";
            IsProgressVisible = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusText = "准备启动…";
        }

        /// <summary>Enters the busy (pre-launch pack update) state for a server pack.</summary>
        public void BeginPackUpdate(int targetRevision)
        {
            ButtonEnabled = false;
            ButtonText = "更新中…";
            LaunchActionText="取消启动";
            IsProgressVisible = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusText = $"正在更新整合包 r{targetRevision}…";
        }

        /// <summary>Applies a pack-update progress report to the card (mirrors install progress stages).</summary>
        public void ReportPackUpdateProgress(PackInstallProgress p)
        {
            IsProgressVisible = true;
            if (!string.IsNullOrEmpty(p.Message))
                StatusText = p.Message;

            double? percent = null;
            if (p.Stage == "下载整合包")
            {
                if (p.TotalBytes is long total && total > 0)
                    percent = p.BytesDownloaded * 100.0 / total;
            }
            else if (p.Stage == "下载文件")
            {
                if (p.TotalBytes is long bytes && bytes > 0)
                    percent = p.BytesDownloaded * 100.0 / bytes;
                else if (p.TotalFiles > 0)
                    percent = p.CurrentFile * 100.0 / p.TotalFiles;
            }

            if (percent.HasValue)
                AdvanceProgress(percent.Value);
            else if (ProgressValue <= 0)
                IsIndeterminate = true;
        }

        /// <summary>Marks the launch as failed and re-enables the button.</summary>
        public void LaunchFail(string message)
        {
            IsProgressVisible = false;
            ButtonEnabled = true;
            ButtonText = "启动游戏";
            StatusText = message;
            LaunchActionText="";
        }

        /// <summary>Marks the launch as started (game process running).</summary>
        public void LaunchStarted()
        {
            IsProgressVisible = false;
            ButtonEnabled = true;
            ButtonText = "启动游戏";
            StatusText = "游戏已启动";
            ButtonEnabled=false;LaunchActionText="结束游戏";
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
            ButtonEnabled=false;LaunchActionText="结束游戏";
        }

        /// <summary>Applies a launch progress report to the card.</summary>
        public void ReportLaunchProgress(LaunchProgress p)
        {
            IsProgressVisible = true;
            if (!string.IsNullOrEmpty(p.Message))
                StatusText = p.Message;

            double? percent = null;
            if (p.TotalTasks > 0)
                percent = p.ProgressedTasks * 100.0 / p.TotalTasks;
            else if (p.TotalBytes > 0)
                percent = p.ProgressedBytes * 100.0 / p.TotalBytes;

            if (percent.HasValue)
                AdvanceProgress(percent.Value);
            else if (ProgressValue <= 0)
                IsIndeterminate = true;
        }

        private void AdvanceProgress(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent)) return;
            ProgressValue = Math.Max(ProgressValue, Math.Min(100, Math.Max(0, percent)));
            IsIndeterminate = false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
