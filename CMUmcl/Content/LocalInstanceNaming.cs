using System;
using System.Collections.Generic;
using System.IO;

namespace CMUmcl.Content
{
    /// <summary>
    /// Pure helpers for turning a user-supplied display name into a folder-safe directory name
    /// and for disambiguating collisions with YSUMCL-style <c> (2)</c>, <c> (3)</c> suffixes.
    /// Kept free of I/O so it can be tested in isolation without touching real instances.
    /// </summary>
    internal static class LocalInstanceNaming
    {
        /// <summary>
        /// Reduces a display name to a single safe folder name (no separators, no invalid
        /// characters, no reserved device names). Falls back to <c>instance</c> when the result
        /// would otherwise be empty or a traversal segment.
        /// </summary>
        public static string SanitizeFolderName(string? displayName)
        {
            string s = (displayName ?? string.Empty).Trim();

            if (s.Length == 0)
                return "instance";

            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '-');

            // Windows forbids trailing dots/spaces in file/folder names.
            s = s.TrimEnd('.', ' ');

            if (s.Length == 0 || s == "." || s == "..")
                return "instance";

            if (IsReservedDeviceName(s))
                s = "_" + s;

            return s;
        }

        /// <summary>
        /// Picks a collision-free folder name and the matching (auto-suffixed) display name.
        /// The first attempt is unsuffixed; collisions append <c> (2)</c>, <c> (3)</c>, … to both
        /// the folder and the display name. Comparison is case-insensitive.
        /// </summary>
        public static (string Folder, string DisplayName) ResolveUniqueName(
            string baseFolder,
            string baseDisplay,
            IReadOnlyCollection<string> takenNames)
        {
            string folder = baseFolder;
            string display = baseDisplay;
            int n = 2;

            while (ContainsName(takenNames, folder))
            {
                folder = baseFolder + " (" + n + ")";
                display = baseDisplay + " (" + n + ")";
                n++;
            }

            return (folder, display);
        }

        private static bool ContainsName(IReadOnlyCollection<string> takenNames, string name)
        {
            foreach (string taken in takenNames)
            {
                if (string.Equals(taken, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsReservedDeviceName(string name)
        {
            string upper = name.ToUpperInvariant();
            if (upper is "CON" or "PRN" or "AUX" or "NUL")
                return true;

            // COM1-9 / LPT1-9 (COM0/LPT0 are not reserved on Windows).
            if (upper.Length == 4
                && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && char.IsDigit(upper[3])
                && upper[3] != '0')
            {
                return true;
            }

            return false;
        }
    }
}
