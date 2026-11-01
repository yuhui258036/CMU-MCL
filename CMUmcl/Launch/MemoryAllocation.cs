using System.Runtime.InteropServices;
using CMUmcl.Content;

namespace CMUmcl.Launch;

public readonly record struct MemorySnapshot(long TotalMb, long AvailableMb)
{
    public static MemorySnapshot Read()
    {
        var status = new NativeMemory { Length = (uint)Marshal.SizeOf<NativeMemory>() };
        return GlobalMemoryStatusEx(ref status)
            ? new((long)(status.TotalPhysical / 1048576), (long)(status.AvailablePhysical / 1048576))
            : new(0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMemory
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref NativeMemory status);
}

public static class MemoryAllocation
{
    // Keep a system reserve, account for currently available RAM, and avoid huge automatic heaps.
    public static int Recommend(MemorySnapshot memory, int javaBits = 64)
    {
        if (memory.TotalMb <= 0) return javaBits == 32 ? 1024 : 2048;
        long available = Math.Clamp(memory.AvailableMb, 0, memory.TotalMb);
        long budget = Math.Min(memory.TotalMb / 2, available - Math.Max(768, memory.TotalMb / 8));
        long ceiling = javaBits == 32 ? 1024 : 8192;
        return (int)Math.Clamp(budget / 256 * 256, 512, ceiling);
    }

    public static int Resolve(AppSettings settings, JavaInfo java, MemorySnapshot memory)
    {
        if (settings.AutoMemory) return Recommend(memory, java.Bits);
        int requested = settings.MaxMemoryMb;
        string? error = ValidateManual(requested, memory, java.Bits);
        if (error != null) throw new LaunchException(error);
        return requested;
    }

    public static string? ValidateManual(int requested, MemorySnapshot memory, int javaBits = 64)
    {
        if (requested < 512 || requested > 1048576) return "最大内存需在 512 MB 至 1 TB 之间。";
        if (javaBits == 32 && requested > 1024) return "32 位 Java 最多分配 1024 MB；请改用 64 位 Java。";
        if (memory.TotalMb > 0 && requested > memory.TotalMb) return "分配内存不能超过系统物理内存。";
        return null;
    }
}
