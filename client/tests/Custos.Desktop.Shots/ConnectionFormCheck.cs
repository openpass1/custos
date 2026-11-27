using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Custos.Core;
using Custos.Core.Cloud;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Gui;

namespace Custos.Desktop.Shots;

internal static class ConnectionFormCheck
{
    public static bool Run()
    {
        if (OperatingSystem.IsLinux())
        {
            CustosEdition.UseLinux();
        }

        AvaloniaSynchronizationContext.InstallIfNeeded();
        using var monitor = new ServiceMonitor();
        var monitorForm = new MonitorForm(monitor);
        var line = monitorForm.Items.OfType<TextItem>().Single();
        bool offline = line.ReadOnly && line.HasHelp && line.HasAction && !line.IsActionEnabled && !line.Secret
            && RegistrationCode.TrySplitConnection(line.Placeholder, out _, out var exampleCode)
            && RegistrationCode.TryParse(exampleCode, out _) is not null
            && monitorForm.Items.OfType<NoteItem>().Any(note => note.IsVisible && note.Text == Strings.ProblemNoConnection);
        monitorForm.Close();

        bool appSelection = CloudAppSelection();
        bool layout = Layout();
        Console.WriteLine($"Connection form: offline={offline}, cloud application={appSelection}, layout={layout}.");
        return offline && appSelection && layout;
    }

    private static bool CloudAppSelection()
    {
        var apps = new CloudAppsResult();
        bool missing = CloudNames.Kinds.All(kind => !CloudConnectDialog.HasUsableApp(CloudService.Of(kind), apps));
        apps.BuiltIn.Add(DestinationKinds.GDrive);
        bool builtIn = CloudConnectDialog.HasUsableApp(CloudService.GDrive, apps);
        var own = new CloudApp { ClientId = "fixture-local-app" };
        apps.Own.Gdrive = own;
        bool invalidOverride = !CloudConnectDialog.HasUsableApp(CloudService.GDrive, apps);
        apps.BuiltIn.Clear();
        own.ClientSecret = "fixture-local-secret";
        apps.Own.Yadisk = new CloudApp { ClientId = "fixture-yandex-app" };
        apps.Own.Dropbox = new CloudApp { ClientId = "fixture-dropbox-app" };
        return missing && builtIn && invalidOverride && CloudConnectDialog.HasUsableApp(CloudService.GDrive, apps)
            && !CloudConnectDialog.HasUsableApp(CloudService.YaDisk, apps) && CloudConnectDialog.HasUsableApp(CloudService.Dropbox, apps);
    }

    private static bool Layout()
    {
        var line = new TextItem("Monitor", "https://custos.example.lan/" + new string('a', 180))
        {
            ActionIcon = "content_paste", Action = () => Task.CompletedTask,
        };
        var code = new TextItem("OAuth", "fixture-code")
        {
            Secret = true, ActionIcon = "content_paste", Action = () => Task.CompletedTask,
        };
        const string instruction = "An instruction that has to wrap before it reaches the edge of a narrow connection dialog. "
            + "After signing in to the provider, return to this window and enter the code given on the provider page.";
        var window = new LayoutForm(new LabelItem(instruction), line, code, new RadioItem(new RadioGroup(), "Restore into a folder", on: true))
        {
            Width = 480,
        };
        window.Show();
        try
        {
            Pump();
            var boxes = window.GetVisualDescendants().OfType<TextBox>().ToArray();
            var monitorBox = boxes.Single(box => ReferenceEquals(box.DataContext, line));
            var cloudBox = boxes.Single(box => ReferenceEquals(box.DataContext, code));
            var paste = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.GetType() == typeof(Button) && ReferenceEquals(button.DataContext, code));
            var show = window.GetVisualDescendants().OfType<ToggleButton>().Single(button => ReferenceEquals(button.DataContext, code));
            var pasteOrigin = paste.TranslatePoint(default, window);
            var showOrigin = show.TranslatePoint(default, window);
            bool distinctButtons = pasteOrigin is { } pastePoint && showOrigin is { } showPoint
                && pastePoint.X + paste.Bounds.Width <= showPoint.X && showPoint.X + show.Bounds.Width <= window.Bounds.Width;

            monitorBox.Focus();
            monitorBox.CaretIndex = line.Text.Length;
            Pump();
            var scroll = monitorBox.GetVisualDescendants().OfType<ScrollViewer>().Single();
            bool scrollsToCaret = scroll.Offset.X > 0;
            monitorBox.CaretIndex = 0;
            Pump();

            var text = window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == instruction);
            bool wraps = text.Bounds.Width <= window.Bounds.Width && text.Bounds.Height > text.FontSize * 2
                && window.Extent.Width <= window.Viewport.Width;
            bool returnedToStart = scroll.Offset.X == 0;

            return distinctButtons && cloudBox.PasswordChar != '\0' && monitorBox.PasswordChar == '\0'
                && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden && scrollsToCaret && returnedToStart
                && wraps && RadioCentered(window);
        }
        finally
        {
            window.Close();
        }
    }

    private static bool RadioCentered(Window window)
    {
        var radio = window.GetVisualDescendants().OfType<RadioButton>().Single();
        var ellipses = radio.GetVisualDescendants().OfType<Ellipse>().ToArray();
        var ring = ellipses.Single(ellipse => ellipse.Name == "ring");
        var dot = ellipses.Single(ellipse => ellipse.Name == "dot");
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            window.SetRenderScaling(scale);
            Pump();
            var ringCenter = ring.TranslatePoint(new Point(ring.Bounds.Width / 2, ring.Bounds.Height / 2), radio);
            var dotCenter = dot.TranslatePoint(new Point(dot.Bounds.Width / 2, dot.Bounds.Height / 2), radio);
            if (window.RenderScaling != scale || ringCenter is not { } r || dotCenter is not { } d
                || Math.Abs(r.X - d.X) > 0.01 || Math.Abs(r.Y - d.Y) > 0.01 || Math.Abs(r.Y - radio.Bounds.Height / 2) > 0.01)
            {
                Console.WriteLine($"Radio center at {scale:P0}: ring={ringCenter}, dot={dotCenter}.");
                return false;
            }
        }

        return true;
    }

    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private sealed class MonitorForm : FormDialog
    {
        public MonitorForm(ServiceMonitor monitor) : base(Strings.SettingsServer) =>
            _ = new MonitorSettingsSection(this, monitor, ShowError);

        protected override Task<bool> OkAsync() => Task.FromResult(false);
    }

    private sealed class LayoutForm : FormDialog
    {
        public LayoutForm(params FormItem[] items) : base("Connect an account", 660)
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }

        public Size Extent => Scroll.Extent;

        public Size Viewport => Scroll.Viewport;

        protected override Task<bool> OkAsync() => Task.FromResult(false);
    }
}
