namespace AutoHDR.Services;

/// <summary>
/// Best-effort file logger under %AppData%\AutoHDR\autohdr.log.
/// Never throws — logging must not crash the tray app.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;

    private static string LogPath
    {
        get
        {
            if (_path != null) return _path;
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AutoHDR");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "autohdr.log");
            return _path;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex != null)
                line += $" | {ex.GetType().Name}: {ex.Message}";
            lock (Gate)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            /* never throw from logger */
        }
    }
}
