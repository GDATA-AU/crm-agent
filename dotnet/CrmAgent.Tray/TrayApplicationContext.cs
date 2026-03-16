using System.ServiceProcess;
using System.Runtime.InteropServices;

namespace CrmAgent.Tray;

/// <summary>
/// Drives the system tray icon lifecycle.
/// On first run (no config) immediately shows <see cref="ConnectForm"/>.
/// A background timer refreshes the service status every 10 seconds.
/// Left-click opens the <see cref="StatusForm"/> popup.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private StatusForm? _statusForm;

    public TrayApplicationContext()
    {
        _statusMenuItem = new ToolStripMenuItem("Checking status…") { Enabled = false };

        var headerItem = new ToolStripMenuItem("GDATA CRM Agent")
        {
            Enabled = false,
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont!, FontStyle.Bold),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(headerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configure…", null, (_, _) => ShowConnectForm());
        menu.Items.Add("Open Log Folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "GDATA CRM Agent",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.MouseClick += OnTrayClick;

        _pollTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _pollTimer.Tick += (_, _) => RefreshStatus();
        _pollTimer.Start();

        // Defer first-run check until after the message loop starts
        Application.Idle += OnFirstIdle;
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        RefreshStatus();
        if (!ConfigStore.IsConfigured())
            ShowConnectForm();
    }

    private void RefreshStatus()
    {
        try
        {
            var status = ServiceManager.GetStatus();
            var (text, tip) = status switch
            {
                ServiceControllerStatus.Running      => ("● Running",   "GDATA CRM Agent | Running"),
                ServiceControllerStatus.Stopped      => ("○ Stopped",   "GDATA CRM Agent | Stopped"),
                ServiceControllerStatus.StartPending => ("◌ Starting…", "GDATA CRM Agent | Starting"),
                ServiceControllerStatus.StopPending  => ("◌ Stopping…", "GDATA CRM Agent | Stopping"),
                _                                    => ("? Unknown",   "GDATA CRM Agent | Unknown"),
            };
            _statusMenuItem.Text = text;
            // NotifyIcon.Text is capped at 63 characters
            _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
        }
        catch { /* never crash the tray */ }
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_statusForm is { Visible: true })
        {
            _statusForm.BringToFront();
            return;
        }

        _statusForm = new StatusForm();
        _statusForm.FormClosed += (_, _) => _statusForm = null;
        _statusForm.ConfigureRequested += ShowConnectForm;
        _statusForm.Show();
    }

    private void ShowConnectForm()
    {
        using var f = new ConnectForm();
        f.ShowDialog();
        RefreshStatus();
    }

    private static void OpenLogFolder()
    {
        var path = ConfigStore.ConfigDirectory;
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private void Exit()
    {
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    /// <summary>
    /// Renders the ⚡ HIGH VOLTAGE emoji (U+26A1) from Segoe UI Emoji at 32×32
    /// and converts the bitmap to an <see cref="Icon"/> for the system tray.
    /// Falls back to the generic application icon if the font is unavailable.
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        const int size = 32;
        const string lightning = "⚡";

        try
        {
            using var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var font = new Font("Segoe UI Emoji", size * 0.72f, GraphicsUnit.Pixel);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(lightning, font, Brushes.White, new RectangleF(0, 0, size, size), sf);

            // Convert Bitmap → Icon. Clone so the native HICON can be freed.
            var hIcon = bmp.GetHicon();
            using var temp = Icon.FromHandle(hIcon);
            var icon = (Icon)temp.Clone();
            DestroyIcon(hIcon);
            return icon;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
