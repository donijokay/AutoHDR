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
        ClientSize = new Size(520, 520);
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = true;
        Padding = new Padding(0);
        AutoScaleMode = AutoScaleMode.Dpi;

        try { Icon = AppIcon.Load(); } catch { /* ignore */ }

        // --- Root layout ---
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16, 16, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // content
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // config path
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0),
        };
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // Game detection
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // HDR
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // General

        // === Game detection ===
        var grpGame = new GroupBox
        {
            Text = "Game detection",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0, 0, 0, 10),
        };
        var gameInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 4, 0, 0),
        };
        gameInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        gameInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _whitelistBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            AcceptsReturn = true,
            Font = new Font("Consolas", 9.5f),
            Text = string.Join(Environment.NewLine, config.Whitelist),
            Margin = new Padding(0, 0, 0, 6),
        };

        var lblWhitelistHelp = new Label
        {
            Text = "One executable name per line (e.g. Cyberpunk2077). Leave empty to detect any fullscreen game. Stubborn / windowed-borderless titles can be whitelisted by exe name (e.g. Resonance).",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 48,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0),
        };

        gameInner.Controls.Add(_whitelistBox, 0, 0);
        gameInner.Controls.Add(lblWhitelistHelp, 0, 1);
        grpGame.Controls.Add(gameInner);

        // === HDR ===
        var grpHdr = new GroupBox
        {
            Text = "HDR",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0, 0, 0, 10),
        };
        _allDisplays = new CheckBox
        {
            Text = "Apply to all HDR-capable displays (unchecked = primary only)",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 36,
            Checked = config.AllHdrDisplays,
            Margin = new Padding(0, 4, 0, 0),
            Padding = new Padding(0, 2, 0, 2),
        };
        // Ensure text wraps within the group box width
        _allDisplays.MaximumSize = new Size(460, 0);
        grpHdr.Controls.Add(_allDisplays);

        // === General ===
        var grpGeneral = new GroupBox
        {
            Text = "General",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0),
        };
        var generalInner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(0, 4, 0, 0),
        };
        generalInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        generalInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        generalInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        generalInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        generalInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _startWithWindows = new CheckBox
        {
            Text = "Start with Windows",
            AutoSize = true,
            Checked = config.StartWithWindows,
            Margin = new Padding(0, 4, 0, 8),
        };
        generalInner.Controls.Add(_startWithWindows, 0, 0);
        generalInner.SetColumnSpan(_startWithWindows, 2);

        var lblPoll = new Label
        {
            Text = "Poll interval (ms)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6),
        };
        _pollInterval = new NumericUpDown
        {
            Minimum = 500,
            Maximum = 10000,
            Increment = 100,
            Value = Math.Clamp(config.PollIntervalMs, 500, 10000),
            Width = 110,
            Margin = new Padding(0, 4, 0, 4),
            Anchor = AnchorStyles.Right,
        };
        generalInner.Controls.Add(lblPoll, 0, 1);
        generalInner.Controls.Add(_pollInterval, 1, 1);

        var lblCov = new Label
        {
            Text = "Fullscreen coverage (0.50–1.00)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6),
        };
        _coverage = new NumericUpDown
        {
            Minimum = 0.50m,
            Maximum = 1.00m,
            DecimalPlaces = 2,
            Increment = 0.01m,
            Value = (decimal)Math.Clamp(config.FullscreenCoverageThreshold, 0.5, 1.0),
            Width = 110,
            Margin = new Padding(0, 4, 0, 4),
            Anchor = AnchorStyles.Right,
        };
        generalInner.Controls.Add(lblCov, 0, 2);
        generalInner.Controls.Add(_coverage, 1, 2);

        grpGeneral.Controls.Add(generalInner);

        content.Controls.Add(grpGame, 0, 0);
        content.Controls.Add(grpHdr, 0, 1);
        content.Controls.Add(grpGeneral, 0, 2);

        // === Config path ===
        var pathPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(0),
            Margin = new Padding(0, 4, 0, 4),
        };
        var lblPathCaption = new Label
        {
            Text = "Config:",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(0, 5),
        };
        var lblPath = new Label
        {
            Text = _configService.ConfigPath,
            AutoSize = false,
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(52, 5),
            Size = new Size(430, 20),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };
        pathPanel.Controls.Add(lblPathCaption);
        pathPanel.Controls.Add(lblPath);
        pathPanel.Resize += (_, _) =>
        {
            lblPath.Width = Math.Max(40, pathPanel.ClientSize.Width - 52);
        };

        // === Buttons ===
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 4),
            Margin = new Padding(0),
        };
        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(96, 32),
            Margin = new Padding(8, 0, 0, 0),
        };
        var btnOk = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Size = new Size(96, 32),
            Margin = new Padding(0),
        };
        btnOk.Click += (_, _) => ApplyAndSave();
        buttonPanel.Controls.Add(btnCancel);
        buttonPanel.Controls.Add(btnOk);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        root.Controls.Add(content, 0, 0);
        root.Controls.Add(pathPanel, 0, 1);
        root.Controls.Add(buttonPanel, 0, 2);

        Controls.Add(root);
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
