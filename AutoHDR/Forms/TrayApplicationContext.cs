using AutoHDR.Models;
using AutoHDR.Services;

namespace AutoHDR.Forms;

/// <summary>
/// System-tray host: arms/disarms AutoHDR, polls for games, toggles HDR.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enableItem;
    private readonly ToolStripMenuItem _toggleHdrItem;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly ConfigService _configService;
    private readonly HdrController _hdr;
    private AppConfig _config;
    private GameDetector _detector;

    private bool _armed;
    private bool _gameActive;
    private string? _activeGame;
    private bool _unsupportedTipShown;

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

        // Startup balloon if HDR unsupported
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

        UpdateMenuState();
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (!_armed)
        {
            if (_gameActive)
                EndGameSession();
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
        if (result.IsGameRunning)
        {
            if (!_gameActive || !string.Equals(_activeGame, result.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                _gameActive = true;
                _activeGame = result.ProcessName;
                _hdr.EnableHdrForGame(_config.AllHdrDisplays);
                UpdateMenuState();
            }
        }
        else if (_gameActive)
        {
            EndGameSession();
        }
    }

    private void EndGameSession()
    {
        _gameActive = false;
        _activeGame = null;
        _hdr.RestoreAfterGame();
        UpdateMenuState();
    }

    private void OnToggleArmed(object? sender, EventArgs e)
    {
        _armed = !_armed;
        _config.Enabled = _armed;
        _configService.Save(_config);

        if (!_armed && _gameActive)
            EndGameSession();

        UpdateMenuState();
    }

    private void OnToggleHdr(object? sender, EventArgs e)
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
        if (_gameActive)
        {
            _gameActive = false;
            _activeGame = null;
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

    private void OnSettings(object? sender, EventArgs e)
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

    private void OnExit(object? sender, EventArgs e)
    {
        if (_gameActive || _hdr.AutoHdrTurnedOnHdr)
            _hdr.RestoreAfterGame();

        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        ExitThread();
    }

    private void UpdateMenuState()
    {
        _enableItem.Checked = _armed;
        _enableItem.Text = _armed ? "Disable AutoHDR" : "Enable AutoHDR";
        _tray.Text = BuildTooltip();
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
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
