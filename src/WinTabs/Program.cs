using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WinTabs;

/// <summary>
/// Custom entry point so that a second WinTabs.exe launch is redirected to the running
/// instance (which then simply opens another group window) instead of starting a second
/// process with its own hooks and hotkeys.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // WinUI 3 cannot activate its runtime from a network path (\\server\share, \\wsl$\...).
        // Copy ourselves to a local folder and run from there instead of crashing.
        if (RelaunchFromLocalCopyIfOnNetworkPath(args))
            return 0;

        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToExistingInstance())
            return 0;

        try
        {
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            MainWindow.EmergencyReleaseAll();
            MessageBox(IntPtr.Zero,
                "WinTabs 를 시작할 수 없습니다.\n\n" + ex.Message +
                "\n\n앱 폴더가 로컬 디스크(C:\\ 등)에 있는지, 폴더의 파일이 모두 함께 복사되었는지 확인하세요.",
                "WinTabs", 0x10 /* MB_ICONERROR */);
            return 1;
        }
        return 0;
    }

    private static bool RelaunchFromLocalCopyIfOnNetworkPath(string[] args)
    {
        string source = AppContext.BaseDirectory.TrimEnd('\\');
        if (!source.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTabs", "app");
        try
        {
            CopyDirectory(source, target);
            var exe = Path.Combine(target, "WinTabs.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? '"' + a + '"' : a)),
                WorkingDirectory = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero,
                "WinTabs 는 네트워크 경로(\\\\wsl$ 등)에서 직접 실행할 수 없습니다.\n" +
                "로컬 폴더로 복사하는 중 오류가 발생했습니다:\n" + ex.Message +
                "\n\n앱 폴더를 C:\\ 드라이브 등 로컬 디스크로 복사한 뒤 실행하세요.",
                "WinTabs", 0x10);
        }
        return true;
    }

    /// <summary>Copies files that are missing or differ in size/timestamp (keeps the local copy in sync with the share).</summary>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            var src = new FileInfo(file);
            var dst = new FileInfo(dest);
            if (dst.Exists && dst.Length == src.Length && Math.Abs((dst.LastWriteTimeUtc - src.LastWriteTimeUtc).TotalSeconds) < 2)
                continue;
            src.CopyTo(dest, true);
            File.SetLastWriteTimeUtc(dest, src.LastWriteTimeUtc);
        }
    }

    private static bool RedirectToExistingInstance()
    {
        try
        {
            var main = AppInstance.FindOrRegisterForKey("WinTabs.MainInstance");
            if (main.IsCurrent) return false;

            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            var done = CreateEvent(IntPtr.Zero, true, false, null);
            _ = Task.Run(() =>
            {
                try { main.RedirectActivationToAsync(activationArgs).AsTask().Wait(); }
                catch { /* the other instance may be shutting down; just exit */ }
                SetEvent(done);
            });
            // Pump COM messages while waiting, as the redirection round-trips through this STA thread.
            CoWaitForMultipleObjects(0, 5000, 1, new[] { done }, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
