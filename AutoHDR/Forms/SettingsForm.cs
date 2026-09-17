using AutoHDR.Models;
using AutoHDR.Services;

namespace AutoHDR.Forms;

public sealed class SettingsForm : Form
{
    private readonly ConfigService _configService;
    private readonly AppConfig _config;
    private readonly GameLibraryService _library;

    private readonly TabControl _tabs;
    private readonly ListView _gamesList;
    private readonly Label _gamesCountLabel;
    private readonly TextBox _whitelistBox;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox _allDisplays;
    private readonly NumericUpDown _pollInterval;
    private readonly NumericUpDown _coverage;
    private readonly ListBox _customFoldersList;

    private bool _suppressGameCheck;

    public SettingsForm(
        ConfigService configService,
        AppConfig config,
        GameLibraryService library,
        bool selectGamesTab = false)
    {
        _configService = configService;
        _config = config;
        _library = library;

        Text = "AutoHDR Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 580);
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        try { Icon = AppIcon.Load(); } catch { /* ignore */ }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 14, 14, 10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _tabs = new TabControl { Dock = DockStyle.Fill };

        // ── Games tab ────────────────────────────────────────────────────
        var tabGames = new TabPage("Games") { Padding = new Padding(10) };
        var gamesLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        gamesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gamesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        gamesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gamesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _gamesCountLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            Text = "Enabled: 0",
        };

        var grpGames = new GroupBox
        {
            Text = "Game library (On = HDR when process starts)",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10),
        };

        _gamesList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
            ShowItemToolTips = true,
        };
        _gamesList.Columns.Add("Name", 220);
        _gamesList.Columns.Add("Source", 70);
        _gamesList.Columns.Add("Status", 90);
        _gamesList.Columns.Add("Exe", 160);
        _gamesList.ItemChecked += OnGameItemChecked;
        grpGames.Controls.Add(_gamesList);

        var gamesButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };
        var btnRefresh = new Button { Text = "Refresh", Size = new Size(100, 30), Margin = new Padding(0, 0, 8, 0) };
        btnRefresh.Click += (_, _) => { _library.Scan(force: true); ReloadGamesList(); };
        var btnAddFolder = new Button { Text = "Add folder…", Size = new Size(110, 30), Margin = new Padding(0, 0, 8, 0) };
        btnAddFolder.Click += OnAddFolder;
        var btnAddExe = new Button { Text = "Add exe…", Size = new Size(100, 30), Margin = new Padding(0, 0, 8, 0) };
        btnAddExe.Click += OnAddExe;
        var btnEnableAll = new Button { Text = "Enable all", Size = new Size(100, 30), Margin = new Padding(0, 0, 8, 0) };
        btnEnableAll.Click += (_, _) => SetAllGamesEnabled(true);
        var btnDisableAll = new Button { Text = "Disable all", Size = new Size(100, 30) };
        btnDisableAll.Click += (_, _) => SetAllGamesEnabled(false);
        gamesButtons.Controls.AddRange(new Control[] { btnRefresh, btnAddFolder, btnAddExe, btnEnableAll, btnDisableAll });

        var grpFolders = new GroupBox
        {
            Text = "Custom folders",
            Dock = DockStyle.Top,
            Height = 100,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 8, 0, 0),
        };
        _customFoldersList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
        };
        var folderBtnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 90,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(6, 0, 0, 0),
        };
        var btnRemoveFolder = new Button { Text = "Remove", Size = new Size(80, 28) };
        btnRemoveFolder.Click += OnRemoveFolder;
        folderBtnPanel.Controls.Add(btnRemoveFolder);
        grpFolders.Controls.Add(_customFoldersList);
        grpFolders.Controls.Add(folderBtnPanel);

        gamesLayout.Controls.Add(_gamesCountLabel, 0, 0);
        gamesLayout.Controls.Add(grpGames, 0, 1);
        gamesLayout.Controls.Add(gamesButtons, 0, 2);
        gamesLayout.Controls.Add(grpFolders, 0, 3);
        tabGames.Controls.Add(gamesLayout);

        // ── Detection tab ────────────────────────────────────────────────
        var tabDetect = new TabPage("Detection") { Padding = new Padding(10) };
        var detectLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        detectLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        detectLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var grpWhitelist = new GroupBox
        {
            Text = "Fullscreen fallback whitelist (optional)",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 12),
        };
        var whiteInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        whiteInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        whiteInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _whitelistBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            AcceptsReturn = true,
            Font = new Font("Consolas", 9.5f),
            Text = string.Join(Environment.NewLine, config.Whitelist),
        };
        var lblWhitelistHelp = new Label
        {
            Text = "Used only for games NOT in the library (or as extra filter). One exe name per line. Leave empty to detect any fullscreen game. Library games with Enabled=On skip this path and enable HDR on process start.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 56,
            ForeColor = SystemColors.GrayText,
        };
        whiteInner.Controls.Add(_whitelistBox, 0, 0);
        whiteInner.Controls.Add(lblWhitelistHelp, 0, 1);
        grpWhitelist.Controls.Add(whiteInner);

        var grpCov = new GroupBox
        {
            Text = "Fullscreen coverage",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0, 10, 0, 0),
        };
        var covRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 4, 0, 0),
        };
        covRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        covRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lblCov = new Label
        {
            Text = "Coverage threshold (0.50–1.00)",
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
        covRow.Controls.Add(lblCov, 0, 0);
        covRow.Controls.Add(_coverage, 1, 0);
        grpCov.Controls.Add(covRow);

        detectLayout.Controls.Add(grpWhitelist, 0, 0);
        detectLayout.Controls.Add(grpCov, 0, 1);
        tabDetect.Controls.Add(detectLayout);

        // ── General tab ──────────────────────────────────────────────────
        var tabGeneral = new TabPage("General") { Padding = new Padding(10) };
        var generalOuter = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
        };

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
            Height = 40,
            Checked = config.AllHdrDisplays,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 4, 0, 0),
        };
        grpHdr.Controls.Add(_allDisplays);

        var grpGeneral = new GroupBox
        {
            Text = "General",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 12),
        };
        var generalInner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0, 4, 0, 0),
        };
        generalInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        generalInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _startWithWindows = new CheckBox
        {
            Text = "Start with Windows",
            AutoSize = true,
            Checked = config.StartWithWindows,
            Margin = new Padding(0, 4, 0, 10),
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
        grpGeneral.Controls.Add(generalInner);

        generalOuter.Controls.Add(grpHdr, 0, 0);
        generalOuter.Controls.Add(grpGeneral, 0, 1);
        tabGeneral.Controls.Add(generalOuter);

        _tabs.TabPages.Add(tabGames);
        _tabs.TabPages.Add(tabDetect);
        _tabs.TabPages.Add(tabGeneral);
        if (selectGamesTab)
            _tabs.SelectedTab = tabGames;

        // Path + buttons
        var pathPanel = new Panel { Dock = DockStyle.Top, Height = 28, Margin = new Padding(0, 4, 0, 4) };
        var lblPathCaption = new Label
        {
            Text = "Config:",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(0, 5),
        };
        var lblPath = new Label
        {
            Text = _configService.ConfigPath + "  ·  " + _library.LibraryPath,
            AutoSize = false,
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(52, 5),
            Size = new Size(540, 20),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };
        pathPanel.Controls.Add(lblPathCaption);
        pathPanel.Controls.Add(lblPath);
        pathPanel.Resize += (_, _) =>
        {
            lblPath.Width = Math.Max(40, pathPanel.ClientSize.Width - 52);
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 4),
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

        root.Controls.Add(_tabs, 0, 0);
        root.Controls.Add(pathPanel, 0, 1);
        root.Controls.Add(buttonPanel, 0, 2);
        Controls.Add(root);

        // Initial scan when opening settings, then populate UI
        try { _library.Scan(force: true); } catch { /* ignore */ }
        ReloadGamesList();
        ReloadCustomFolders();
    }

    private void ReloadGamesList()
    {
        _suppressGameCheck = true;
        try
        {
            _gamesList.BeginUpdate();
            _gamesList.Items.Clear();
            foreach (var g in _library.Games.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ListViewItem(g.Name)
                {
                    Checked = g.Enabled,
                    Tag = g.Id,
                    ToolTipText = string.IsNullOrEmpty(g.ExePath) ? g.ExeName : g.ExePath,
                };
                item.SubItems.Add(g.Source);
                item.SubItems.Add(g.IsFound ? "Found" : "Not found");
                item.SubItems.Add(g.ExeName);
                if (!g.IsFound)
                    item.ForeColor = SystemColors.GrayText;
                _gamesList.Items.Add(item);
            }
            _gamesList.EndUpdate();
            UpdateGamesCountLabel();
        }
        finally
        {
            _suppressGameCheck = false;
        }
    }

    private void ReloadCustomFolders()
    {
        _customFoldersList.Items.Clear();
        foreach (var f in _library.CustomFolders)
            _customFoldersList.Items.Add(f);
    }

    private void UpdateGamesCountLabel()
    {
        int enabled = _library.EnabledCount;
        int total = _library.Games.Count;
        _gamesCountLabel.Text = $"Enabled: {enabled} / {total} games  ·  New installs default to On";
    }

    private void OnGameItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_suppressGameCheck) return;
        if (e.Item.Tag is not string id) return;
        _library.SetEnabled(id, e.Item.Checked);
        UpdateGamesCountLabel();
    }

    private void SetAllGamesEnabled(bool enabled)
    {
        var updates = _library.Games.Select(g => (g.Id, enabled));
        _library.SetAllEnabled(updates);
        ReloadGamesList();
    }

    private void OnAddFolder(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select a folder that contains games",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        if (_library.AddCustomFolder(dlg.SelectedPath))
        {
            ReloadCustomFolders();
            ReloadGamesList();
        }
    }

    private void OnRemoveFolder(object? sender, EventArgs e)
    {
        if (_customFoldersList.SelectedItem is not string folder)
            return;
        if (_library.RemoveCustomFolder(folder))
        {
            ReloadCustomFolders();
            // Re-scan so custom-only games may show Not found
            _library.Scan(force: true);
            ReloadGamesList();
        }
    }

    private void OnAddExe(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select game executable",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        var entry = _library.AddManualExe(dlg.FileName);
        if (entry != null)
            ReloadGamesList();
    }

    private void ApplyAndSave()
    {
        // Persist checkbox states (already saved live, but sync once more)
        var updates = new List<(string Id, bool Enabled)>();
        foreach (ListViewItem item in _gamesList.Items)
        {
            if (item.Tag is string id)
                updates.Add((id, item.Checked));
        }
        _library.SetAllEnabled(updates);

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
        catch { /* registry may fail */ }
    }
}
