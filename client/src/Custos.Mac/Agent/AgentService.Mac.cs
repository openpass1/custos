using System.Diagnostics;
using Custos.Core;
using Custos.Service.Platform;

namespace Custos.Service.Agent;

/// <summary>
/// Updates on macOS (A.10): the package of the site or of GitHub (<c>custos-macos-X.Y.Z.zip</c>) holds one file,
/// <c>Custos.pkg</c>, the same installer as the one people download. A small script installs it with the installer
/// of macOS: the scripts of the package stop this service, replace Custos.app and start the new service, which then
/// reads the result. The script is not stopped with the service: the launchd job has AbandonProcessGroup.
/// </summary>
internal sealed partial class AgentService
{
    /// <summary>The installer of CUSTOS inside the update package.</summary>
    internal const string MacPackageFile = "Custos.pkg";

    private const string UpdateScriptFile = "update.sh";

    /// <summary>Without it a package is no CUSTOS for macOS.</summary>
    private static readonly string[] RequiredFiles = [MacPackageFile];

    /// <summary>
    /// Installs the package, writes the result in the fields of UpdaterResult, and starts the service again if the
    /// package did not (when it failed after stopping it). Arguments: package, result file, from, to, launchd label,
    /// launchd plist, log file.
    /// </summary>
    internal const string UpdateScript = """
        #!/bin/sh
        # Written by the CUSTOS service: installs an update of CUSTOS for macOS and leaves the result for the service.
        pkg="$1"; result="$2"; from="$3"; to="$4"; label="$5"; plist="$6"; log="$7"
        sleep 2
        echo "$(date '+%Y-%m-%d %H:%M:%S') installing CUSTOS $to over $from" >>"$log"
        /usr/sbin/installer -pkg "$pkg" -target / >>"$log" 2>&1
        code=$?
        echo "$(date '+%Y-%m-%d %H:%M:%S') installer exit code $code" >>"$log"
        if [ "$code" = "0" ]; then
          printf '{"ok":true,"from_version":"%s","to_version":"%s","message":null,"rolled_back":false}\n' "$from" "$to" >"$result"
        else
          why=$(grep -i 'installer:' "$log" | tail -n 2 | tr '\n\t\r' '   ' | tr -d '"\\' | cut -c1-400)
          printf '{"ok":false,"from_version":"%s","to_version":"%s","message":"installer %s: %s","rolled_back":false}\n' "$from" "$to" "$code" "$why" >"$result"
        fi
        # The package starts the service; if it stopped it and failed, the old one starts again here.
        if ! /bin/launchctl print "system/$label" >/dev/null 2>&1; then
          /bin/launchctl bootstrap system "$plist" >>"$log" 2>&1
        fi
        /bin/launchctl kickstart "system/$label" >>"$log" 2>&1
        exit 0
        """;

    private static void PrepareUpdater(string staging, string updaterFolder)
    {
        string script = Path.Combine(updaterFolder, UpdateScriptFile);
        File.WriteAllText(script, UpdateScript.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!File.Exists(Path.Combine(staging, MacPackageFile)))
        {
            throw new IOException(Messages.Format(Messages.MonitorPackageMissingFile, MacPackageFile));
        }
    }

    private ProcessStartInfo UpdaterStart(string staging, string updaterFolder, string version)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updaterFolder,
        };
        foreach (string argument in new[]
        {
            Path.Combine(updaterFolder, UpdateScriptFile), Path.Combine(staging, MacPackageFile), UpdateResultFile,
            info.Version, version, CustosInfo.MacServiceLabel, MacPaths.LaunchdPlist,
            Path.Combine(CustosInfo.LogsDirectory, "update.log"),
        })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
