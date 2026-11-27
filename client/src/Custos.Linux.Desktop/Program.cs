using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Custos.Core;
using Custos.Desktop;
using Custos.Gui;

namespace Custos.Linux.Desktop;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine("Usage: custos-gui [--demo]");
            return 0;
        }

        if (args is not ([] or ["--demo"]))
        {
            Console.Error.WriteLine("Usage: custos-gui [--demo]");
            return 2;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("custos-gui runs on Linux.");
            return 1;
        }

        if (GetEffectiveUserId() == 0)
        {
            Console.Error.WriteLine("Start custos-gui from your desktop session without sudo.");
            return 1;
        }

        CustosEdition.UseLinux();
        UiState.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), "CUSTOS");
        DesktopPlatform.Current = new LinuxPlatform();
        App.Demo = args.Length > 0;
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = Fonts.Roboto })
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    private sealed class LinuxPlatform : DesktopPlatform
    {
        public override bool UsesTray => false;

        public override bool SupportsManagement => false;
    }
}
