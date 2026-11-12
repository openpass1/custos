using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using Custos.Core;

namespace Custos.Gui;

/// <summary>
/// «Start the service» and «Stop the service» of the tray menu. Only administrators may start or stop a Windows
/// service, so the interface starts a second copy of itself with administrator rights (the Windows UAC prompt). That
/// copy does nothing else: it starts or stops the service, waits for it, says so if it failed, and quits.
/// </summary>
internal static class ServiceControl
{
    public const string StartArgument = "--service-start";
    public const string StopArgument = "--service-stop";

    /// <summary>The user said "No" to the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>A run being stopped finishes its current file first; a big one on a slow share takes a while.</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);

    /// <summary>The state of the service as Windows sees it, or null when it is not installed or cannot be asked.</summary>
    public static ServiceControllerStatus? Query()
    {
        try
        {
            using var service = new ServiceController(CustosInfo.ServiceName);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Starts the copy with administrator rights; false when the user refused the UAC prompt.</summary>
    public static bool RequestElevated(bool start)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath, start ? StartArgument : StopArgument)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            using var process = Process.Start(info);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>The copy with administrator rights: starts or stops the service and waits for it. Returns the exit code.</summary>
    public static int Run(bool start)
    {
        try
        {
            using var service = new ServiceController(CustosInfo.ServiceName);
            if (start)
            {
                if (service.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
                {
                    service.Start();
                }

                service.WaitForStatus(ServiceControllerStatus.Running, WaitLimit);
            }
            else
            {
                if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
                {
                    service.Stop();
                }

                service.WaitForStatus(ServiceControllerStatus.Stopped, WaitLimit);
            }

            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            // InvalidOperationException wraps the Windows error ("The service did not respond…") in InnerException.
            string reason = ex.InnerException?.Message ?? ex.Message;
            Ui.ShowError(null, Strings.Format(start ? Strings.ServiceStartFailed : Strings.ServiceStopFailed, reason));
            return 1;
        }
    }
}
