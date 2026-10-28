using System;

namespace CMUmcl.Launch
{
    /// <summary>Progress report for a launch operation.</summary>
    public sealed class LaunchProgress
    {
        /// <summary>Current stage description (e.g. 校验登录 / 安装原版 / 安装 Fabric / 启动).</summary>
        public string Stage { get; init; } = string.Empty;

        /// <summary>Human-readable message for the current step.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>Completed file/task count (0 when not applicable).</summary>
        public int ProgressedTasks { get; init; }

        /// <summary>Total file/task count (0 when unknown).</summary>
        public int TotalTasks { get; init; }

        /// <summary>Bytes processed (0 when not applicable).</summary>
        public long ProgressedBytes { get; init; }

        /// <summary>Total bytes (0 when unknown).</summary>
        public long TotalBytes { get; init; }

        /// <summary>Selected Java major version (informational; e.g. "26").</summary>
        public string? JavaVersion { get; init; }

        /// <summary>Selected Java executable path (informational).</summary>
        public string? JavaPath { get; init; }
    }
}
