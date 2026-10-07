using System.Diagnostics;
using static WinTabs.Interop.NativeMethods;

namespace WinTabs.Core;

public sealed record LaunchTarget(string Id, string DisplayName, string FileName, string? Arguments, string[] ExpectedClasses, string Glyph)
{
    public static readonly LaunchTarget Explorer = new("explorer", "파일 탐색기", "explorer.exe", null, new[] { "CabinetWClass" }, "");
    public static readonly LaunchTarget Cmd = new("cmd", "명령 프롬프트", "cmd.exe", null, new[] { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS" }, "");
    public static readonly LaunchTarget PowerShell = new("powershell", "Windows PowerShell", "powershell.exe", null, new[] { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS" }, "");
    public static readonly LaunchTarget Terminal = new("terminal", "터미널", "wt.exe", null, new[] { "CASCADIA_HOSTING_WINDOW_CLASS" }, "");
    public static readonly LaunchTarget Notepad = new("notepad", "메모장", "notepad.exe", null, new[] { "Notepad" }, "");

    public static readonly LaunchTarget[] All = { Explorer, Cmd, PowerShell, Terminal, Notepad };

    public static LaunchTarget? ById(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Best-effort path of the executable, for icon extraction.</summary>
    public string? ResolvePath()
    {
        try
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Id switch
            {
                "explorer" => Path.Combine(sys, "explorer.exe"),
                "cmd" => Path.Combine(sys, "System32", "cmd.exe"),
                "powershell" => Path.Combine(sys, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                "notepad" => Path.Combine(sys, "System32", "notepad.exe"),
                _ => null,
            };
        }
        catch { return null; }
    }
}

public static class Launcher
{
    /// <summary>
    /// Starts the target and waits for a new top-level window to appear. Explorer and the
    /// packaged Notepad hand off to an already running process, so we cannot rely on the
    /// PID; instead we diff the set of top-level windows before/after the launch.
    /// </summary>
    public static async Task<IntPtr> LaunchAndWaitForWindowAsync(LaunchTarget target, Func<IntPtr, bool> isAlreadyGrouped, CancellationToken ct)
    {
        var before = WindowEnumerator.SnapshotAll();
        uint ourPid = (uint)Environment.ProcessId;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target.FileName,
                Arguments = target.Arguments ?? string.Empty,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
        }
        catch
        {
            return IntPtr.Zero;
        }

        var started = Environment.TickCount64;
        const int timeoutMs = 20000;
        IntPtr fallback = IntPtr.Zero;

        while (!ct.IsCancellationRequested && Environment.TickCount64 - started < timeoutMs)
        {
            await Task.Delay(80, ct).ConfigureAwait(true);

            IntPtr exact = IntPtr.Zero, any = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (before.Contains(h) || isAlreadyGrouped(h)) return true;
                if (!WindowEnumerator.IsCandidate(h, ourPid)) return true;
                var cls = GetWindowClass(h);
                if (target.ExpectedClasses.Any(c => c.Equals(cls, StringComparison.OrdinalIgnoreCase)))
                {
                    exact = h;
                    return false;
                }
                if (any == IntPtr.Zero) any = h;
                return true;
            }, IntPtr.Zero);

            if (exact != IntPtr.Zero) return exact;
            if (any != IntPtr.Zero) fallback = any;

            // Only accept a window of an unexpected class after giving the expected one a fair chance.
            if (fallback != IntPtr.Zero && Environment.TickCount64 - started > 4000 && IsWindow(fallback) && IsWindowVisible(fallback))
                return fallback;
        }
        return IntPtr.Zero;
    }
}
