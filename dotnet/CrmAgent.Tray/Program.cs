using CrmAgent.Tray;

// Prevent multiple tray instances using a named mutex.
bool createdNew;
using var mutex = new Mutex(true, "Global\\CrmAgentTray_4F8A2C1D", out createdNew);
if (!createdNew)
    return;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new TrayApplicationContext());
