namespace AutoHDR.Models;

/// <summary>
/// Root document for %AppData%\AutoHDR\games.json
/// </summary>
public sealed class GameLibraryStore
{
    public List<GameEntry> Games { get; set; } = new();
    public List<string> CustomFolders { get; set; } = new();
}
