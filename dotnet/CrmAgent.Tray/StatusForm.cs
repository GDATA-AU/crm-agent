using System.ServiceProcess;

namespace CrmAgent.Tray;

/// <summary>
/// Small status popup that appears when the user left-clicks the tray icon.
/// Shows service status and provides quick Start/Stop and Configure actions.
/// </summary>
public sealed class StatusForm : Form
{
    /// <summary>Raised when the user clicks Configure, so the tray context can open the ConnectForm.</summary>
    public event Action? ConfigureRequested;

    private readonly Label _statusLabel;
    private readonly Label _portalLabel;
    private readonly Button _startStopBtn;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public StatusForm()
    {
        Text = "GDATA CRM Agent";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Width = 380;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TopMost = true;

        // Anchor near the system tray (bottom-right of working area)
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Location = new Point(area.Right - Width - 20, area.Bottom - 180);

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
            Margin = new Padding(0, 8, 0, 0),
        };
        btnRow.Controls.Add(_startStopBtn);
        btnRow.Controls.Add(configBtn);

        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            WrapContents = false,
        };
        panel.Controls.Add(_statusLabel);
        panel.Controls.Add(_portalLabel);
        panel.Controls.Add(btnRow);

        Controls.Add(panel);

        RefreshDisplay();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _refreshTimer.Tick += (_, _) => RefreshDisplay();
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
