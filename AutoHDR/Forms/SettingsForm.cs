using AutoHDR.Models;
using AutoHDR.Services;

namespace AutoHDR.Forms;

public sealed class SettingsForm : Form
{
    private readonly ConfigService _configService;
    private readonly AppConfig _config;
    private readonly TextBox _whitelistBox;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox _allDisplays;
    private readonly NumericUpDown _pollInterval;
    private readonly NumericUpDown _coverage;

    public SettingsForm(ConfigService configService, AppConfig config)
    {
        _configService = configService;
        _config = config;

        Text = "AutoHDR Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 420);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;

        var lblWhitelist = new Label
        {
            Text = "Whitelist (one exe name per line; empty = any fullscreen game):",
            AutoSize = true,
            Location = new Point(12, 12),
        };

        _whitelistBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(12, 36),
            Size = new Size(436, 180),
            AcceptsReturn = true,
            Text = string.Join(Environment.NewLine, config.Whitelist),
        };

        _startWithWindows = new CheckBox
        {
            Text = "Start with Windows",
            AutoSize = true,
            Location = new Point(12, 230),
            Checked = config.StartWithWindows,
        };

        _allDisplays = new CheckBox
        {
            Text = "Apply HDR to all HDR-capable displays (unchecked = primary only)",
            AutoSize = true,
            Location = new Point(12, 258),
            Checked = config.AllHdrDisplays,
        };

        var lblPoll = new Label
        {
            Text = "Poll interval (ms):",
            AutoSize = true,
            Location = new Point(12, 292),
        };
        _pollInterval = new NumericUpDown
        {
            Minimum = 500,
            Maximum = 10000,
            Increment = 100,
            Value = Math.Clamp(config.PollIntervalMs, 500, 10000),
            Location = new Point(160, 288),
            Width = 100,
        };

        var lblCov = new Label
        {
            Text = "Fullscreen coverage (0.50–1.00):",
            AutoSize = true,
            Location = new Point(12, 324),
        };
        _coverage = new NumericUpDown
        {
            Minimum = 0.50m,
            Maximum = 1.00m,
            DecimalPlaces = 2,
            Increment = 0.01m,
            Value = (decimal)Math.Clamp(config.FullscreenCoverageThreshold, 0.5, 1.0),
            Location = new Point(220, 320),
            Width = 80,
        };

        var lblPath = new Label
        {
            Text = $"Config: {_configService.ConfigPath}",
            AutoSize = false,
            Location = new Point(12, 354),
            Size = new Size(436, 18),
            ForeColor = SystemColors.GrayText,
        };

        var btnOk = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(272, 380),
            Size = new Size(85, 28),
        };
        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(363, 380),
            Size = new Size(85, 28),
        };

        btnOk.Click += (_, _) => ApplyAndSave();

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[]
        {
            lblWhitelist, _whitelistBox,
            _startWithWindows, _allDisplays,
            lblPoll, _pollInterval,
            lblCov, _coverage,
            lblPath, btnOk, btnCancel,
        });
    }

    private void ApplyAndSave()
    {
        _config.Whitelist = _whitelistBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        _config.StartWithWindows = _startWithWindows.Checked;
        _config.AllHdrDisplays = _allDisplays.Checked;
        _config.PollIntervalMs = (int)_pollInterval.Value;
        _config.FullscreenCoverageThreshold = (double)_coverage.Value;

        _configService.Save(_config);
        try { StartupService.SetStartWithWindows(_config.StartWithWindows); }
        catch { /* registry may fail in restricted contexts */ }
    }
}
