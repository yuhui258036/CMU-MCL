using System;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CMUmcl.Content
{
    /// <summary>
    /// Deterministic SHA-256 over a pack's override entries, matching the server's
    /// <c>FileIndexBuilder.ComputeOverridesSha256</c>.
    /// <para>
    /// Algorithm:
    ///   1. Collect every non-directory ZIP entry whose full name starts with
    ///      "overrides/" or "client-overrides/" (ordinal, case-sensitive).
    ///   2. Sort those entries by full name using ordinal comparison.
    ///   3. Feed into one SHA-256: UTF-8(full name) || 0x00 || entry bytes, for each
    ///      entry in sorted order.
    ///   4. Return the lowercase hex digest.
    /// </para>
    /// </summary>
    public static class OverridesHash
    {
        private const string OverridesPrefix = "overrides/";
        private const string ClientOverridesPrefix = "client-overrides/";

        private static readonly byte[] NulByte = { 0x00 };

        /// <summary>Computes the override hash for the .mrpack archive at <paramref name="zipPath"/>.</summary>
        public static string Compute(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return Compute(zip);
        }

        /// <summary>Computes the override hash for an open archive.</summary>
        public static string Compute(ZipArchive zip)
        {
            var entries = zip.Entries
                .Where(e => !e.FullName.EndsWith('/'))
                .Where(e => e.FullName.StartsWith(OverridesPrefix, StringComparison.Ordinal)
                            || e.FullName.StartsWith(ClientOverridesPrefix, StringComparison.Ordinal))
                .OrderBy(e => e.FullName, StringComparer.Ordinal)
                .ToList();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            foreach (var entry in entries)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(entry.FullName));
                hash.AppendData(NulByte);

                using var stream = entry.Open();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }
}
