using WinTabs.Interop;
using static WinTabs.Interop.NativeMethods;

namespace WinTabs.Core;

/// <summary>
/// A foreign top-level window that has been pulled into a group. We never re-parent it
/// (cross-process SetParent is fragile); instead the window is made *owned* by the group
/// window so it always floats directly above it, its caption/thick frame is removed, and it
/// is moved to exactly cover the group's content area below the tab strip. The window must
/// not overlap the strip: DWM stops presenting the group window's XAML content when another
/// window covers its whole rectangle, even if that window has a region. Inactive tabs are
/// simply hidden.
/// </summary>
public sealed class CapturedWindow
{
    public IntPtr Hwnd { get; }
    public uint Pid { get; }
    public string ClassName { get; }
    public string? ProcessPath { get; }
    public string ProcessName => ProcessPath != null ? Path.GetFileName(ProcessPath) : string.Empty;
    public AppProfile Profile { get; }

    public string Title { get; private set; }
    public bool IsAttached { get; private set; }
    public IntPtr Owner { get; private set; }

    // Saved state for a faithful detach.
    private long _origStyle, _origExStyle;
    private IntPtr _origOwner;
    private RECT _origRect;
    private bool _origMaximized;

    // Last applied geometry (window coordinates, physical pixels) to avoid redundant region churn.
    private RECT _lastRect;
    private int _lastRadius = -1;

    public CapturedWindow(IntPtr hwnd, AppProfile profile)
    {
        Hwnd = hwnd;
        Pid = GetWindowPid(hwnd);
        ClassName = GetWindowClass(hwnd);
        ProcessPath = GetProcessPath(Pid);
        Title = GetWindowTitle(hwnd);
        Profile = profile;
    }

    public string RefreshTitle()
    {
        Title = GetWindowTitle(Hwnd);
        return Title;
    }

    public bool IsAlive => IsWindow(Hwnd);

    public void Attach(IntPtr owner)
    {
        if (IsAttached) { SetOwner(owner); return; }

        _origStyle = GetStyle(Hwnd);
        _origExStyle = GetExStyle(Hwnd);
        _origOwner = GetWindowLongPtr(Hwnd, GWLP_HWNDPARENT);
        _origMaximized = IsZoomed(Hwnd);
        if (IsIconic(Hwnd) || _origMaximized) ShowWindow(Hwnd, SW_RESTORE);
        GetWindowRect(Hwnd, out _origRect);

        if (Profile.StripFrame)
        {
            long strip = WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
            // WS_CAPTION == WS_BORDER | WS_DLGFRAME. Apps with a custom title bar (Notepad) need it kept,
            // see AppProfile.KeepCaption; without MIN/MAXIMIZEBOX they still hide their own caption buttons.
            if (!Profile.KeepsCaption(ClassName)) strip |= WS_CAPTION | WS_DLGFRAME | WS_BORDER;
            SetStyle(Hwnd, _origStyle & ~strip);
        }
        long ex = _origExStyle & ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE | WS_EX_APPWINDOW);
        SetExStyle(Hwnd, ex);

        SetOwner(owner);

        SetDwmInt(Hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND);
        SetDwmColor(Hwnd, DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE);
        // Do NOT disable DWM non-client rendering (DWMWA_NCRENDERING_POLICY). Apps with a custom
        // title bar (Win11 Notepad) re-extend their frame on every WM_ACTIVATE, and with NC rendering
        // off that leaves their content shifted down behind a strip of DWM caption text whenever the
        // group window takes the foreground. The window region below already hides the DWM frame.

        SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        IsAttached = true;
        _lastRadius = -1;
    }

    /// <summary>Makes the window owned by <paramref name="owner"/> (so it stays above it and follows minimize).</summary>
    public void SetOwner(IntPtr owner)
    {
        Owner = owner;
        SetWindowLongPtr(Hwnd, GWLP_HWNDPARENT, owner);
    }

    /// <summary>Positions the window over the content rectangle (screen coordinates, physical pixels).</summary>
    public void Layout(RECT content, double scale)
    {
        if (!IsAttached || !IsAlive) return;
        int radius = (int)Math.Round(8 * scale);

        var target = content;
        GetWindowRect(Hwnd, out var current);

        bool sizeChanged = target.Width != _lastRect.Width || target.Height != _lastRect.Height;
        if (!current.Equals(target))
        {
            Log.Write($"layout {Hwnd:X} {current} -> {target}");
            SetWindowPos(Hwnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        if (sizeChanged || radius != _lastRadius)
        {
            Log.Write($"region {Hwnd:X} {target.Width}x{target.Height} r={radius}");
            ApplyRegion(target.Width, target.Height, radius);
            _lastRadius = radius;
        }
        _lastRect = target;
    }

    /// <summary>Rounded bottom corners (to match the group window's Win11 corners), square top.</summary>
    private void ApplyRegion(int w, int h, int radius)
    {
        IntPtr rgn;
        if (radius > 0)
        {
            rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, radius * 2, radius * 2);
            var top = CreateRectRgn(0, 0, w, Math.Min(h, radius));
            CombineRgn(rgn, rgn, top, RGN_OR);
            DeleteObject(top);
        }
        else
        {
            rgn = CreateRectRgn(0, 0, w, h);
        }
        // The system takes ownership of the region handle.
        SetWindowRgn(Hwnd, rgn, true);
    }

    public void Show()
    {
        if (!IsAlive) return;
        if (IsIconic(Hwnd)) ShowWindow(Hwnd, SW_RESTORE);
        ShowWindow(Hwnd, SW_SHOWNA);
    }

    public void Hide()
    {
        if (IsAlive) ShowWindow(Hwnd, SW_HIDE);
    }

    public void Focus()
    {
        if (!IsAlive) return;
        SetForegroundWindow(Hwnd);
    }

    public void RequestClose()
    {
        if (IsAlive) PostMessage(Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Restores frame, owner and region, and places the window at <paramref name="placeAt"/> (or its original rect).</summary>
    public void Detach(RECT? placeAt, bool activate)
    {
        if (!IsAttached) return;
        IsAttached = false;
        if (!IsAlive) return;

        SetWindowRgn(Hwnd, IntPtr.Zero, true);
        SetWindowLongPtr(Hwnd, GWLP_HWNDPARENT, _origOwner);
        SetStyle(Hwnd, _origStyle & ~WS_MINIMIZE & ~WS_MAXIMIZE);
        SetExStyle(Hwnd, _origExStyle);
        SetDwmInt(Hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DEFAULT);
        SetDwmColor(Hwnd, DWMWA_BORDER_COLOR, DWMWA_COLOR_DEFAULT);

        var r = placeAt ?? _origRect;
        if (r.Width < 200 || r.Height < 120) r = _origRect;
        SetWindowPos(Hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        ShowWindow(Hwnd, _origMaximized ? SW_MAXIMIZE : SW_SHOWNA);
        _lastRadius = -1;
        _lastRect = default;
        if (activate) SetForegroundWindow(Hwnd);
    }
}
