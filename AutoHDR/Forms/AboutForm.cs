using System.Diagnostics;

namespace AutoHDR.Forms;

public sealed class AboutForm : Form
{
    private const string RepositoryUrl = "https://github.com/donijokay/AutoHDR";

    public AboutForm()
    {
        Text = "About AutoHDR";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        ClientSize = new Size(430, 245);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        try { Icon = AppIcon.Load(); } catch { /* ignore */ }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(18, 16, 18, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var icon = new PictureBox
        {
            Dock = DockStyle.Fill,
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 10, 0),
        };
        try { icon.Image = AppIcon.Load().ToBitmap(); } catch { /* ignore */ }
        header.Controls.Add(icon, 0, 0);

        var title = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = $"AutoHDR\nVersion {GetVersion()}",
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 0),
        };
        header.Controls.Add(title, 1, 0);
        root.Controls.Add(header, 0, 0);

        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(390, 0),
            Text = "Turns Windows HDR on when a game starts and restores it when the game closes.",
            Margin = new Padding(0, 14, 0, 0),
        };
        root.Controls.Add(description, 0, 1);

        var details = new Label
        {
            AutoSize = true,
            Text = "MIT License\n© 2026 donijokay",
            Margin = new Padding(0, 10, 0, 0),
        };
        root.Controls.Add(details, 0, 3);

        var repository = new LinkLabel
        {
            AutoSize = true,
            Text = RepositoryUrl,
            Margin = new Padding(0, 8, 0, 0),
        };
        repository.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = RepositoryUrl,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Ignore failures to open the user's default browser.
            }
        };
        root.Controls.Add(repository, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0),
        };
        var ok = new Button
        {
            AutoSize = true,
            Text = "OK",
            DialogResult = DialogResult.OK,
        };
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons, 0, 4);

        AcceptButton = ok;
        CancelButton = ok;
        Controls.Add(root);
    }

    private static string GetVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "Unknown version";
}
