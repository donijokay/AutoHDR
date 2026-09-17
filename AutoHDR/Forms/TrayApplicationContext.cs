using AutoHDR.Models;
using AutoHDR.Services;

namespace AutoHDR.Forms;

/// <summary>
/// System-tray host: arms/disarms AutoHDR, polls for games, toggles HDR.
/// v1.0.6: library-tracked games enable HDR on process start (no fullscreen wait)
/// and restore only after process exit (+ debounce). Fullscreen detection remains
/// as fallback for processes not in the library (or with Enabled=Off).
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int RestoreDebounceMs = 2500;
    private const int LibraryScanIntervalMs = 3 * 60 * 1000; // ~3 minutes

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enableItem;
    private readonly ToolStripMenuItem _toggleHdrItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _restoreDebounceTimer;
    private readonly System.Windows.Forms.Timer _libraryScanTimer;

    private readonly ConfigService _configService;
    private readonly GameLibraryService _library;
    private readonly HdrController _hdr;
    private AppConfig _config;
    private GameDetector _detector;

    private bool _armed;
    private bool _gameActive;
    private string? _activeGame;
    private int? _activeProcessId;
    private bool _librarySession; // true = keep HDR while process alive (ignore Alt-Tab)
    private bool _unsupportedTipShown;
    private bool _restorePending;

    // Start debounce: require 2 consecutive Detect() hits (same PID) before enabling HDR.
    // Used only for the fullscreen fallback path (not library sessions).
    private int? _pendingPid;
    private string? _pendingName;
    private int _pendingHits;

    public TrayApplicationContext()
    {
        _configService = new ConfigService();
        _config = _configService.Load();
        _library = new GameLibraryService();
        _library.Load();
        try { _library.Scan(force: true); }
        catch (Exception ex) { AppLog.Warn($"Initial library scan failed: {ex.Message}"); }

        _hdr = new HdrController();
        _detector = new GameDetector(_config);
        _armed = _config.Enabled;

        try { StartupService.SetStartWithWindows(_config.StartWithWindows); }
        catch { /* ignore */ }

        _enableItem = new ToolStripMenuItem("Enable AutoHDR", null, OnToggleArmed)
        {
            Checked = _armed,
            CheckOnClick = false,
        };
        _toggleHdrItem = new ToolStripMenuItem("Toggle HDR now", null, OnToggleHdr);

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_enableItem);
        _menu.Items.Add(_toggleHdrItem);
        _menu.Items.Add(new ToolStripMenuItem("Games…", null, OnGames));
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, OnSettings));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(),
            Text = BuildTooltip(),
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => OnSettings(null!, EventArgs.Empty);

        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(_config.PollIntervalMs, 500, 10000),
        };
        _timer.Tick += OnPoll;
        _timer.Start();

        _restoreDebounceTimer = new System.Windows.Forms.Timer
        {
            Interval = RestoreDebounceMs,
        };
        _restoreDebounceTimer.Tick += OnRestoreDebounceElapsed;

        _libraryScanTimer = new System.Windows.Forms.Timer
        {
            Interval = LibraryScanIntervalMs,
        };
        _libraryScanTimer.Tick += (_, _) =>
        {
            try { _library.Scan(force: false); }
            catch (Exception ex) { AppLog.Warn($"Periodic library scan failed: {ex.Message}"); }
        };
        _libraryScanTimer.Start();

        // Startup balloon
        try
        {
            if (!_hdr.IsHdrSupported(_config.AllHdrDisplays))
            {
                _tray.BalloonTipTitle = "AutoHDR";
                _tray.BalloonTipText = "No HDR-capable display detected. AutoHDR needs an HDR monitor and Windows 11 HDR support.";
                _tray.BalloonTipIcon = ToolTipIcon.Warning;
                _tray.ShowBalloonTip(5000);
                _unsupportedTipShown = true;
            }
            else
            {
                int enabledGames = _library.EnabledCount;
                _tray.BalloonTipTitle = "AutoHDR";
                _tray.BalloonTipText = _armed
                    ? $"Armed — library auto-detect ({enabledGames} games On). HDR on process start or fullscreen."
                    : "Disarmed — use the tray menu to enable.";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(3200);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup HDR check failed", ex);
        }

        UpdateMenuState();
        AppLog.Info($"AutoHDR started (library: {_library.Games.Count} games, { _library.EnabledCount} enabled).");
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        try
        {
            if (!_armed)
            {
                CancelRestoreDebounce();
                ClearStartDebounce();
                if (_gameActive)
                    EndGameSessionImmediate();
                return;
            }

            if (!_hdr.IsHdrSupported(_config.AllHdrDisplays))
            {
                if (!_unsupportedTipShown)
                {
                    _tray.BalloonTipTitle = "AutoHDR";
                    _tray.BalloonTipText = "HDR is not supported on any active display.";
                    _tray.BalloonTipIcon = ToolTipIcon.Warning;
                    _tray.ShowBalloonTip(4000);
                    _unsupportedTipShown = true;
                }
                return;
            }

            // ── A) Library process tracking ──────────────────────────────
            var running = _library.FindRunningEnabledGame();
            if (running is { } libHit)
            {
                CancelRestoreDebounce();

                if (_gameActive && _librarySession && _activeProcessId == libHit.ProcessId)
                {
                    ClearStartDebounce();
                    return; // keep HDR while process alive (Alt-Tab OK)
                }

                // New or switched library session — enable immediately (no start debounce)
                ClearStartDebounce();
                _gameActive = true;
                _librarySession = true;
                _activeGame = libHit.Game.Name;
                _activeProcessId = libHit.ProcessId;
                AppLog.Info($"library process HDR: {libHit.Game.Name} ({libHit.Game.ExeName}) pid {libHit.ProcessId}");
                _hdr.EnableHdrForGame(_config.AllHdrDisplays);
                UpdateMenuState();
                return;
            }

            // Library session but process gone → debounce restore
            if (_gameActive && _librarySession)
            {
                ClearStartDebounce();
                if (_activeProcessId is int libPid && GameDetector.IsProcessAlive(libPid))
                {
                    // Process still alive but FindRunningEnabledGame missed (access race) — keep
                    return;
                }
                ScheduleRestoreDebounce(libraryExit: true);
                return;
            }

            // ── B) Fullscreen fallback (non-library / disabled library) ──
            var result = _detector.Detect();
            if (result.IsGameRunning && result.ProcessId is int pid)
            {
                // Don't double-enable / steal from library path for enabled library exes
                if (_library.IsEnabledLibraryExe(result.ProcessName))
                {
                    // Process may still be starting; next poll will catch via library path
                    ClearStartDebounce();
                    return;
                }

                // Disabled library entry → Off means Off (skip fullscreen fallback)
                if (_library.IsLibraryExe(result.ProcessName))
                {
                    ClearStartDebounce();
                    if (_gameActive && !_librarySession)
                        ScheduleRestoreDebounce(libraryExit: false);
                    return;
                }

                CancelRestoreDebounce();

                if (_gameActive && !_librarySession && _activeProcessId == pid)
                {
                    ClearStartDebounce();
                    return;
                }

                // Start debounce: need 2 consecutive polls with the same PID.
                if (_pendingPid == pid)
                {
                    _pendingHits++;
                    _pendingName = result.ProcessName;
                }
                else
                {
                    _pendingPid = pid;
                    _pendingName = result.ProcessName;
                    _pendingHits = 1;
                }

                if (_pendingHits < 2)
                {
                    AppLog.Info($"Start debounce {_pendingHits}/2: {result.ProcessName} (pid {pid})");
                    return;
                }

                ClearStartDebounce();
                _gameActive = true;
                _librarySession = false;
                _activeGame = result.ProcessName;
                _activeProcessId = pid;
                AppLog.Info($"Game session started (fullscreen): {result.ProcessName} (pid {pid})");
                _hdr.EnableHdrForGame(_config.AllHdrDisplays);
                UpdateMenuState();
                return;
            }

            ClearStartDebounce();

            // Foreground is not a fullscreen game — for fullscreen sessions only,
            // schedule restore so Print Screen works on the desktop.
            if (_gameActive && !_librarySession)
                ScheduleRestoreDebounce(libraryExit: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("OnPoll failed", ex);
        }
    }

    private void ScheduleRestoreDebounce(bool libraryExit)
    {
        if (_restorePending)
            return;

        _restorePending = true;
        _restoreDebounceTimer.Stop();
        _restoreDebounceTimer.Interval = RestoreDebounceMs;
        _restoreDebounceTimer.Start();
        AppLog.Info(libraryExit
            ? $"Library process exited; restoring HDR in {RestoreDebounceMs}ms."
            : $"Left fullscreen/desktop; restoring HDR in {RestoreDebounceMs}ms.");
    }

    private void CancelRestoreDebounce()
    {
        if (!_restorePending && !_restoreDebounceTimer.Enabled)
            return;

        _restoreDebounceTimer.Stop();
        _restorePending = false;
    }

    private void ClearStartDebounce()
    {
        _pendingPid = null;
        _pendingName = null;
        _pendingHits = 0;
    }

    private void OnRestoreDebounceElapsed(object? sender, EventArgs e)
    {
        try
        {
            _restoreDebounceTimer.Stop();
            _restorePending = false;

            // Library path: cancel restore if process came back
            if (_librarySession)
            {
                var again = _library.FindRunningEnabledGame();
                if (again is { } hit)
                {
                    _gameActive = true;
                    _librarySession = true;
                    _activeGame = hit.Game.Name;
                    _activeProcessId = hit.ProcessId;
                    AppLog.Info($"Restore cancelled — library process again: {hit.Game.Name} (pid {hit.ProcessId})");
                    _hdr.EnableHdrForGame(_config.AllHdrDisplays);
                    UpdateMenuState();
                    return;
                }

                EndGameSessionImmediate();
                return;
            }

            // Fullscreen path: re-check Detect()
            var redetect = _detector.Detect();
            if (redetect.IsGameRunning && redetect.ProcessId is int newPid
                && !_library.IsLibraryExe(redetect.ProcessName))
            {
                _gameActive = true;
                _librarySession = false;
                _activeGame = redetect.ProcessName;
                _activeProcessId = newPid;
                AppLog.Info($"Restore cancelled — fullscreen game again: {redetect.ProcessName} (pid {newPid})");
                _hdr.EnableHdrForGame(_config.AllHdrDisplays);
                UpdateMenuState();
                return;
            }

            EndGameSessionImmediate();
        }
        catch (Exception ex)
        {
            AppLog.Error("OnRestoreDebounceElapsed failed", ex);
        }
    }

    private void EndGameSessionImmediate()
    {
        CancelRestoreDebounce();
        ClearStartDebounce();
        string? name = _activeGame;
        int? pid = _activeProcessId;
        bool wasLibrary = _librarySession;
        _gameActive = false;
        _librarySession = false;
        _activeGame = null;
        _activeProcessId = null;
        AppLog.Info(wasLibrary
            ? $"Restoring HDR after library game exit ({name ?? "?"} pid {pid?.ToString() ?? "?"})."
            : $"Restoring HDR after leaving fullscreen/desktop ({name ?? "?"} pid {pid?.ToString() ?? "?"}).");
        try
        {
            _hdr.RestoreAfterGame();
        }
        catch (Exception ex)
        {
            AppLog.Error("RestoreAfterGame threw", ex);
        }
        UpdateMenuState();
    }

    private void OnToggleArmed(object? sender, EventArgs e)
    {
        try
        {
            _armed = !_armed;
            _config.Enabled = _armed;
            _configService.Save(_config);

            if (!_armed && (_gameActive || _restorePending))
                EndGameSessionImmediate();

            UpdateMenuState();
        }
        catch (Exception ex)
        {
            AppLog.Error("OnToggleArmed failed", ex);
        }
    }

    private void OnToggleHdr(object? sender, EventArgs e)
    {
        try
        {
            if (!_hdr.IsHdrSupported(_config.AllHdrDisplays))
            {
                _tray.BalloonTipTitle = "AutoHDR";
                _tray.BalloonTipText = "HDR is not supported on any active display.";
                _tray.BalloonTipIcon = ToolTipIcon.Warning;
                _tray.ShowBalloonTip(4000);
                return;
            }

            bool currentlyOn = _hdr.IsHdrEnabled(_config.AllHdrDisplays);
            bool willEnable = !currentlyOn;
            CancelRestoreDebounce();

            bool claimRestore = _armed && willEnable;
            bool ok = _hdr.TryManualToggle(_config.AllHdrDisplays, claimRestore, out bool nowEnabled);

            _tray.BalloonTipTitle = "AutoHDR";
            if (!ok)
            {
                _tray.BalloonTipText = "Failed to toggle HDR.";
                _tray.BalloonTipIcon = ToolTipIcon.Error;
            }
            else if (nowEnabled)
            {
                _tray.BalloonTipText = _armed
                    ? "HDR on — will turn off after you exit the game."
                    : "HDR on.";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
            }
            else
            {
                _tray.BalloonTipText = "HDR off.";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
            }
            _tray.ShowBalloonTip(2500);
            UpdateMenuState();
        }
        catch (Exception ex)
        {
            AppLog.Error("OnToggleHdr failed", ex);
        }
    }

    private void OnGames(object? sender, EventArgs e) => OpenSettings(selectGamesTab: true);

    private void OnSettings(object? sender, EventArgs e) => OpenSettings(selectGamesTab: false);

    private void OpenSettings(bool selectGamesTab)
    {
        try
        {
            using var form = new SettingsForm(_configService, _config, _library, selectGamesTab);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _config = _configService.Load();
                _detector = new GameDetector(_config);
                _armed = _config.Enabled;
                _timer.Interval = Math.Clamp(_config.PollIntervalMs, 500, 10000);
                UpdateMenuState();
            }
            else
            {
                // Games tab may have saved library changes even on Cancel of general —
                // SettingsForm saves library live; refresh tooltip count.
                UpdateMenuState();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("OpenSettings failed", ex);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        try
        {
            CancelRestoreDebounce();
            if (_gameActive || _hdr.AutoHdrTurnedOnHdr)
                _hdr.RestoreAfterGame();
        }
        catch (Exception ex)
        {
            AppLog.Error("OnExit restore failed", ex);
        }

        _timer.Stop();
        _restoreDebounceTimer.Stop();
        _libraryScanTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        ExitThread();
    }

    private void UpdateMenuState()
    {
        try
        {
            _enableItem.Checked = _armed;
            _enableItem.Text = _armed ? "Disable AutoHDR" : "Enable AutoHDR";
            _tray.Text = BuildTooltip();
        }
        catch (Exception ex)
        {
            AppLog.Error("UpdateMenuState failed", ex);
        }
    }

    private string BuildTooltip()
    {
        string status = _armed ? "Armed" : "Disarmed";
        if (_gameActive && _activeGame != null)
            status += $" | {_activeGame}";
        else if (_hdr.IsHdrEnabled(_config.AllHdrDisplays))
            status += " | HDR on";
        string tip = $"AutoHDR — {status}";
        return tip.Length <= 63 ? tip : tip[..63];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _restoreDebounceTimer.Dispose();
            _libraryScanTimer.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
