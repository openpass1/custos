using System.Diagnostics;
using System.Drawing.Drawing2D;
using Custos.Core;

namespace Custos.Gui;

/// <summary>
/// The main window. For now it shows the state of the service; the task list and card (docs/client-ui.md) come next.
/// Closing it only hides it: the interface stays in the tray.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ServiceMonitor _monitor;
    private readonly Label _serviceValue;
    private readonly Label _serviceVersionValue;
    private readonly Label _userValue;
    private readonly Label _problemLabel;
    private readonly Label _problemValue;
    private readonly ToolStripStatusLabel _serviceStatus;
    private readonly ToolStripStatusLabel _userStatus;
    private readonly Bitmap _greenDot = CreateDot(Color.FromArgb(46, 160, 67), filled: true);
    private readonly Bitmap _greyDot = CreateDot(Color.FromArgb(128, 134, 140), filled: true);
    private readonly Bitmap _emptyDot = CreateDot(Color.FromArgb(128, 134, 140), filled: false);

    public MainForm(ServiceMonitor monitor)
    {
        _monitor = monitor;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.AppTitle;
        Icon = AppIcons.App();
        Size = new Size(1000, 700);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(16),
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _serviceValue = AddRow(details, Strings.LabelService).Value;
        _serviceVersionValue = AddRow(details, Strings.LabelServiceVersion).Value;
        AddRow(details, Strings.LabelInterfaceVersion).Value.Text = CustosInfo.Version;
        _userValue = AddRow(details, Strings.LabelUser).Value;
        using (var self = Process.GetCurrentProcess())
        {
            AddRow(details, Strings.LabelSession).Value.Text = self.SessionId.ToString(Thread.CurrentThread.CurrentCulture);
        }
        (_problemLabel, _problemValue) = AddRow(details, Strings.LabelProblem);

        _serviceStatus = new ToolStripStatusLabel();
        var serverStatus = new ToolStripStatusLabel(Strings.ServerNotConnected, _emptyDot)
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ImageAlign = ContentAlignment.MiddleLeft,
        };
        _userStatus = new ToolStripStatusLabel();
        var versionStatus = new ToolStripStatusLabel(CustosInfo.Version);
        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange([_serviceStatus, serverStatus, _userStatus, versionStatus]);

        Controls.Add(details);
        Controls.Add(statusStrip);
        ResumeLayout(performLayout: true);

        _monitor.StatusChanged += OnStatusChanged;
        ShowStatus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.StatusChanged -= OnStatusChanged;
            _greenDot.Dispose();
            _greyDot.Dispose();
            _emptyDot.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnStatusChanged(object? sender, EventArgs e) => ShowStatus();

    private void ShowStatus()
    {
        var status = _monitor.Status;
        var hello = status.State == ServiceState.Running ? status.Hello : null;

        string state = ServiceMonitor.Describe(status.State);
        _serviceStatus.Text = Capitalize(state);
        _serviceStatus.Image = status.State == ServiceState.Running ? _greenDot : _greyDot;
        _serviceValue.Text = hello is null ? state : Strings.Format(Strings.RunningSince, hello.StartedAt.LocalDateTime);
        _serviceVersionValue.Text = hello?.ServiceVersion ?? "—";

        string? user = hello?.User is { } name
            ? Strings.Format(Strings.UserWithRights, name, hello.IsAdmin ? Strings.RightsAdmin : Strings.RightsUser)
            : null;
        _userValue.Text = user ?? "—";
        _userStatus.Text = user ?? string.Empty;

        _problemValue.Text = status.Problem ?? string.Empty;
        _problemLabel.Visible = _problemValue.Visible = status.Problem is not null;
    }

    private static (Label Caption, Label Value) AddRow(TableLayoutPanel table, string caption)
    {
        var captionLabel = new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 4, 12, 4) };
        var valueLabel = new Label { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        table.Controls.Add(captionLabel);
        table.Controls.Add(valueLabel);
        return (captionLabel, valueLabel);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Thread.CurrentThread.CurrentCulture) + text[1..];

    private static Bitmap CreateDot(Color color, bool filled)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var circle = new Rectangle(3, 3, 9, 9);
        if (filled)
        {
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, circle);
        }
        else
        {
            using var pen = new Pen(color, 1.5f);
            graphics.DrawEllipse(pen, circle);
        }

        return bitmap;
    }
}
