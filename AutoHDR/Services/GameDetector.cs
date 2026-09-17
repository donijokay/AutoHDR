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

    /// <summary>Rate-limit near-miss logs: process name → last log UTC ticks.</summary>
    private readonly Dictionary<string, long> _nearMissLogUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long NearMissCooldownTicks = TimeSpan.FromMinutes(1).Ticks;

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

            if (!LooksFullscreenOrBorderless(hwnd, normalized))
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

    private bool LooksFullscreenOrBorderless(IntPtr hwnd, string processName)
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

        int workW = mi.rcWork.right - mi.rcWork.left;
        int workH = mi.rcWork.bottom - mi.rcWork.top;

        int winW = windowRect.right - windowRect.left;
        int winH = windowRect.bottom - windowRect.top;
        if (winW <= 0 || winH <= 0)
            return false;

        // Intersection coverage vs full monitor
        double coverage = ComputeCoverage(windowRect, mi.rcMonitor, monW, monH);

        // Intersection coverage vs work area (maximized windows often match work area)
        double workCoverage = (workW > 0 && workH > 0)
            ? ComputeCoverage(windowRect, mi.rcWork, workW, workH)
            : 0;

        // Client-area coverage vs monitor (borderless sometimes has chrome outside client)
        double clientCoverage = 0;
        if (GetClientRect(hwnd, out RECT clientRect))
        {
            int clientW = clientRect.right - clientRect.left;
            int clientH = clientRect.bottom - clientRect.top;
            if (clientW > 0 && clientH > 0)
                clientCoverage = (double)(clientW * clientH) / (monW * monH);
        }

        int style = GetWindowLong(hwnd, GWL_STYLE);
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        bool hasCaption = (style & WS_CAPTION) != 0;
        bool isPopup = (style & WS_POPUP) != 0;
        bool hasThickFrame = (style & WS_THICKFRAME) != 0;
        bool isTopmost = (exStyle & WS_EX_TOPMOST) != 0;
        bool isMaximized = IsZoomed(hwnd);

        bool meetsCoverage = coverage >= _coverageThreshold
            || clientCoverage >= _coverageThreshold
            || (isMaximized && (coverage >= 0.88 || workCoverage >= 0.88));

        if (!meetsCoverage)
        {
            MaybeLogNearMiss(processName, coverage, clientCoverage, workCoverage, style, exStyle, isMaximized);
            return false;
        }

        // Exclusive fullscreen often has no caption; borderless is typically WS_POPUP
        if (!hasCaption || isPopup)
            return true;

        // Maximized window covering most of monitor/work area
        if (isMaximized && (coverage >= 0.88 || workCoverage >= 0.88))
            return true;

        // Borderless-windowed games often keep caption but are topmost or lack thick frame
        if (coverage >= _coverageThreshold && (isTopmost || !hasThickFrame))
            return true;

        // Client fills the monitor even if outer frame has caption chrome
        if (clientCoverage >= _coverageThreshold && (isTopmost || !hasThickFrame || isPopup))
            return true;

        // Near-exact monitor match with widened tolerance (8–16 px) for borderless
        const int tol = 16;
        if (coverage >= 0.88 &&
            Math.Abs(winW - monW) <= tol &&
            Math.Abs(winH - monH) <= tol)
            return true;

        MaybeLogNearMiss(processName, coverage, clientCoverage, workCoverage, style, exStyle, isMaximized);
        return false;
    }

    private static double ComputeCoverage(RECT window, RECT area, int areaW, int areaH)
    {
        int interL = Math.Max(window.left, area.left);
        int interT = Math.Max(window.top, area.top);
        int interR = Math.Min(window.right, area.right);
        int interB = Math.Min(window.bottom, area.bottom);
        int interW = Math.Max(0, interR - interL);
        int interH = Math.Max(0, interB - interT);
        return (double)(interW * interH) / (areaW * areaH);
    }

    private void MaybeLogNearMiss(
        string processName,
        double coverage,
        double clientCoverage,
        double workCoverage,
        int style,
        int exStyle,
        bool isMaximized)
    {
        double best = Math.Max(coverage, Math.Max(clientCoverage, workCoverage));
        if (best < 0.75)
            return;

        long now = DateTime.UtcNow.Ticks;
        lock (_nearMissLogUtc)
        {
            if (_nearMissLogUtc.TryGetValue(processName, out long last) &&
                now - last < NearMissCooldownTicks)
                return;
            _nearMissLogUtc[processName] = now;
        }

        bool hasCaption = (style & WS_CAPTION) != 0;
        bool isPopup = (style & WS_POPUP) != 0;
        bool hasThickFrame = (style & WS_THICKFRAME) != 0;
        bool isTopmost = (exStyle & WS_EX_TOPMOST) != 0;

        AppLog.Info(
            $"Near-miss fullscreen: {processName} coverage={coverage:F3} client={clientCoverage:F3} " +
            $"work={workCoverage:F3} caption={hasCaption} popup={isPopup} thickFrame={hasThickFrame} " +
            $"topmost={isTopmost} maximized={isMaximized} style=0x{style:X8} exStyle=0x{exStyle:X8}");
    }

    private static GameDetectionResult NotGame() => new() { IsGameRunning = false };
}
