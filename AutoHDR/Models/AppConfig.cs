using System.Text.Json.Serialization;

namespace AutoHDR.Models;

/// <summary>
/// Application configuration stored at %AppData%\AutoHDR\config.json
/// </summary>
public sealed class AppConfig
{
    /// <summary>When true, AutoHDR arms on startup and watches for games.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Poll interval in milliseconds (1000–2000 recommended).</summary>
    public int PollIntervalMs { get; set; } = 1500;

    /// <summary>
    /// Optional whitelist of executable names (without path).
    /// Empty = detect any fullscreen/borderless game not on the exclusion list.
    /// Non-empty = only those exe names can trigger HDR.
    /// </summary>
    public List<string> Whitelist { get; set; } = new();

    /// <summary>Extra process names to always ignore (merged with built-in defaults).</summary>
    public List<string> ExtraExclusions { get; set; } = new();

    /// <summary>Launch AutoHDR when Windows starts (Current User Run key).</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Minimum window coverage of the monitor (0.0–1.0) to count as fullscreen.</summary>
    public double FullscreenCoverageThreshold { get; set; } = 0.95;

    /// <summary>Apply HDR to all HDR-capable displays (true) or primary only (false).</summary>
    public bool AllHdrDisplays { get; set; } = true;

    [JsonIgnore]
    public static IReadOnlyList<string> DefaultExclusions { get; } = new[]
    {
        "explorer",
        "ApplicationFrameHost",
        "ShellExperienceHost",
        "SearchHost",
        "SearchUI",
        "StartMenuExperienceHost",
        "TextInputHost",
        "LockApp",
        "LogonUI",
        "dwm",
        "sihost",
        "csrss",
        "winlogon",
        "RuntimeBroker",
        "SystemSettings",
        "Settings",
        "chrome",
        "msedge",
        "msedgewebview2",
        "firefox",
        "brave",
        "opera",
        "iexplore",
        "Code",
        "devenv",
        "Cursor",
        "WINWORD",
        "EXCEL",
        "POWERPNT",
        "OUTLOOK",
        "Discord",
        "Slack",
        "Teams",
        "ms-teams",
        "Zoom",
        "Spotify",
        "vlc",
        "mpv",
        "notepad",
        "notepad++",
        "Taskmgr",
        "cmd",
        "powershell",
        "pwsh",
        "WindowsTerminal",
        "wt",
        "AutoHDR",
        "steamwebhelper",
        "GameBar",
        "GameBarFTServer",
        "XboxPcApp",
        "WidgetBoard",
        "Widgets",
    };
}
