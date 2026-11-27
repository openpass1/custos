using System.ComponentModel;
using System.Diagnostics;
using Custos.Core;
using Custos.Core.Ipc;
using Custos.Desktop;
using Custos.Gui;

namespace Custos.Linux.Desktop;

internal static class SystemdService
{
    private const string Unit = CustosInfo.LinuxServiceUnit + ".service";

    public static async Task ControlAsync(SystemServiceAction action)
    {
        if (PipeProtocol.UnixSocketPath is not ("/run/custos.sock" or "/var/run/custos.sock"))
        {
            throw new InvalidOperationException(DesktopStrings.SystemServiceCustomSocket);
        }

        string systemctl = File.Exists("/usr/bin/systemctl") ? "/usr/bin/systemctl" : "/bin/systemctl";
        if (!File.Exists(systemctl))
        {
            throw new InvalidOperationException(DesktopStrings.SystemServiceUnavailable);
        }

        using var process = new Process { StartInfo = CreateCommand(systemctl, action) };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(Strings.Format(DesktopStrings.SystemServiceFailed, ex.Message), ex);
        }

        // The user's graphical PolicyKit agent authorizes systemctl. No password is read by the window or a terminal.
        process.StandardInput.Close();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // systemctl may have just exited.
            }

            throw new InvalidOperationException(DesktopStrings.SystemServiceTimeout);
        }

        string detail = (await errors.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            detail = detail.Length > 500 ? detail[..500] + "…" : detail;
            if (detail.Length == 0)
            {
                detail = Strings.Format(DesktopStrings.SystemServiceExitCode, process.ExitCode);
            }

            throw new InvalidOperationException(Strings.Format(DesktopStrings.SystemServiceFailed, detail));
        }
    }

    private static ProcessStartInfo CreateCommand(string systemctl, SystemServiceAction action)
    {
        string verb = action switch
        {
            SystemServiceAction.Start => "start",
            SystemServiceAction.Stop => "stop",
            SystemServiceAction.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var start = new ProcessStartInfo(systemctl)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in (string[])["--system", "--no-pager", verb, "--", Unit])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("DBUS_SYSTEM_BUS_ADDRESS");
        start.Environment.Remove("SYSTEMD_BUS_ADDRESS");
        start.Environment["SYSTEMD_COLORS"] = "0";
        return start;
    }
}
