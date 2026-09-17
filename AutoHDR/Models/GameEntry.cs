using System.Text.Json.Serialization;

namespace AutoHDR.Models;

/// <summary>
/// One discovered or manually added game in the AutoHDR library.
/// Persisted in %AppData%\AutoHDR\games.json
/// </summary>
public sealed class GameEntry
{
    /// <summary>Stable id (source + normalized path / exe).</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name shown in Settings.</summary>
    public string Name { get; set; } = "";

    /// <summary>Executable name without extension (e.g. Cyberpunk2077).</summary>
    public string ExeName { get; set; } = "";

    /// <summary>Full path to the primary executable when known.</summary>
    public string ExePath { get; set; } = "";

    /// <summary>Discovery source: Steam | Epic | Xbox | Custom | Manual</summary>
    public string Source { get; set; } = "Custom";

    /// <summary>When true, AutoHDR enables HDR as soon as this process starts.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>UTC timestamp of last successful discovery / refresh.</summary>
    public string LastSeenUtc { get; set; } = "";

    /// <summary>False when the install path is no longer present (kept for user On/Off).</summary>
    [JsonIgnore]
    public bool IsFound =>
        !string.IsNullOrWhiteSpace(ExePath) && File.Exists(ExePath);
}
