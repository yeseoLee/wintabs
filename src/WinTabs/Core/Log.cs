namespace WinTabs.Core;

public static class Log
{
    private static readonly object Gate = new();
    private static bool? _enabled;
    private static string LogPath => Path.Combine(Settings.Directory, "debug.log");

    public static bool Enabled
    {
        get
        {
            _enabled ??= App.Settings.DebugLog || Environment.GetEnvironmentVariable("WINTABS_DEBUG") == "1";
            return _enabled.Value;
        }
    }

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Directory);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
