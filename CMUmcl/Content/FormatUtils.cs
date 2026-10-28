using System;

namespace CMUmcl.Content
{
    /// <summary>Small shared formatting helpers.</summary>
    public static class FormatUtils
    {
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "0 B";
            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
        }
    }
}
