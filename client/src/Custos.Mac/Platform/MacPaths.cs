using Custos.Core;

namespace Custos.Service.Platform;

/// <summary>Where CUSTOS for macOS is installed (mac/build-mac.sh and the scripts of the package use the same).</summary>
internal static class MacPaths
{
    /// <summary>The program: the window and, started by launchd with "agent", the service.</summary>
    public const string AppBundle = "/Applications/Custos.app";

    /// <summary>The launchd job of the service.</summary>
    public static string LaunchdPlist => $"/Library/LaunchDaemons/{CustosInfo.MacServiceLabel}.plist";
}
