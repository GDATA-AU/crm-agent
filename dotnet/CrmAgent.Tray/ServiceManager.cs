using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace CrmAgent.Tray;

/// <summary>
/// Thin wrapper around <see cref="ServiceController"/> for querying and
/// controlling the crm-agent Windows service.
/// Start/Stop fall back to an elevated <c>sc.exe</c> process when the
/// tray app is running without admin rights.
/// </summary>
public static class ServiceManager
{
    public const string ServiceName = "gdata-agent";

    public static ServiceControllerStatus GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            // Service not installed or SCM unreachable.
            return ServiceControllerStatus.Stopped;
        }
    }

    public static void Start()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 /* ACCESS_DENIED */)
        {
            RunElevated("sc.exe", $"start {ServiceName}");
        }
        catch (InvalidOperationException) when (IsAccessDenied())
        {
            RunElevated("sc.exe", $"start {ServiceName}");
        }
    }

    public static void Stop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 /* ACCESS_DENIED */)
        {
            RunElevated("sc.exe", $"stop {ServiceName}");
        }
        catch (InvalidOperationException) when (IsAccessDenied())
        {
            RunElevated("sc.exe", $"stop {ServiceName}");
        }
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>
    /// Returns true when the current user lacks permission to open the service handle.
    /// Used as a filter for <see cref="InvalidOperationException"/> thrown by
    /// <see cref="ServiceController.Status"/> before Start/Stop is even attempted.
    /// </summary>
    private static bool IsAccessDenied()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return false;
        }
        catch { return true; }
    }

    private static void RunElevated(string fileName, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        p?.WaitForExit(30_000);
    }
}
