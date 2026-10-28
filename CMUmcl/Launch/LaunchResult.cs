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
    }
}
