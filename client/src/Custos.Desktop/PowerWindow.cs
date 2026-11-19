using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Custos.Core.Ipc;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// The window every session shows while the service counts down to a shutdown, restart or sleep after a run
/// (section 6.9 of the client spec). Whoever sits at the computer may cancel it; otherwise it closes when the
/// countdown is over or another session cancelled it (PowerCountdownForm of Custos.exe).
/// </summary>
internal sealed class PowerWindow : Window
{
    private const double Seconds = 60;

    private readonly ServiceMonitor _monitor;
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = Seconds, Height = 6 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _cancel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private PowerCountdown _countdown;

    public PowerWindow(ServiceMonitor monitor, PowerCountdown countdown)
    {
        _monitor = monitor;
        _countdown = countdown;
        Title = Strings.AppTitle;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMinimize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Classes.Add("surface");
        _error.Foreground = Ui.Brush("Error");
        _cancel = Ui.Filled(Strings.PowerCancel, () => { });
        _cancel.IsDefault = true;
        _cancel.OnClick(CancelAsync, ex => ShowError(ex.Message));
        Content = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 20),
            Spacing = 16,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { new Icon { Kind = "power_settings_new", VerticalAlignment = VerticalAlignment.Top }, _text },
                },
                _bar,
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel } },
            },
        };
        _text.MaxWidth = 380;
        _timer.Tick += (_, _) => UpdateText();
        _timer.Start();
        UpdateText();
        Opened += (_, _) => TitleBar.Paint(this);
        ActualThemeVariantChanged += (_, _) => TitleBar.Paint(this);
        ScreenFit.Keep(this);
        Closed += (_, _) => _timer.Stop();

        // Closed by the service (the countdown is over or cancelled), not by the person: they cancel with the button.
        Closing += (_, e) => e.Cancel = !Done && e.CloseReason == WindowCloseReason.WindowClosing;
    }

    /// <summary>Set before the window is closed by the program.</summary>
    public bool Done { get; set; }

    internal int SecondsLeft => Math.Max(0, (int)Math.Ceiling((_countdown.Deadline - DateTimeOffset.UtcNow).TotalSeconds));

    internal string Message => _text.Text ?? string.Empty;

    /// <summary>The service started the countdown again, for example after another run.</summary>
    public void ShowCountdown(PowerCountdown countdown)
    {
        // Every snapshot brings the countdown; only a new one (another deadline) clears what the last one showed.
        if (countdown.Deadline != _countdown.Deadline)
        {
            _cancel.IsEnabled = true;
            ShowError(null);
        }

        _countdown = countdown;
        UpdateText();
    }

    private void UpdateText()
    {
        int left = SecondsLeft;
        _text.Text = Strings.Format(Strings.PowerCountdownText, _countdown.TaskName, Words.PowerAction(_countdown.Action), left);
        _bar.Value = Math.Clamp(left, 0, Seconds);
    }

    private void ShowError(string? text)
    {
        _error.Text = text;
        _error.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async Task CancelAsync()
    {
        _cancel.IsEnabled = false;
        try
        {
            var result = await _monitor.CommandAsync(PipeProtocol.Methods.PowerCancel);
            if (!result.Ok)
            {
                ShowError(result.Message ?? Strings.PowerCancelFailed);
            }
        }
        finally
        {
            _cancel.IsEnabled = true;
        }
    }
}
