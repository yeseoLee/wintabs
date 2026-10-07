using WinTabs.Interop;
using static WinTabs.Interop.NativeMethods;

namespace WinTabs.Core;

public sealed record WindowInfo(IntPtr Hwnd, string Title, string ClassName, uint Pid, string? ProcessPath)
{
    public string ProcessName => ProcessPath != null ? Path.GetFileName(ProcessPath) : string.Empty;
}

public static class WindowEnumerator
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "Xaml_WindowedPopupClass", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Composition.DesktopWindowContentBridge", "WinUIDesktopWin32WindowClass",
    };

    /// <summary>All top-level HWNDs (visible or not) – used to detect windows created by a launch.</summary>
    public static HashSet<IntPtr> SnapshotAll()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) => { set.Add(h); return true; }, IntPtr.Zero);
        return set;
    }

    /// <summary>Top-level, visible, titled, unowned application windows in z-order (topmost first).</summary>
    public static List<WindowInfo> ListCandidates(Func<IntPtr, bool>? exclude = null)
    {
        var list = new List<WindowInfo>();
        uint ourPid = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (IsCandidate(h, ourPid) && (exclude == null || !exclude(h)))
            {
                var pid = GetWindowPid(h);
                list.Add(new WindowInfo(h, GetWindowTitle(h), GetWindowClass(h), pid, GetProcessPath(pid)));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool IsCandidate(IntPtr h, uint ourPid)
    {
        if (!IsWindowVisible(h) || IsCloaked(h)) return false;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        long ex = GetExStyle(h);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (GetWindowTextLength(h) == 0) return false;
        if (GetWindowPid(h) == ourPid) return false;
        var cls = GetWindowClass(h);
        if (IgnoredClasses.Contains(cls)) return false;
        return true;
    }

    public static WindowInfo Describe(IntPtr h)
    {
        var pid = GetWindowPid(h);
        return new WindowInfo(h, GetWindowTitle(h), GetWindowClass(h), pid, GetProcessPath(pid));
    }
}
