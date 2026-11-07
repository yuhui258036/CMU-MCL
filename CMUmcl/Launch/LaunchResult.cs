using System.Diagnostics;
using CMUmcl.Auth;

namespace CMUmcl.Launch
{
    /// <summary>Result of a successful launch.</summary>
    public sealed class LaunchResult
    {
        /// <summary>The started game process.</summary>
        public Process Process { get; init; } = null!;

        /// <summary>The (possibly refreshed) session used to launch.</summary>
        public AuthSession Session { get; init; } = null!;

        /// <summary>The resolved version id that was launched.</summary>
        public string VersionName { get; init; } = string.Empty;

        /// <summary>
        /// True when a visible game window was detected before the wait timed out.
        /// False means the process is still running but no window appeared in time.
        /// </summary>
        public bool WindowDetected { get; init; }

        /// <summary>Output classification for the started process, used to explain an abnormal exit.</summary>
        public GameProcessDiagnostics? Diagnostics { get; init; }

        /// <summary>The Java runtime the game was started with.</summary>
        public string? JavaPath { get; init; }
        public int? JavaMajor { get; init; }
    }
}
