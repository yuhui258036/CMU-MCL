using System;

namespace CMUmcl.Content
{
    /// <summary>Outcome of the pre-launch auto-update decision for a managed pack instance.</summary>
    public enum PackAutoUpdateAction
    {
        /// <summary>Launch the installed revision directly (no update needed or auto-update disabled).</summary>
        LaunchInstalled,

        /// <summary>Update to the manifest revision before launching.</summary>
        Update,

        /// <summary>Cannot verify the pack revision; fail closed and do not launch.</summary>
        FailClosed,
    }

    /// <summary>
    /// Pure decision helper for the launch-time pack auto-update check. Kept free of UI/IO so
    /// the behavior can be verified with a small harness.
    /// </summary>
    public static class PackAutoUpdate
    {
        /// <summary>Local custom instances are identified by this id prefix and are never auto-updated.</summary>
        public const string LocalIdPrefix = "local:";

        /// <summary>
        /// Decides whether a launch must auto-update first.
        /// </summary>
        /// <param name="instanceId">The instance id (local ids start with <see cref="LocalIdPrefix"/>).</param>
        /// <param name="autoUpdateEnabled">The user's 启动前自动更新整合包 setting.</param>
        /// <param name="installedRevision">The locally installed revision, or null when unknown.</param>
        /// <param name="manifest">The manifest entry, or null when it could not be obtained.</param>
        public static PackAutoUpdateAction Decide(
            string instanceId,
            bool autoUpdateEnabled,
            int? installedRevision,
            ManifestInstance? manifest)
        {
            // Local custom instances are never auto-updated.
            if (instanceId.StartsWith(LocalIdPrefix, StringComparison.Ordinal))
                return PackAutoUpdateAction.LaunchInstalled;

            // Auto-update disabled: launch the installed revision directly.
            if (!autoUpdateEnabled)
                return PackAutoUpdateAction.LaunchInstalled;

            // Auto-update enabled but the manifest cannot be checked: fail closed.
            if (manifest == null)
                return PackAutoUpdateAction.FailClosed;

            // Manifest newer than installed (or installed unknown): update first.
            if (installedRevision == null || manifest.Revision > installedRevision.Value)
                return PackAutoUpdateAction.Update;

            // Manifest <= installed: launch the installed revision.
            return PackAutoUpdateAction.LaunchInstalled;
        }
    }
}
