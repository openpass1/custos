using System.Runtime.InteropServices;
using Custos.Core;

namespace Custos.Service.Platform;

/// <summary>Where CUSTOS for Linux is installed (linux/install.sh and the update script use the same).</summary>
internal static class LinuxPaths
{
    /// <summary>The program: the service and the commands of the terminal.</summary>
    public const string ProgramFolder = "/opt/custos";

    /// <summary>The command people type: a link to the program.</summary>
    public const string Command = "/usr/local/bin/custos";

    /// <summary>The unit of the service.</summary>
    public static string UnitFile => $"/etc/systemd/system/{CustosInfo.LinuxServiceUnit}.service";

    /// <summary>"x64" or "arm64": the processors CUSTOS for Linux is built for.</summary>
    public static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>The program for this processor inside an update package: custos-linux-x64.tar.gz.</summary>
    public static string ProgramArchive => $"custos-linux-{Architecture}.tar.gz";
}
