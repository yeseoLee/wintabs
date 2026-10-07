using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;
using WinRT;
using WinRT.Interop;
using WinTabs.Core;
using WinTabs.Interop;
using static WinTabs.Interop.NativeMethods;

namespace WinTabs;

/// <summary>
/// One "group" window: a File Explorer-styled tab strip in the title bar, Mica background, and a
/// content area that the currently selected application window is laid over.
/// </summary>
public sealed partial class MainWindow : Window
{
    public static readonly List<MainWindow> All = new();
    public static MainWindow? LastActive { get; private set; }

    private const string DragKey = "WinTabs.Hwnd";
    private const int HotkeyId = 0x5754;
    private const int TabHotkeyBase = 0x5760; // TabHotkeyBase + n = Ctrl+n (1..9), registered only while this group is in front
    private static MainWindow? _tabHotkeyOwner;
    private const int MinWidthDips = 480, MinHeightDips = 320;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly SubclassProc _subclassProc;
    private readonly WindowsSystemDispatcherQueueHelper _dispatcherQueueHelper = new();
    private MicaController? _mica;
    private SystemBackdropConfiguration? _backdropConfig;

    private CapturedWindow? _active;
    private bool _groupActive = true;
    private bool _closing;
    private bool _tornDown;
    private bool _hotkeyOwner;
    private bool _suppressSelection;
    private bool _dragging; // one of our tabs is inside an OLE drag (TabDragStarting..TabDragCompleted)
    private readonly Dictionary<IntPtr, IntPtr> _hostIcons = new(); // hwnd -> HICON copy for the taskbar/Alt-Tab icon
    private readonly List<CancellationTokenSource> _pendingLaunches = new();

    public IntPtr Hwnd => _hwnd;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        All.Add(this);
        LastActive = this;

        SetupTitleBar();
        SetupBackdrop();
        ApplyTheme();

        double scale = GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32((int)(App.Settings.WindowWidth * scale), (int)(App.Settings.WindowHeight * scale)));
        _appWindow.Title = "WinTabs";

        _subclassProc = WndProcSubclass;
        SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1, UIntPtr.Zero);
        EnsureHotkey();

        Activated += OnActivated;
        Closed += OnClosed;
        _appWindow.Changed += OnAppWindowChanged;
        _appWindow.Closing += OnAppWindowClosing;
        RootGrid.Loaded += (_, _) => { UpdateTitleBarInsets(); LayoutActive(); };
        RootGrid.ActualThemeChanged += (_, _) => ApplyTheme();

        var tracker = WindowTracker.Instance;
        tracker.Destroyed += OnWindowDestroyed;
        tracker.Foreground += OnForeground;
        tracker.NameChanged += OnNameChanged;
        tracker.LocationChanged += OnLocationChanged;
        tracker.MinimizeStart += OnMinimizeStart;

        UpdateEmptyState();
    }

    // ------------------------------------------------------------------ setup

    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = _appWindow.TitleBar;
            tb.PreferredHeightOption = TitleBarHeightOption.Standard;
            tb.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        }
    }

    private void SetupBackdrop()
    {
        if (!MicaController.IsSupported()) return;
        _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        _mica = new MicaController();
        _mica.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _mica.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private bool IsDark => RootGrid.ActualTheme == ElementTheme.Dark;

    private void ApplyTheme()
    {
        bool dark = IsDark;
        if (_backdropConfig != null)
            _backdropConfig.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var tb = _appWindow.TitleBar;
        var fg = dark ? Colors.White : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = dark ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x9E, 0x00, 0x00, 0x00);
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x09, 0x00, 0x00, 0x00);
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x06, 0x00, 0x00, 0x00);
        UpdateInactiveButtonColor();
    }

    private void UpdateInactiveButtonColor()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var tb = _appWindow.TitleBar;
        tb.ButtonInactiveForegroundColor = _groupActive
            ? tb.ButtonForegroundColor
            : (IsDark ? Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x5C, 0x00, 0x00, 0x00));
    }

    private void UpdateTitleBarInsets()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || RootGrid.XamlRoot == null) return;
        double scale = RootGrid.XamlRoot.RasterizationScale;
        var tb = _appWindow.TitleBar;
        RightPaddingColumn.Width = new GridLength(Math.Max(0, tb.RightInset / scale));
        LeftPaddingColumn.Width = new GridLength(Math.Max(0, tb.LeftInset / scale));
    }

    private void EnsureHotkey()
    {
        if (!App.Settings.GrabHotkey) return;
        if (All.Any(w => w._hotkeyOwner)) return;
        _hotkeyOwner = RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'T');
    }

    private IntPtr WndProcSubclass(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        switch (msg)
        {
            case WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                GrabForegroundWindow();
                return IntPtr.Zero;

            case WM_HOTKEY when wParam.ToInt32() > TabHotkeyBase && wParam.ToInt32() <= TabHotkeyBase + 9:
                SelectTabByNumber(wParam.ToInt32() - TabHotkeyBase);
                return IntPtr.Zero;

            case WM_GETMINMAXINFO:
            {
                var result = DefSubclassProc(hWnd, msg, wParam, lParam);
                double scale = GetDpiForWindow(hWnd) / 96.0;
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.X = (int)(MinWidthDips * scale);
                mmi.ptMinTrackSize.Y = (int)(MinHeightDips * scale);
                Marshal.StructureToPtr(mmi, lParam, false);
                return result;
            }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void RunStartupAction()
    {
        if (string.Equals(App.Settings.StartupTab, "explorer", StringComparison.OrdinalIgnoreCase))
            LaunchInto(LaunchTarget.Explorer);
        else if (LaunchTarget.ById(App.Settings.StartupTab) is { } target)
            LaunchInto(target);
    }

    // ------------------------------------------------------------------ lookup helpers

    private IEnumerable<TabViewItem> TabItems => Tabs.TabItems.OfType<TabViewItem>();

    private TabViewItem? FindTab(IntPtr hwnd) =>
        TabItems.FirstOrDefault(t => t.Tag is CapturedWindow cw && cw.Hwnd == hwnd);

    private TabViewItem? FindTab(CapturedWindow cw) => TabItems.FirstOrDefault(t => ReferenceEquals(t.Tag, cw));

    public static MainWindow? FindHostOf(IntPtr hwnd, out TabViewItem? tab)
    {
        foreach (var w in All)
        {
            tab = w.FindTab(hwnd);
            if (tab != null) return w;
        }
        tab = null;
        return null;
    }

    public static bool IsGroupedAnywhere(IntPtr hwnd) => FindHostOf(hwnd, out _) != null || All.Any(w => w._hwnd == hwnd);

    /// <summary>True for this group window, any grouped app window, and any popup/dialog owned by them.</summary>
    private bool IsOurs(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == _hwnd || FindTab(hwnd) != null) return true;
        var rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
        if (rootOwner == _hwnd) return true;
        // A dialog owned by a grouped window whose owner chain stops at that window.
        return rootOwner != IntPtr.Zero && FindTab(rootOwner) != null;
    }

    private double Scale => RootGrid.XamlRoot?.RasterizationScale ?? GetDpiForWindow(_hwnd) / 96.0;

    private bool TryGetContentRect(out RECT rect)
    {
        rect = default;
        if (ContentHost.XamlRoot == null || ContentHost.ActualWidth <= 0 || ContentHost.ActualHeight <= 0) return false;
        if (IsIconic(_hwnd)) return false;
        double scale = ContentHost.XamlRoot.RasterizationScale;
        var p = ContentHost.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var origin = ClientOrigin(_hwnd);
        int left = origin.X + (int)Math.Round(p.X * scale);
        int top = origin.Y + (int)Math.Round(p.Y * scale);
        int w = (int)Math.Round(ContentHost.ActualWidth * scale);
        int h = (int)Math.Round(ContentHost.ActualHeight * scale);
        rect = new RECT(left, top, left + w, top + h);
        return true;
    }

    // ------------------------------------------------------------------ tabs

    /// <summary>Pulls an existing top-level window into this group (moving it from another group if needed).</summary>
    public CapturedWindow? AddWindow(IntPtr hwnd, bool activate = true, int index = -1)
    {
        if (_closing || !IsWindow(hwnd)) return null;
        hwnd = GetAncestor(hwnd, GA_ROOT);

        var host = FindHostOf(hwnd, out var existingTab);
        if (host != null && existingTab != null)
        {
            if (host == this)
            {
                Tabs.SelectedItem = existingTab;
                return (CapturedWindow)existingTab.Tag;
            }
            var moved = host.TransferOut(existingTab);
            return AdoptWindow(moved, index, activate);
        }

        var info = WindowEnumerator.Describe(hwnd);
        var profile = App.Settings.GetProfile(info.ProcessName, info.ClassName);
        var cw = new CapturedWindow(hwnd, profile);
        Log.Write($"attach {hwnd:X} {cw.ClassName} {cw.ProcessName} '{cw.Title}'");
        cw.Attach(_hwnd);

        var tab = CreateTab(cw);
        InsertTab(tab, index);
        if (activate) Tabs.SelectedItem = tab;
        else if (!ReferenceEquals(Tabs.SelectedItem, tab)) cw.Hide();
        return cw;
    }

    /// <summary>Takes a window that is already attached (coming from another group window).</summary>
    private CapturedWindow AdoptWindow(CapturedWindow cw, int index, bool activate)
    {
        cw.SetOwner(_hwnd);
        var tab = CreateTab(cw);
        InsertTab(tab, index);
        if (activate) Tabs.SelectedItem = tab;
        else if (!ReferenceEquals(Tabs.SelectedItem, tab)) cw.Hide();
        if (ReferenceEquals(Tabs.SelectedItem, tab)) ActivateTab(cw); // selection may not have changed
        return cw;
    }

    /// <summary>Removes the tab but keeps the window attached, for a move to another group window.</summary>
    private CapturedWindow TransferOut(TabViewItem tab)
    {
        var cw = (CapturedWindow)tab.Tag;
        cw.Hide();
        RemoveTab(tab);
        return cw;
    }

    private void InsertTab(TabViewItem tab, int index)
    {
        if (index < 0 || index > Tabs.TabItems.Count) Tabs.TabItems.Add(tab);
        else Tabs.TabItems.Insert(index, tab);
        UpdateEmptyState();
    }

    private TabViewItem CreateTab(CapturedWindow cw)
    {
        var tab = new TabViewItem { Tag = cw, IsClosable = true };
        tab.ContextFlyout = BuildTabMenu(tab);
        RefreshTab(tab, cw);
        return tab;
    }

    private void RefreshTab(TabViewItem tab, CapturedWindow cw)
    {
        cw.RefreshTitle();
        tab.Header = CleanTitle(cw);
        ToolTipService.SetToolTip(tab, cw.Title);

        var hIcon = IconHelper.GetIconCopyForWindow(cw.Hwnd, cw.ProcessPath);
        if (hIcon != IntPtr.Zero)
        {
            var src = IconHelper.ToImageSource(hIcon);
            tab.IconSource = src != null ? new ImageIconSource { ImageSource = src } : FallbackIcon(cw);
            if (_hostIcons.TryGetValue(cw.Hwnd, out var old) && old != IntPtr.Zero) DestroyIcon(old);
            _hostIcons[cw.Hwnd] = hIcon;
        }
        else
        {
            tab.IconSource = FallbackIcon(cw);
        }
    }

    private static IconSource FallbackIcon(CapturedWindow cw)
    {
        string glyph = cw.ClassName switch
        {
            "CabinetWClass" => "",
            "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" => "",
            "Notepad" => "",
            _ => "",
        };
        return new FontIconSource { Glyph = glyph };
    }

    private static readonly string[] AppSuffixes =
    {
        " - 파일 탐색기", " - File Explorer", " - 메모장", " - Notepad", " - Windows 메모장", " - Windows Notepad",
    };

    private static string CleanTitle(CapturedWindow cw)
    {
        var title = cw.Title;
        foreach (var suffix in AppSuffixes)
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return title[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(title))
            title = LaunchTargetFor(cw)?.DisplayName ?? Path.GetFileNameWithoutExtension(cw.ProcessName);
        return title;
    }

    private static LaunchTarget? LaunchTargetFor(CapturedWindow cw) => cw.ClassName switch
    {
        "CabinetWClass" => LaunchTarget.Explorer,
        "Notepad" => LaunchTarget.Notepad,
        "CASCADIA_HOSTING_WINDOW_CLASS" => LaunchTarget.Terminal,
        "ConsoleWindowClass" => LaunchTarget.Cmd,
        _ => null,
    };

    private void ActivateTab(CapturedWindow? cw)
    {
        if (_closing) return;
        if (_dragging)
        {
            // While a tab is being dragged, TabView removes and re-inserts the dragged item to reorder
            // it, flipping the selection to a neighbour and back from inside the drop callback. Showing,
            // hiding or focusing the (cross-process) app windows from there blocks the drag-drop thread
            // and the app freezes, so leave the windows alone and resync once the drag has completed.
            Log.Write($"activate tab {(cw == null ? "none" : cw.Hwnd.ToString("X"))} deferred (dragging)");
            return;
        }
        Log.Write($"activate tab {(cw == null ? "none" : cw.Hwnd.ToString("X"))}");
        var previous = _active;
        _active = cw;

        if (previous != null && !ReferenceEquals(previous, cw) && FindTab(previous) != null)
            previous.Hide();

        if (cw != null)
        {
            LayoutActive();
            cw.Show();
            if (_groupActive) cw.Focus();
        }
        UpdateHostIdentity();
        UpdateEmptyState();
    }

    private void LayoutActive()
    {
        if (_active == null || _closing) return;
        if (!TryGetContentRect(out var rect)) return;
        _active.Layout(rect, Scale);
    }

    private void UpdateHostIdentity()
    {
        if (_active != null)
        {
            _appWindow.Title = string.IsNullOrWhiteSpace(_active.Title) ? "WinTabs" : _active.Title;
            if (_hostIcons.TryGetValue(_active.Hwnd, out var icon) && icon != IntPtr.Zero)
            {
                try { _appWindow.SetIcon(Win32Interop.GetIconIdFromIcon(icon)); } catch { }
            }
        }
        else
        {
            _appWindow.Title = "WinTabs";
        }
    }

    private void UpdateEmptyState()
    {
        bool empty = Tabs.TabItems.Count == 0;
        bool pending = Tabs.SelectedItem is TabViewItem t && t.Tag == null;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = pending;
    }

    /// <summary>Removes a tab from the strip. The window itself is untouched.</summary>
    private void RemoveTab(TabViewItem tab)
    {
        bool wasSelected = ReferenceEquals(Tabs.SelectedItem, tab);
        int index = Tabs.TabItems.IndexOf(tab);
        var cw = tab.Tag as CapturedWindow;

        _suppressSelection = true;
        try { Tabs.TabItems.Remove(tab); }
        finally { _suppressSelection = false; }

        if (cw != null)
        {
            if (ReferenceEquals(_active, cw)) _active = null;
            if (_hostIcons.Remove(cw.Hwnd, out var icon) && icon != IntPtr.Zero) DestroyIcon(icon);
        }

        if (Tabs.TabItems.Count == 0)
        {
            ActivateTab(null);
            if (App.Settings.CloseWhenEmpty && !_closing) Close();
            return;
        }

        if (wasSelected || Tabs.SelectedItem == null)
        {
            int next = Math.Clamp(index, 0, Tabs.TabItems.Count - 1);
            if (Tabs.SelectedIndex == next) Tabs_SelectionChanged(Tabs, null!);
            else Tabs.SelectedIndex = next;
        }
        UpdateEmptyState();
    }

    private void DetachTab(TabViewItem tab, RECT? placeAt, bool activate)
    {
        if (tab.Tag is not CapturedWindow cw) { RemoveTab(tab); return; }
        if (placeAt == null && TryGetContentRect(out var r)) placeAt = r;
        RemoveTab(tab);
        cw.Detach(placeAt, activate);
    }

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is CapturedWindow cw)
            cw.RequestClose(); // the tab goes away when the window is destroyed
        else
            RemoveTab(tab);    // pending launch
    }

    private void LaunchInto(LaunchTarget target) => _ = LaunchIntoAsync(target);

    public async Task LaunchIntoAsync(LaunchTarget target)
    {
        if (_closing) return;
        Log.Write($"launch {target.Id}");
        var pending = new TabViewItem
        {
            Header = target.DisplayName,
            IconSource = LaunchIconSource(target),
            IsClosable = true,
            Tag = null,
        };
        InsertTab(pending, -1);
        Tabs.SelectedItem = pending;

        var cts = new CancellationTokenSource();
        _pendingLaunches.Add(cts);
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = await Launcher.LaunchAndWaitForWindowAsync(target, IsGroupedAnywhere, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally { _pendingLaunches.Remove(cts); }

        if (_closing || !Tabs.TabItems.Contains(pending)) return;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            Log.Write($"launch {target.Id}: no window found");
            RemoveTab(pending);
            return;
        }

        var info = WindowEnumerator.Describe(hwnd);
        var profile = App.Settings.GetProfile(info.ProcessName, info.ClassName);
        var cw = new CapturedWindow(hwnd, profile);
        Log.Write($"attach (launched {target.Id}) {hwnd:X} {cw.ClassName} {cw.ProcessName} '{cw.Title}'");
        cw.Attach(_hwnd);

        pending.Tag = cw;
        pending.ContextFlyout = BuildTabMenu(pending);
        RefreshTab(pending, cw);

        if (ReferenceEquals(Tabs.SelectedItem, pending)) ActivateTab(cw);
        else cw.Hide();
    }

    private static IconSource LaunchIconSource(LaunchTarget target)
    {
        var h = IconHelper.ExtractExeIcon(target.ResolvePath());
        if (h != IntPtr.Zero)
        {
            var src = IconHelper.ToImageSource(h);
            DestroyIcon(h);
            if (src != null) return new ImageIconSource { ImageSource = src };
        }
        return new FontIconSource { Glyph = target.Glyph };
    }

    private static IconElement LaunchIconElement(LaunchTarget target)
    {
        var h = IconHelper.ExtractExeIcon(target.ResolvePath());
        if (h != IntPtr.Zero)
        {
            var src = IconHelper.ToImageSource(h);
            DestroyIcon(h);
            if (src != null) return new ImageIcon { Source = src };
        }
        return new FontIcon { Glyph = target.Glyph };
    }

    // ------------------------------------------------------------------ menus

    private MenuFlyout BuildAddMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };

        foreach (var target in LaunchTarget.All)
        {
            var item = new MenuFlyoutItem { Text = target.DisplayName, Icon = LaunchIconElement(target) };
            var captured = target;
            item.Click += (_, _) => LaunchInto(captured);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var grab = new MenuFlyoutSubItem { Text = "열려 있는 창 가져오기", Icon = new FontIcon { Glyph = "" } };
        var candidates = WindowEnumerator.ListCandidates(IsGroupedAnywhere);
        if (candidates.Count == 0)
        {
            grab.Items.Add(new MenuFlyoutItem { Text = "(가져올 수 있는 창이 없습니다)", IsEnabled = false });
        }
        foreach (var c in candidates.Take(25))
        {
            var text = c.Title.Length > 60 ? c.Title[..57] + "…" : c.Title;
            var item = new MenuFlyoutItem { Text = text };
            var hIcon = IconHelper.GetIconCopyForWindow(c.Hwnd, c.ProcessPath);
            if (hIcon != IntPtr.Zero)
            {
                var src = IconHelper.ToImageSource(hIcon);
                DestroyIcon(hIcon);
                if (src != null) item.Icon = new ImageIcon { Source = src };
            }
            var hwnd = c.Hwnd;
            item.Click += (_, _) => AddWindow(hwnd);
            grab.Items.Add(item);
        }
        menu.Items.Add(grab);

        menu.Items.Add(new MenuFlyoutSeparator());
        var newGroup = new MenuFlyoutItem { Text = "새 그룹 창", Icon = new FontIcon { Glyph = "" } };
        newGroup.Click += (_, _) => App.OpenNewGroupWindow();
        menu.Items.Add(newGroup);

        return menu;
    }

    private MenuFlyout BuildTabMenu(TabViewItem tab)
    {
        var menu = new MenuFlyout();

        var detach = new MenuFlyoutItem { Text = "새 창으로 분리", Icon = new FontIcon { Glyph = "" } };
        detach.Click += (_, _) => DetachTab(tab, null, true);
        menu.Items.Add(detach);

        var closeOthers = new MenuFlyoutItem { Text = "다른 탭 닫기" };
        closeOthers.Click += (_, _) =>
        {
            foreach (var other in TabItems.Where(t => !ReferenceEquals(t, tab)).ToList()) CloseTab(other);
        };
        menu.Items.Add(closeOthers);

        var close = new MenuFlyoutItem { Text = "탭 닫기", Icon = new FontIcon { Glyph = "" } };
        close.Click += (_, _) => CloseTab(tab);
        menu.Items.Add(close);


        return menu;
    }

    // ------------------------------------------------------------------ TabView events

    private void Tabs_AddTabButtonClick(TabView sender, object args)
    {
        var anchor = FindDescendant<Button>(sender, "AddButton") ?? (FrameworkElement)sender;
        BuildAddMenu().ShowAt(anchor);
    }

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        // The tab's X (and middle click) releases the window back to the desktop instead of closing the
        // program; closing it for real is the context menu's "탭 닫기".
        if (args.Tab != null) DetachTab(args.Tab, null, activate: true);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        var tab = Tabs.SelectedItem as TabViewItem;
        ActivateTab(tab?.Tag as CapturedWindow);
    }

    private void Tabs_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (args.Tab?.Tag is not CapturedWindow cw)
        {
            args.Cancel = true;
            return;
        }
        args.Data.Properties.Add(DragKey, cw.Hwnd.ToInt64());
        args.Data.RequestedOperation = DataPackageOperation.Move;
        _dragging = true;
        Log.Write($"drag start {cw.Hwnd:X}");
    }

    private void Tabs_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        _dragging = false;
        Log.Write($"drag completed result={args.DropResult}");
        // Raised before TabDroppedOutside; run after it so a detached tab is not re-activated.
        DispatcherQueue.TryEnqueue(SyncActiveWithSelection);
    }

    /// <summary>Makes the shown app window match the selected tab (after selection changes were deferred).</summary>
    private void SyncActiveWithSelection()
    {
        if (_closing || _dragging) return;
        var cw = (Tabs.SelectedItem as TabViewItem)?.Tag as CapturedWindow;
        if (!ReferenceEquals(cw, _active)) ActivateTab(cw);
    }

    private void Tabs_TabStripDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.ContainsKey(DragKey))
            e.AcceptedOperation = DataPackageOperation.Move;
    }

    private void Tabs_TabStripDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.TryGetValue(DragKey, out var value) || value is not long raw) return;
        var hwnd = new IntPtr(raw);
        var source = FindHostOf(hwnd, out var sourceTab);
        if (source == null || sourceTab == null || source == this) return; // same-window reorder is handled by TabView

        int index = Tabs.TabItems.Count;
        for (int i = 0; i < Tabs.TabItems.Count; i++)
        {
            if (Tabs.ContainerFromIndex(i) is TabViewItem item && e.GetPosition(item).X - item.ActualWidth < 0)
            {
                index = i;
                break;
            }
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        // Moving the window (hide, re-own, show, focus) talks to another process synchronously; do it
        // after the drop callback has returned so the drag-drop thread is never blocked on it.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            var host = FindHostOf(hwnd, out var tab);
            if (host == null || tab == null || host == this) return;
            var cw = host.TransferOut(tab);
            AdoptWindow(cw, Math.Min(index, Tabs.TabItems.Count), activate: true);
        });
    }

    private void Tabs_TabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        if (args.Tab?.Tag is not CapturedWindow) return;
        if (Tabs.TabItems.Count <= 1) return; // dragging the only tab out of its window does nothing (Explorer behaviour)

        GetCursorPos(out var cursor);
        RECT? place = null;
        if (TryGetContentRect(out var content))
        {
            double scale = Scale;
            int w = content.Width, h = content.Height + (int)(TitleBarArea.ActualHeight * scale);
            int x = cursor.X - (int)(120 * scale), y = cursor.Y - (int)(20 * scale);
            place = new RECT(x, y, x + w, y + h);
        }
        DetachTab(args.Tab, place, activate: true);
    }

    private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Log.Write($"content size {e.NewSize.Width}x{e.NewSize.Height} strip={TitleBarArea.ActualHeight} tabs={Tabs.ActualHeight}");
        LayoutActive();
    }

    // ------------------------------------------------------------------ window events

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            SetGroupActive(IsOurs(GetForegroundWindow()));
        else
            SetGroupActive(true);
    }

    private void SetGroupActive(bool active)
    {
        if (active) LastActive = this;
        SetTabHotkeys(active);
        if (_groupActive == active) return;
        Log.Write($"group {_hwnd:X} active={active}");
        _groupActive = active;
        if (_backdropConfig != null) _backdropConfig.IsInputActive = active;
        UpdateInactiveButtonColor();
    }

    /// <summary>
    /// Ctrl+1..9 switch tabs like a browser. The hotkeys are global, so they are only registered while
    /// this group (the host or one of its app windows) is in the foreground and released otherwise, so
    /// other programs keep their own Ctrl+digit shortcuts. One owner at a time across group windows.
    /// </summary>
    private void SetTabHotkeys(bool on)
    {
        if (on)
        {
            if (ReferenceEquals(_tabHotkeyOwner, this)) return;
            _tabHotkeyOwner?.SetTabHotkeys(false);
            for (int n = 1; n <= 9; n++)
                RegisterHotKey(_hwnd, TabHotkeyBase + n, MOD_CONTROL | MOD_NOREPEAT, (uint)('0' + n));
            _tabHotkeyOwner = this;
        }
        else if (ReferenceEquals(_tabHotkeyOwner, this))
        {
            for (int n = 1; n <= 9; n++) UnregisterHotKey(_hwnd, TabHotkeyBase + n);
            _tabHotkeyOwner = null;
        }
    }

    /// <summary>Ctrl+n selects the n-th tab; Ctrl+9 selects the last one (browser convention).</summary>
    private void SelectTabByNumber(int n)
    {
        if (_closing || _dragging) return;
        int count = Tabs.TabItems.Count;
        if (count == 0) return;
        int index = n == 9 ? count - 1 : n - 1;
        if (index >= count) return;
        Log.Write($"hotkey ctrl+{n} -> tab {index}");
        if (Tabs.SelectedIndex == index) ActivateTab((Tabs.SelectedItem as TabViewItem)?.Tag as CapturedWindow); // refocus
        else Tabs.SelectedIndex = index;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closing) return;
        if (args.DidSizeChange) UpdateTitleBarInsets();
        if (args.DidPositionChange || args.DidSizeChange) LayoutActive();
        if ((args.DidPresenterChange || args.DidVisibilityChange) && !IsIconic(_hwnd) && _active != null)
        {
            LayoutActive();
            _active.Show();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args) => Teardown();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Teardown();
        _mica?.Dispose();
        _mica = null;
        RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1);
    }

    /// <summary>Best-effort restore of all grouped windows in every group window (crash path).</summary>
    public static void EmergencyReleaseAll()
    {
        foreach (var w in All.ToList())
        {
            try { w.Teardown(); } catch { }
        }
    }

    /// <summary>Releases every grouped window before our HWND goes away (owner destruction would orphan them frameless).</summary>
    private void Teardown()
    {
        if (_tornDown) return;
        _tornDown = true;
        _closing = true;
        Log.Write($"teardown {_hwnd:X}");

        foreach (var cts in _pendingLaunches.ToList()) cts.Cancel();
        _pendingLaunches.Clear();

        bool closeApps = string.Equals(App.Settings.OnGroupClose, "close", StringComparison.OrdinalIgnoreCase);
        TryGetContentRect(out var rect);
        var tabs = TabItems.ToList();
        foreach (var tab in tabs)
        {
            if (tab.Tag is not CapturedWindow cw) continue;
            var place = rect.Width > 0 ? rect : (RECT?)null;
            cw.Detach(place, activate: false);
            if (closeApps) cw.RequestClose();
        }
        foreach (var icon in _hostIcons.Values) if (icon != IntPtr.Zero) DestroyIcon(icon);
        _hostIcons.Clear();
        _active = null;

        var tracker = WindowTracker.Instance;
        tracker.Destroyed -= OnWindowDestroyed;
        tracker.Foreground -= OnForeground;
        tracker.NameChanged -= OnNameChanged;
        tracker.LocationChanged -= OnLocationChanged;
        tracker.MinimizeStart -= OnMinimizeStart;

        SetTabHotkeys(false);
        if (_hotkeyOwner)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyOwner = false;
        }
        All.Remove(this);
        if (ReferenceEquals(LastActive, this)) LastActive = All.LastOrDefault();
        All.FirstOrDefault()?.EnsureHotkey();
    }

    // ------------------------------------------------------------------ WinEvent handlers (UI thread)

    private void OnWindowDestroyed(IntPtr hwnd)
    {
        if (_closing) return;
        var tab = FindTab(hwnd);
        if (tab == null) return;
        Log.Write($"destroyed {hwnd:X}");
        RemoveTab(tab);
    }

    private void OnForeground(IntPtr hwnd)
    {
        if (_closing) return;
        // WinEvents are delivered asynchronously. After two tabs are activated back to back (tab
        // reorder, quick launches) the stale event for the first window would re-select its tab,
        // which focuses it, which queues another event for the other one... an endless ping-pong
        // that looks like a freeze. Only act on an event that still describes the real foreground.
        if (GetForegroundWindow() != hwnd) return;
        bool ours = IsOurs(hwnd);
        if (!ours && _groupActive && Log.Enabled)
            Log.Write($"foreground left group: {hwnd:X} '{GetWindowClass(hwnd)}' pid={GetWindowPid(hwnd)}");
        SetGroupActive(ours);
        if (!ours || hwnd == _hwnd) return;

        // An app window that is not the selected tab somehow came to the front: follow it.
        var root = GetAncestor(hwnd, GA_ROOTOWNER);
        var tab = FindTab(hwnd) ?? (root != IntPtr.Zero ? FindTab(root) : null);
        if (tab?.Tag is CapturedWindow cw && !ReferenceEquals(cw, _active))
            Tabs.SelectedItem = tab;
    }

    private void OnNameChanged(IntPtr hwnd)
    {
        if (_closing) return;
        var tab = FindTab(hwnd);
        if (tab?.Tag is not CapturedWindow cw) return;
        RefreshTab(tab, cw);
        if (ReferenceEquals(cw, _active)) UpdateHostIdentity();
    }

    private void OnLocationChanged(IntPtr hwnd)
    {
        if (_closing || _active == null) return;
        if (hwnd == _hwnd || hwnd == _active.Hwnd) LayoutActive();
    }

    private void OnMinimizeStart(IntPtr hwnd)
    {
        if (_closing) return;
        // The app minimized itself (e.g. Win+Down inside it): minimize the whole group instead.
        if (FindTab(hwnd) != null && !IsIconic(_hwnd)) ShowWindow(_hwnd, SW_MINIMIZE);
    }

    // ------------------------------------------------------------------ hotkey

    private static void GrabForegroundWindow()
    {
        var fg = GetAncestor(GetForegroundWindow(), GA_ROOT);
        if (fg == IntPtr.Zero || IsGroupedAnywhere(fg)) return;
        if (!WindowEnumerator.IsCandidate(fg, (uint)Environment.ProcessId)) return;

        var target = LastActive != null && All.Contains(LastActive) ? LastActive : All.FirstOrDefault();
        if (target == null) return;
        if (IsIconic(target._hwnd)) ShowWindow(target._hwnd, SW_RESTORE);
        var cw = target.AddWindow(fg);
        if (cw == null) return;
        // The grabbed window keeps focus; make sure the group window sits right behind it.
        SetWindowPos(target._hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        cw.Focus();
    }

    // ------------------------------------------------------------------ misc

    private static T? FindDescendant<T>(DependencyObject root, string? name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (name == null || match.Name == name)) return match;
            var deeper = FindDescendant<T>(child, name);
            if (deeper != null) return deeper;
        }
        return null;
    }
}

/// <summary>Creates the Windows.System.DispatcherQueue the Mica controller needs on this thread.</summary>
internal sealed class WindowsSystemDispatcherQueueHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController([In] DispatcherQueueOptions options, [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

    private object? _controller;

    public void EnsureWindowsSystemDispatcherQueueController()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        if (_controller != null) return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,    // DQTYPE_THREAD_CURRENT
            apartmentType = 2, // DQTAT_COM_STA
        };
        CreateDispatcherQueueController(options, ref _controller);
    }
}
