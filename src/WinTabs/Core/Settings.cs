using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTabs.Core;

/// <summary>Per-application tweaks, matched by process file name or window class name (case-insensitive).</summary>
public sealed class AppProfile
{
    public string Match { get; set; } = string.Empty;

    /// <summary>Remove caption/thick frame while grouped (almost always what you want).</summary>
    public bool StripFrame { get; set; } = true;

    /// <summary>
    /// Keep the WS_CAPTION style bit while stripping the rest of the frame. For apps that draw their
    /// own title bar (Windows 11 Notepad) the system caption is never visible anyway, but without the
    /// bit their frame logic goes wrong every time the window is deactivated: the content shifts down
    /// behind a strip of DWM caption text until the window is hidden and shown again. null = built-in
    /// default (true for Notepad, false otherwise).
    /// </summary>
    public bool? KeepCaption { get; set; }

    public bool KeepsCaption(string? className) => KeepCaption ?? string.Equals(className, "Notepad", StringComparison.Ordinal);
}

public sealed class Settings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTabs");
    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>"explorer" opens a File Explorer tab in every new group window; "none" starts empty.</summary>
    public string StartupTab { get; set; } = "explorer";

    /// <summary>Close the group window when its last tab goes away (File Explorer behaviour).</summary>
    public bool CloseWhenEmpty { get; set; } = true;

    /// <summary>"detach" releases grouped windows back to the desktop when a group window closes; "close" closes them.</summary>
    public string OnGroupClose { get; set; } = "detach";

    /// <summary>Ctrl+Alt+T pulls the foreground window into the most recently used group.</summary>
    public bool GrabHotkey { get; set; } = true;

    /// <summary>Default width/height of a new group window in DIPs.</summary>
    public int WindowWidth { get; set; } = 1142;

    /// <summary>Append diagnostics to %LOCALAPPDATA%\WinTabs\debug.log (also enabled by WINTABS_DEBUG=1).</summary>
    public bool DebugLog { get; set; }

    public int WindowHeight { get; set; } = 637;

    public List<AppProfile> Profiles { get; set; } = DefaultProfiles();

    public static List<AppProfile> DefaultProfiles() => new()
    {
        new AppProfile { Match = "explorer.exe" },
        new AppProfile { Match = "Notepad.exe", KeepCaption = true },
        new AppProfile { Match = "WindowsTerminal.exe" },
        new AppProfile { Match = "ConsoleWindowClass" },
    };

    public AppProfile GetProfile(string? processName, string? className)
    {
        AppProfile? found = null;
        if (!string.IsNullOrEmpty(processName))
            found = Profiles.FirstOrDefault(p => string.Equals(p.Match, processName, StringComparison.OrdinalIgnoreCase));
        if (found == null && !string.IsNullOrEmpty(className))
            found = Profiles.FirstOrDefault(p => string.Equals(p.Match, className, StringComparison.OrdinalIgnoreCase));
        if (found == null)
        {
            found = new AppProfile { Match = processName ?? className ?? "*" };
            Profiles.Add(found);
        }
        return found;
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                if (s != null)
                {
                    // Make sure built-in defaults exist for apps the user never touched.
                    foreach (var d in DefaultProfiles())
                        if (!s.Profiles.Any(p => string.Equals(p.Match, d.Match, StringComparison.OrdinalIgnoreCase)))
                            s.Profiles.Add(d);
                    return s;
                }
            }
        }
        catch { /* fall through to defaults */ }
        var fresh = new Settings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* settings are best effort */ }
    }
}
