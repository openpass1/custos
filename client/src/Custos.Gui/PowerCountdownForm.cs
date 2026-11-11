using Custos.Core.Ipc;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// The window every session shows while the service counts down to a shutdown, restart or sleep after a run
/// (section 6.9 of the client spec). Whoever sits at the computer may cancel it; otherwise it closes when the
/// countdown is over or another session cancelled it.
/// </summary>
internal sealed class PowerCountdownForm : Form
{
    private readonly ServiceMonitor _monitor;
    private readonly Label _text;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private PowerCountdown _countdown;

    public PowerCountdownForm(ServiceMonitor monitor, PowerCountdown countdown)
    {
        _monitor = monitor;
        _countdown = countdown;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.AppTitle;
        Icon = AppIcons.App();
        ClientSize = new Size(440, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        TopMost = true;
        ShowInTaskbar = true;

        _text = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 54, Padding = new Padding(16, 14, 16, 0) };
        _bar = new ProgressBar { Dock = DockStyle.Top, Height = 14, Maximum = 60, Margin = new Padding(16) };
        var barPanel = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(16, 4, 16, 8) };
        barPanel.Controls.Add(_bar);
        _cancel = new Button { Text = Strings.PowerCancel, AutoSize = true, MinimumSize = new Size(120, 0) };
        _cancel.Click += (_, _) => CancelAction();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 6, 12, 12),
        };
        buttons.Controls.Add(_cancel);
        Controls.Add(barPanel);
        Controls.Add(_text);
        Controls.Add(buttons);
        AcceptButton = _cancel;
        ResumeLayout(performLayout: true);

        _timer.Tick += (_, _) => UpdateText();
        _timer.Start();
        UpdateText();
    }

    /// <summary>The service started the countdown again, for example after another run.</summary>
    public void ShowCountdown(PowerCountdown countdown)
    {
        _countdown = countdown;
        _cancel.Enabled = true;
        UpdateText();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    internal int SecondsLeft => Math.Max(0, (int)Math.Ceiling((_countdown.Deadline - DateTimeOffset.UtcNow).TotalSeconds));

    internal string Message => _text.Text;

    private void UpdateText()
    {
        int left = SecondsLeft;
        _text.Text = Strings.Format(Strings.PowerCountdownText, _countdown.TaskName, Words.PowerAction(_countdown.Action), left);
        _bar.Value = Math.Clamp(left, 0, _bar.Maximum);
    }

    private void CancelAction()
    {
        _cancel.Enabled = false;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.CommandAsync(PipeProtocol.Methods.PowerCancel);
                if (!result.Ok && !IsDisposed)
                {
                    Ui.ShowInfo(this, result.Message ?? Strings.PowerCancelFailed);
                }
            }
            finally
            {
                if (!IsDisposed)
                {
                    _cancel.Enabled = true;
                }
            }
        });
    }
}
