// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using CmlLib.Core;
using CmlLib.Core.Files;

namespace CMUmcl.Launch;

/// <summary>
/// Fast pre-launch check of an already provisioned version: every file the version needs
/// (client, libraries, natives, asset objects) must exist with its recorded size.
/// Hashes are not recomputed here; a size or presence mismatch triggers the normal repair path.
/// </summary>
internal static class InstallIntegrity
{
    internal static async Task<int> CountBrokenAsync(MinecraftLauncher launcher, string versionName, CancellationToken ct)
    {
        IEnumerable<GameFile> files = await launcher.ExtractFiles(versionName, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            int broken = 0;
            foreach (GameFile file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (IsBroken(file.Path, file.Size)) broken++;
            }
            return broken;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Missing files are broken; a known size must match exactly. Entries without a path are skipped.</summary>
    internal static bool IsBroken(string? path, long expectedSize)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var info = new FileInfo(path);
        return !info.Exists || (expectedSize > 0 && info.Length != expectedSize);
    }
}
