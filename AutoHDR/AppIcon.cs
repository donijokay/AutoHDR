namespace AutoHDR;

/// <summary>
/// Loads the shared AutoHDR tray/app icon from the Assets folder next to the exe,
/// falling back to the icon embedded in the executable.
/// </summary>
internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Load()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "autohdr.ico");
            if (File.Exists(icoPath))
            {
                using var icon = new Icon(icoPath);
                _cached = (Icon)icon.Clone();
                return _cached;
            }
        }
        catch
        {
            /* fall through */
        }

        try
        {
            string? exe = Environment.ProcessPath ?? Application.ExecutablePath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null)
                {
                    _cached = extracted;
                    return _cached;
                }
            }
        }
        catch
        {
            /* fall through */
        }

        _cached = (Icon)SystemIcons.Application.Clone();
        return _cached;
    }
}
