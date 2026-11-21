using System.Runtime.InteropServices;
using Custos.Core;
using Custos.Linux.Cli;
using Custos.Service;

namespace Custos.Linux;

/// <summary>
/// One program, two roles. <c>custos agent</c> is the service (systemd starts it as root); anything else is the command
/// of the terminal (<see cref="Commands"/>), which talks to the service. The language is that of the system (LANG), or
/// the setting «Язык» of the program in the service.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        UseInvariantWithoutIcu();
        CustosEdition.UseLinux();
        if (args.Length > 0 && args[0] == "agent")
        {
            return AgentHost.RunAsync(args[1..]).GetAwaiter().GetResult();
        }

        return Commands.RunAsync(args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// .NET takes languages and formats from libicu and stops the program at once when it is missing. install.sh
    /// installs it; where it could not (or someone removed it), the program runs in English with formats of no country
    /// instead of not running. Must come before anything uses a culture.
    /// </summary>
    private static void UseInvariantWithoutIcu()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") is { Length: > 0 })
        {
            return;
        }

        // The names .NET looks for (libicuuc.so.80 down to .50, then without a number) and some newer ones. The digits
        // by hand: even formatting a number may start the globalization of .NET.
        for (int version = 90; version >= 50; version--)
        {
            if (Found("libicuuc.so." + (char)('0' + (version / 10)) + (char)('0' + (version % 10))))
            {
                return;
            }
        }

        if (!Found("libicuuc.so"))
        {
            AppContext.SetSwitch("System.Globalization.Invariant", true);
        }

        static bool Found(string name)
        {
            if (!NativeLibrary.TryLoad(name, out IntPtr handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }
    }
}
