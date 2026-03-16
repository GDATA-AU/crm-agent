using System.ServiceProcess;

namespace CrmAgent.Tray;

/// <summary>
/// Small status popup that appears when the user left-clicks the tray icon.
/// Shows service status, provides quick Start/Stop and Configure actions,
/// and displays a live activity feed from the agent log.
/// </summary>
public sealed class StatusForm : Form
{
    /// <summary>Raised when the user clicks Configure, so the tray context can open the ConnectForm.</summary>
    public event Action? ConfigureRequested;

    private readonly Label _statusLabel;
    private readonly Label _portalLabel;
    private readonly Button _startStopBtn;
    private readonly ListBox _activityList;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly LogTailer _logTailer = new();

    private static readonly Color DimText = Color.FromArgb(140, 140, 140);

    public StatusForm()
    {
        Text = "GDATA CRM Agent";
        Icon = TrayApplicationContext.LoadAppIcon();
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 400);
        Size = new Size(560, 480);
        TopMost = true;

        _statusLabel = new Label
        {
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont?.FontFamily ?? SystemFonts.MessageBoxFont!.FontFamily, 11f),
        };
        _portalLabel = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        _startStopBtn = new Button { AutoSize = true };
        var configBtn = new Button { Text = "Configure…", AutoSize = true };

        _startStopBtn.Click += OnStartStop;
        configBtn.Click += (_, _) =>
        {
            Close();
            ConfigureRequested?.Invoke();
        };

        var btnRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
        };
        btnRow.Controls.Add(_startStopBtn);
        btnRow.Controls.Add(configBtn);

        // -- Activity feed --
        var activityLabel = new Label
        {
            Text = "Recent Activity",
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont?.FontFamily ?? SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 4),
        };

        _activityList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            SelectionMode = SelectionMode.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 20,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Consolas", 8.5f, FontStyle.Regular),
        };
        _activityList.DrawItem += OnDrawActivityItem;

        // -- Top panel (status + buttons) --
        var topPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(14, 14, 14, 4),
            WrapContents = false,
        };
        topPanel.Controls.Add(_statusLabel);
        topPanel.Controls.Add(_portalLabel);
        topPanel.Controls.Add(btnRow);
        topPanel.Controls.Add(activityLabel);

        // -- Activity panel fills remaining space --
        var activityPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 0, 14, 14),
        };
        activityPanel.Controls.Add(_activityList);

        Controls.Add(activityPanel);
        Controls.Add(topPanel);

        RefreshDisplay();
        LoadActivity();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3_000 };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshDisplay();
            LoadActivity();
        };
        _refreshTimer.Start();
    }

    private void RefreshDisplay()
    {
        var status = ServiceManager.GetStatus();
        var settings = ConfigStore.Load();

        (_statusLabel.Text, _statusLabel.ForeColor) = status switch
        {
            ServiceControllerStatus.Running      => ("● Running",   Color.Green),
            ServiceControllerStatus.Stopped      => ("○ Stopped",   Color.Red),
            ServiceControllerStatus.StartPending => ("◌ Starting…", Color.DarkOrange),
            ServiceControllerStatus.StopPending  => ("◌ Stopping…", Color.DarkOrange),
            _                                    => ("? Unknown",   SystemColors.GrayText),
        };

        _portalLabel.Text = !string.IsNullOrEmpty(settings?.PortalUrl)
            ? $"Portal: {settings!.PortalUrl}"
            : "Portal: not configured";

        _startStopBtn.Text = status == ServiceControllerStatus.Running ? "Stop" : "Start";
    }

    private void LoadActivity()
    {
        var entries = _logTailer.ReadNewEntries();
        if (entries.Count == 0) return;

        _activityList.BeginUpdate();
        foreach (var entry in entries)
        {
            _activityList.Items.Add(entry);
            // Cap at 200 items to avoid unbounded growth.
            if (_activityList.Items.Count > 200)
                _activityList.Items.RemoveAt(0);
        }
        _activityList.EndUpdate();

        // Auto-scroll to the latest entry.
        _activityList.TopIndex = _activityList.Items.Count - 1;
    }

    private static void OnDrawActivityItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var list = (ListBox)sender!;
        var item = list.Items[e.Index];

        e.DrawBackground();

        if (item is LogTailer.LogEntry entry)
        {
            var time = entry.Timestamp.ToString("HH:mm:ss");
            var levelTag = entry.Level switch
            {
                "Error" or "Fatal" => "ERR",
                "Warning" => "WRN",
                "Debug" or "Verbose" => "DBG",
                _ => "INF",
            };
            var levelColor = entry.Level switch
            {
                "Error" or "Fatal" => Color.FromArgb(255, 100, 100),
                "Warning" => Color.FromArgb(255, 200, 80),
                "Debug" or "Verbose" => DimText,
                _ => Color.FromArgb(100, 200, 255),
            };

            var x = e.Bounds.X + 4;
            var y = e.Bounds.Y;
            var height = e.Bounds.Height;

            // Timestamp
            TextRenderer.DrawText(e.Graphics, time, list.Font, new Rectangle(x, y, 70, height),
                DimText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            x += 72;

            // Level badge
            TextRenderer.DrawText(e.Graphics, levelTag, list.Font, new Rectangle(x, y, 32, height),
                levelColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            x += 36;

            // Message
            TextRenderer.DrawText(e.Graphics, entry.Message, list.Font,
                new Rectangle(x, y, e.Bounds.Right - x - 4, height),
                list.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, item.ToString(), list.Font, e.Bounds,
                list.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    private void OnStartStop(object? sender, EventArgs e)
    {
        try
        {
            if (ServiceManager.GetStatus() == ServiceControllerStatus.Running)
                ServiceManager.Stop();
            else
                ServiceManager.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Service Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshDisplay();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}
