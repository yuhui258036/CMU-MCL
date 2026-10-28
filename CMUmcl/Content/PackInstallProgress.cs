using System;

namespace CMUmcl.Content
{
    /// <summary>Progress report for a pack install operation.</summary>
    public sealed class PackInstallProgress
    {
        /// <summary>Current stage description (e.g. 下载整合包 / 下载文件 / 解压覆盖 / 完成).</summary>
        public string Stage { get; init; } = string.Empty;

        /// <summary>Index of the current file being processed (0-based), for file stages.</summary>
        public int CurrentFile { get; init; }

        /// <summary>Total number of files to process, for file stages.</summary>
        public int TotalFiles { get; init; }

        /// <summary>Bytes downloaded so far (only for stages with a known byte stream).</summary>
        public long BytesDownloaded { get; init; }

        /// <summary>Total bytes to download, if known; null when unknown.</summary>
        public long? TotalBytes { get; init; }

        /// <summary>Human-readable message for the current step.</summary>
        public string Message { get; init; } = string.Empty;
    }
}
