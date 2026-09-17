using AutoHDR.Models;
using AutoHDR.Services;

namespace AutoHDR.Forms;

/// <summary>
/// System-tray host: arms/disarms AutoHDR, polls for games, toggles HDR.
/// Enables HDR while a fullscreen game is in the foreground. When focus
/// leaves fullscreen (e.g. desktop / Alt-Tab), restores previous HDR after
/// a short debounce so Windows Print Screen and Snipping Tool work again.
/// Returning to the fullscreen game within the debounce window cancels restore
/// and keeps HDR on without flicker.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int RestoreDebounceMs = 2500;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enableItem;
    private readonly ToolStripMenuItem _toggleHdrItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _restoreDebounceTimer;

    private readonly ConfigService _configService;
    private readonly HdrController _hdr;
    private AppConfig _config;
    private GameDetector _detector;

    private bool _armed;
    private bool _gameActive;
    private string? _activeGame;
    private int? _activeProcessId;
    private bool _unsupportedTipShown;
    private bool _restorePending;

    public TrayApplicationContext()
    {
        _configService = new ConfigService();
        _config = _configService.Load();
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
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, OnSettings));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
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

        // Startup balloon if HDR unsupported
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
                _tray.BalloonTipTitle = "AutoHDR";
                _tray.BalloonTipText = _armed
                    ? "Armed — HDR will turn on when a game goes fullscreen."
                    : "Disarmed — use the tray menu to enable.";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(2500);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup HDR check failed", ex);
        }

        UpdateMenuState();
        AppLog.Info("AutoHDR started.");
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        try
        {
            if (!_armed)
            {
                CancelRestoreDebounce();
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

            var result = _detector.Detect();
            if (result.IsGameRunning && result.ProcessId is int pid)
            {
                CancelRestoreDebounce();

                if (!_gameActive || _activeProcessId != pid)
                {
                    _gameActive = true;
                    _activeGame = result.ProcessName;
                    _activeProcessId = pid;
                    AppLog.Info($"Game session started: {result.ProcessName} (pid {pid})");
                    _hdr.EnableHdrForGame(_config.AllHdrDisplays);
                    UpdateMenuState();
                }
                return;
            }

            // Foreground is not a fullscreen game (desktop / Alt-Tab / other app).
            // Always schedule restore so Print Screen works on the desktop even if
            // the game process is still alive. Debounce avoids HDR flicker on brief focus loss.
            if (_gameActive)
                ScheduleRestoreDebounce();
        }
        catch (Exception ex)
        {
            AppLog.Error("OnPoll failed", ex);
        }
    }

    private void ScheduleRestoreDebounce()
    {
        if (_restorePending)
            return;

        _restorePending = true;
        _restoreDebounceTimer.Stop();
        _restoreDebounceTimer.Interval = RestoreDebounceMs;
        _restoreDebounceTimer.Start();
        AppLog.Info($"Left fullscreen/desktop; restoring HDR in {RestoreDebounceMs}ms.");
    }

    private void CancelRestoreDebounce()
    {
        if (!_restorePending && !_restoreDebounceTimer.Enabled)
            return;

        _restoreDebounceTimer.Stop();
        _restorePending = false;
    }

    private void OnRestoreDebounceElapsed(object? sender, EventArgs e)
    {
        try
        {
            _restoreDebounceTimer.Stop();
            _restorePending = false;

            // Re-check: user may have returned to the fullscreen game.
            var redetect = _detector.Detect();
            if (redetect.IsGameRunning && redetect.ProcessId is int newPid)
            {
                _gameActive = true;
                _activeGame = redetect.ProcessName;
                _activeProcessId = newPid;
                AppLog.Info($"Restore cancelled — fullscreen game again: {redetect.ProcessName} (pid {newPid})");
                _hdr.EnableHdrForGame(_config.AllHdrDisplays);
                UpdateMenuState();
                return;
            }

            // Still not fullscreen (desktop or other window) — restore even if old process lives.
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
        string? name = _activeGame;
        int? pid = _activeProcessId;
        _gameActive = false;
        _activeGame = null;
        _activeProcessId = null;
        AppLog.Info($"Restoring HDR after leaving fullscreen/desktop ({name ?? "?"} pid {pid?.ToString() ?? "?"}).");
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

            // Manual toggle should not fight an active game session restore bookkeeping
            CancelRestoreDebounce();
            if (_gameActive)
            {
                _gameActive = false;
                _activeGame = null;
                _activeProcessId = null;
            }

            bool ok = _hdr.ToggleHdr(_config.AllHdrDisplays);
            _tray.BalloonTipTitle = "AutoHDR";
            _tray.BalloonTipText = ok
                ? (_hdr.IsHdrEnabled(_config.AllHdrDisplays) ? "HDR enabled." : "HDR disabled.")
                : "Failed to toggle HDR.";
            _tray.BalloonTipIcon = ok ? ToolTipIcon.Info : ToolTipIcon.Error;
            _tray.ShowBalloonTip(2000);
            UpdateMenuState();
        }
        catch (Exception ex)
        {
            AppLog.Error("OnToggleHdr failed", ex);
        }
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        try
        {
            using var form = new SettingsForm(_configService, _config);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _config = _configService.Load();
                _detector = new GameDetector(_config);
                _armed = _config.Enabled;
                _timer.Interval = Math.Clamp(_config.PollIntervalMs, 500, 10000);
                UpdateMenuState();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("OnSettings failed", ex);
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
        // NotifyIcon.Text max ~63 chars
        string tip = $"AutoHDR — {status}";
        return tip.Length <= 63 ? tip : tip[..63];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _restoreDebounceTimer.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
