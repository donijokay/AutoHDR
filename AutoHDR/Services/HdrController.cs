using System.Runtime.InteropServices;
using AutoHDR.Native;
using static AutoHDR.Native.HdrNative;

namespace AutoHDR.Services;

internal sealed class DisplayHdrState
{
    public LUID AdapterId { get; init; }
    public uint TargetId { get; init; }
    public bool Supported { get; init; }
    public bool Enabled { get; init; }
    public bool IsPrimary { get; init; }
    public string? GdiDeviceName { get; init; }
}

/// <summary>
/// Controls Windows Advanced Color / HDR via DisplayConfig APIs.
/// Remembers prior HDR state so AutoHDR only restores what it changed.
/// </summary>
public sealed class HdrController
{
    private readonly object _gate = new();
    private bool _autoHdrEnabledHdr;
    private Dictionary<string, bool>? _priorStates;

    public bool AutoHdrTurnedOnHdr
    {
        get { lock (_gate) return _autoHdrEnabledHdr; }
    }

    public bool IsHdrSupported(bool allDisplays = true)
    {
        return QueryHdrCapableDisplays(allDisplays).Any(d => d.Supported);
    }

    public bool IsHdrEnabled(bool allDisplays = true)
    {
        var capable = QueryHdrCapableDisplays(allDisplays).Where(d => d.Supported).ToList();
        if (capable.Count == 0) return false;
        return capable.All(d => d.Enabled);
    }

    /// <summary>
    /// Enable HDR on HDR-capable displays. Snapshots prior state for restore.
    /// </summary>
    public bool EnableHdrForGame(bool allDisplays)
    {
        lock (_gate)
        {
            var capable = QueryHdrCapableDisplays(allDisplays).Where(d => d.Supported).ToList();
            if (capable.Count == 0)
                return false;

            if (!_autoHdrEnabledHdr)
            {
                _priorStates = capable.ToDictionary(KeyOf, d => d.Enabled, StringComparer.Ordinal);
            }

            bool anyTurnedOnByUs = false;
            bool anySuccess = false;

            foreach (var d in capable)
            {
                if (d.Enabled)
                {
                    anySuccess = true;
                    continue;
                }

                if (SetAdvancedColor(d.AdapterId, d.TargetId, enable: true))
                {
                    anySuccess = true;
                    anyTurnedOnByUs = true;
                }
            }

            if (anyTurnedOnByUs)
                _autoHdrEnabledHdr = true;
            else if (_priorStates != null && _priorStates.Values.All(wasOn => wasOn))
            {
                // Already fully on before we got here — no restore needed
                _autoHdrEnabledHdr = false;
                _priorStates = null;
            }

            return anySuccess;
        }
    }

    /// <summary>
    /// Restore HDR only if AutoHDR previously turned it on.
    /// </summary>
    public void RestoreAfterGame()
    {
        lock (_gate)
        {
            if (!_autoHdrEnabledHdr || _priorStates == null)
            {
                _autoHdrEnabledHdr = false;
                _priorStates = null;
                return;
            }

            foreach (var d in QueryHdrCapableDisplays(allDisplays: true).Where(x => x.Supported))
            {
                if (!_priorStates.TryGetValue(KeyOf(d), out var wasEnabled))
                    continue;
                if (d.Enabled != wasEnabled)
                    SetAdvancedColor(d.AdapterId, d.TargetId, wasEnabled);
            }

            _autoHdrEnabledHdr = false;
            _priorStates = null;
        }
    }

    /// <summary>Manual toggle; clears AutoHDR ownership of restore state.</summary>
    public bool ToggleHdr(bool allDisplays)
    {
        var capable = QueryHdrCapableDisplays(allDisplays).Where(d => d.Supported).ToList();
        if (capable.Count == 0)
            return false;

        bool enable = !capable.All(d => d.Enabled);
        bool ok = false;
        foreach (var d in capable)
        {
            if (SetAdvancedColor(d.AdapterId, d.TargetId, enable))
                ok = true;
        }

        lock (_gate)
        {
            _autoHdrEnabledHdr = false;
            _priorStates = null;
        }

        return ok;
    }

    internal List<DisplayHdrState> QueryHdrCapableDisplays(bool allDisplays)
    {
        var result = new List<DisplayHdrState>();
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
            return result;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ERROR_SUCCESS)
            return result;

        var primaryGdiNames = GetPrimaryGdiDeviceNames();

        for (uint i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            if ((path.flags & 0x1) == 0)
                continue;

            var colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                }
            };

            if (DisplayConfigGetDeviceInfo(ref colorInfo) != ERROR_SUCCESS)
                continue;
            if (!colorInfo.AdvancedColorSupported)
                continue;

            string? gdiName = TryGetSourceName(path.sourceInfo.adapterId, path.sourceInfo.id);
            bool isPrimary = gdiName != null && primaryGdiNames.Contains(gdiName);

            result.Add(new DisplayHdrState
            {
                AdapterId = path.targetInfo.adapterId,
                TargetId = path.targetInfo.id,
                Supported = true,
                Enabled = colorInfo.AdvancedColorEnabled,
                IsPrimary = isPrimary,
                GdiDeviceName = gdiName,
            });
        }

        // If primary detection failed, mark first entry as primary
        if (result.Count > 0 && !result.Any(d => d.IsPrimary))
        {
            result[0] = new DisplayHdrState
            {
                AdapterId = result[0].AdapterId,
                TargetId = result[0].TargetId,
                Supported = result[0].Supported,
                Enabled = result[0].Enabled,
                IsPrimary = true,
                GdiDeviceName = result[0].GdiDeviceName,
            };
        }

        if (!allDisplays)
        {
            var primary = result.FirstOrDefault(d => d.IsPrimary) ?? result.FirstOrDefault();
            return primary == null ? result : new List<DisplayHdrState> { primary };
        }

        return result;
    }

    private static HashSet<string> GetPrimaryGdiDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfoEx(hMonitor, ref mi) && (mi.dwFlags & MONITORINFOF_PRIMARY) != 0)
                    names.Add(mi.szDevice);
                return true;
            }, IntPtr.Zero);
        return names;
    }

    private static string? TryGetSourceName(LUID adapterId, uint sourceId)
    {
        var packet = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId,
            }
        };
        return DisplayConfigGetDeviceInfo(ref packet) == ERROR_SUCCESS
            ? packet.viewGdiDeviceName
            : null;
    }

    private static bool SetAdvancedColor(LUID adapterId, uint targetId, bool enable)
    {
        var packet = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                size = Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = adapterId,
                id = targetId,
            },
            value = enable ? 1u : 0u,
        };
        return DisplayConfigSetDeviceInfo(ref packet) == ERROR_SUCCESS;
    }

    private static string KeyOf(DisplayHdrState d) =>
        $"{d.AdapterId.LowPart}:{d.AdapterId.HighPart}:{d.TargetId}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "GetMonitorInfo")]
    private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);
}
