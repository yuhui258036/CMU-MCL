using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Launch
{
    /// <summary>
    /// Polls a launched game process until its visible main window appears, the process
    /// exits, or a timeout elapses. Used to avoid reporting "游戏已启动" before the
    /// actual Minecraft window has opened.
    /// </summary>
    public static class GameWindowWaiter
    {
        /// <summary>How long to keep polling for a game window before giving up.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

        private const int DefaultPollIntervalMilliseconds = 250;

        /// <summary>
        /// Waits for <paramref name="process"/> to expose a visible main window.
        /// Returns <c>true</c> once a window is detected; returns <c>false</c> if the
        /// timeout elapses while the process is still running; throws
        /// <see cref="LaunchException"/> if the process exits before a window appears.
        /// </summary>
        public static Task<bool> WaitForWindowAsync(Process process, CancellationToken ct = default)
            => WaitForWindowAsync(process, DefaultTimeout, DefaultPollIntervalMilliseconds, ct);

        /// <summary>
        /// Test-friendly overload that allows overriding the timeout and poll interval.
        /// </summary>
        public static async Task<bool> WaitForWindowAsync(
            Process process,
            TimeSpan timeout,
            int pollIntervalMilliseconds,
            CancellationToken ct = default)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            if (pollIntervalMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(pollIntervalMilliseconds));

            DateTime deadline = DateTime.UtcNow.Add(timeout);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                bool exited;
                try
                {
                    exited = process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    // The process handle is no longer available (exited mid-poll).
                    return false;
                }

                if (exited)
                    throw new LaunchException($"游戏进程已退出（退出码 {SafeExitCode(process)}）。请查看日志了解启动失败原因。");

                IntPtr windowHandle;
                try
                {
                    process.Refresh();
                    windowHandle = process.MainWindowHandle;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                catch (Win32Exception)
                {
                    return false;
                }

                if (windowHandle != IntPtr.Zero)
                    return true;

                if (DateTime.UtcNow >= deadline)
                    return false;

                await Task.Delay(pollIntervalMilliseconds, ct).ConfigureAwait(false);
            }
        }

        private static int SafeExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }
    }
}
