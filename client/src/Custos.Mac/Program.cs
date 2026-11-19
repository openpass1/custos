using Custos.Mac.Gui;
using Custos.Service;

namespace Custos.Mac;

/// <summary>
/// One program, two roles. <c>Custos agent</c> is the service (launchd starts it as root); without arguments it is
/// the window and the icon in the menu bar of the user, as Custos.exe on Windows.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "agent")
        {
            return AgentHost.RunAsync(args[1..]).GetAwaiter().GetResult();
        }

        return GuiProgram.Run(args);
    }
}
