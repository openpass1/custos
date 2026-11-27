using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Custos.Core;
using Custos.Desktop;
using Custos.Gui;

namespace Custos.Linux.Desktop;

internal static partial class Program
{
    private const string Usage = "Usage: custos-gui [--demo | --version | --help]";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args is ["--version"])
        {
            Console.WriteLine(CustosInfo.Version);
            return 0;
        }

        if (args is not ([] or ["--demo"]))
        {
            Console.Error.WriteLine(Usage);
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
        InterfaceLanguage.Apply(UiState.Load().InterfaceLanguage);
        DesktopPlatform.Current = new LinuxPlatform(StatusNotifier.HostRegisteredAsync().GetAwaiter().GetResult());
        App.Demo = args.Length > 0;
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = Fonts.Roboto })
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    private sealed class LinuxPlatform(bool hasTray) : DesktopPlatform
    {
        private readonly Dictionary<TrayState, WindowIcon> _icons = [];
        private readonly Task<IReadOnlyList<string>> _unitNames = Task.Run(ReadUnitNames);

        public override bool UsesTray => hasTray;

        public override bool KeepsWindowInTray => false;

        public override bool ControlsSystemService => true;

        public override Task ControlSystemServiceAsync(SystemServiceAction action) => SystemdService.ControlAsync(action);

        public override string ThemeAuto => DesktopStrings.LinuxThemeAuto;

        public override IReadOnlyList<string> ServiceNames() => _unitNames.IsCompletedSuccessfully ? _unitNames.Result : [];

        private static IReadOnlyList<string> ReadUnitNames()
        {
            string[] paths = ["/etc/systemd/system", "/run/systemd/system", "/run/systemd/transient",
                "/usr/local/lib/systemd/system", "/usr/lib/systemd/system", "/lib/systemd/system"];
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                try
                {
                    foreach (string unitFile in Directory.EnumerateFiles(path, "*.service"))
                    {
                        names.Add(Path.GetFileName(unitFile));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The event also accepts a unit name typed by hand.
                }
            }

            return [.. names.Order(StringComparer.Ordinal)];
        }

        public override WindowIcon? TrayIcon(TrayState state)
        {
            if (!hasTray)
            {
                return null;
            }

            if (!_icons.TryGetValue(state, out var icon))
            {
                using var image = AssetLoader.Open(new Uri($"avares://custos-gui/Assets/tray-{state.ToString().ToLowerInvariant()}.ico"));
                _icons[state] = icon = new WindowIcon(image);
            }

            return icon;
        }

        public override bool SupportsManagement => false;

        public override bool SupportsSettings => true;

        public override bool SupportsTaskManagement => true;

        public override bool SupportsRestore => true;

        public override bool SupportsCloudAccounts => true;
    }
}
