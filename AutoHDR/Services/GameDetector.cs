using System.Diagnostics;
using AutoHDR.Models;
using AutoHDR.Native;
using static AutoHDR.Native.HdrNative;

namespace AutoHDR.Services;

public sealed class GameDetectionResult
{
    public bool IsGameRunning { get; init; }
    public string? ProcessName { get; init; }
    public int? ProcessId { get; init; }
}

/// <summary>
/// Detects fullscreen / borderless-fullscreen foreground windows that look like games.
/// Uses low-frequency timer polling (~1–2s) for low CPU.
/// Active sessions are tracked by ProcessId so Alt-Tab does not drop HDR.
/// </summary>
public sealed class GameDetector
{
    private readonly HashSet<string> _exclusions;
    private readonly List<string> _whitelist;
    private readonly double _coverageThreshold;

    public GameDetector(AppConfig config)
    {
        _exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in AppConfig.DefaultExclusions)
            _exclusions.Add(ConfigService.NormalizeExeName(e));
        foreach (var e in config.ExtraExclusions)
            _exclusions.Add(ConfigService.NormalizeExeName(e));

        _whitelist = config.Whitelist
            .Select(ConfigService.NormalizeExeName)
            .Where(s => s.Length > 0)
            .ToList();

        _coverageThreshold = config.FullscreenCoverageThreshold;
    }

    public GameDetectionResult Detect()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
                return NotGame();

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return NotGame();

            Process? proc;
            try { proc = Process.GetProcessById((int)pid); }
            catch { return NotGame(); }

            string name;
            try { name = proc.ProcessName; }
            catch { return NotGame(); }

            string normalized = ConfigService.NormalizeExeName(name);
            if (_exclusions.Contains(normalized))
                return NotGame();

            if (_whitelist.Count > 0 &&
                !_whitelist.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                return NotGame();

            if (!LooksFullscreenOrBorderless(hwnd))
                return NotGame();

            return new GameDetectionResult
            {
                IsGameRunning = true,
                ProcessName = normalized,
                ProcessId = (int)pid,
            };
        }
        catch
        {
            return NotGame();
        }
    }

    /// <summary>
    /// Returns true if a process with the given id is still running.
    /// </summary>
    public static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
            return false;
        try
        {
            using var proc = Process.GetProcessById(processId);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool LooksFullscreenOrBorderless(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT windowRect))
            return false;

        IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero)
            return false;

        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
            return false;

        int monW = mi.rcMonitor.right - mi.rcMonitor.left;
        int monH = mi.rcMonitor.bottom - mi.rcMonitor.top;
        if (monW <= 0 || monH <= 0)
            return false;

        int winW = windowRect.right - windowRect.left;
        int winH = windowRect.bottom - windowRect.top;
        if (winW <= 0 || winH <= 0)
            return false;

        // Intersection coverage vs monitor
        int interL = Math.Max(windowRect.left, mi.rcMonitor.left);
        int interT = Math.Max(windowRect.top, mi.rcMonitor.top);
        int interR = Math.Min(windowRect.right, mi.rcMonitor.right);
        int interB = Math.Min(windowRect.bottom, mi.rcMonitor.bottom);
        int interW = Math.Max(0, interR - interL);
        int interH = Math.Max(0, interB - interT);
        double coverage = (double)(interW * interH) / (monW * monH);

        if (coverage < _coverageThreshold)
            return false;

        // Exclusive fullscreen often has no caption; borderless is typically WS_POPUP
        int style = GetWindowLong(hwnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) != 0;
        bool isPopup = (style & WS_POPUP) != 0;

        // Accept: covers most of monitor AND (no caption OR popup-style borderless)
        if (!hasCaption || isPopup)
            return true;

        // Also accept near-exact monitor match even with residual styles
        return coverage >= 0.98 &&
               Math.Abs(winW - monW) <= 4 &&
               Math.Abs(winH - monH) <= 4;
    }

    private static GameDetectionResult NotGame() => new() { IsGameRunning = false };
}
