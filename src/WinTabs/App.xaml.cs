using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinTabs.Core;

namespace WinTabs;

public partial class App : Application
{
    public static Settings Settings { get; private set; } = new();
    public static DispatcherQueue UiQueue { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        // If we go down unexpectedly, give every grouped window its frame and owner back first.
        UnhandledException += (_, e) =>
        {
            Log.Write("unhandled: " + e.Exception);
            MainWindow.EmergencyReleaseAll();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write("unhandled (domain): " + e.ExceptionObject);
            MainWindow.EmergencyReleaseAll();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiQueue = DispatcherQueue.GetForCurrentThread();
        Settings = Settings.Load();
        WindowTracker.Instance.Start();

        // A second launch of the exe is redirected here (see Program.cs).
        AppInstance.GetCurrent().Activated += (_, e) =>
        {
            var arguments = (e.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments ?? string.Empty;
            UiQueue.TryEnqueue(() => _ = HandleCommandLineAsync(SplitArguments(arguments)));
        };

        _ = HandleCommandLineAsync(Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    /// <summary>
    /// WinTabs.exe [--new] [explorer|cmd|powershell|terminal|notepad ...]
    /// Without targets a new group window opens with the configured startup tab. With targets the
    /// tabs are opened in the most recently used group window (or a new one with --new).
    /// </summary>
    public static async Task HandleCommandLineAsync(string[] args)
    {
        bool forceNew = args.Any(a => a.Equals("--new", StringComparison.OrdinalIgnoreCase));
        var targets = args.Select(a => LaunchTarget.ById(a.TrimStart('-', '/'))).Where(t => t != null).Select(t => t!).ToList();
        Log.Write($"command line: {string.Join(' ', args)}");

        if (targets.Count == 0)
        {
            OpenNewGroupWindow();
            return;
        }

        var host = !forceNew && MainWindow.LastActive != null && MainWindow.All.Contains(MainWindow.LastActive)
            ? MainWindow.LastActive
            : OpenNewGroupWindow(runStartupAction: false);

        foreach (var target in targets)
            await host.LaunchIntoAsync(target);
    }

    private static string[] SplitArguments(string commandLine)
    {
        var list = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) { list.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) list.Add(current.ToString());
        return list.ToArray();
    }

    public static MainWindow OpenNewGroupWindow(bool runStartupAction = true)
    {
        var window = new MainWindow();
        window.Activate();
        if (runStartupAction) window.RunStartupAction();
        return window;
    }
}
