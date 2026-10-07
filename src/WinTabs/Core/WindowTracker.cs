using static WinTabs.Interop.NativeMethods;

namespace WinTabs.Core;

/// <summary>
/// System-wide WinEvent hook (out of context, delivered on the UI thread's message loop).
/// Group windows subscribe and filter for the HWNDs they care about.
/// </summary>
public sealed class WindowTracker
{
    public static WindowTracker Instance { get; } = new();

    public event Action<IntPtr>? Destroyed;
    public event Action<IntPtr>? Foreground;
    public event Action<IntPtr>? NameChanged;
    public event Action<IntPtr>? LocationChanged;
    public event Action<IntPtr>? MinimizeStart;
    public event Action<IntPtr>? Shown;

    private readonly WinEventDelegate _callback; // keep alive for the unmanaged hook
    private IntPtr _systemHook, _objectHook;

    private WindowTracker()
    {
        _callback = OnWinEvent;
    }

    public void Start()
    {
        if (_systemHook != IntPtr.Zero) return;
        _systemHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        _objectHook = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public void Stop()
    {
        if (_systemHook != IntPtr.Zero) UnhookWinEvent(_systemHook);
        if (_objectHook != IntPtr.Zero) UnhookWinEvent(_objectHook);
        _systemHook = _objectHook = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        try
        {
            switch (eventType)
            {
                case EVENT_OBJECT_DESTROY: Destroyed?.Invoke(hwnd); break;
                case EVENT_SYSTEM_FOREGROUND: Foreground?.Invoke(hwnd); break;
                case EVENT_OBJECT_NAMECHANGE: NameChanged?.Invoke(hwnd); break;
                case EVENT_OBJECT_LOCATIONCHANGE: LocationChanged?.Invoke(hwnd); break;
                case EVENT_SYSTEM_MINIMIZESTART: MinimizeStart?.Invoke(hwnd); break;
                case EVENT_OBJECT_SHOW: Shown?.Invoke(hwnd); break;
            }
        }
        catch
        {
            // Never let an exception escape into the hook dispatcher.
        }
    }
}
