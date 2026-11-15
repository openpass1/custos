using System.Globalization;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>Small helpers shared by the windows of the interface.</summary>
internal static class Ui
{
    public static void ShowError(IWin32Window? owner, string text) =>
        MessageBox.Show(owner, text, Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static void ShowWarning(IWin32Window? owner, string text) =>
        MessageBox.Show(owner, text, Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void ShowInfo(IWin32Window? owner, string text) =>
        MessageBox.Show(owner, text, Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>Yes or no, with "no" as the default button.</summary>
    public static bool Confirm(IWin32Window? owner, string text) =>
        MessageBox.Show(owner, text, Strings.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>
    /// Runs a command of a button or a menu: what the service refused is shown to the user, anything else goes to the
    /// handler of unexpected errors (Program).
    /// </summary>
    public static async void Run(IWin32Window? owner, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (PasswordNotTypedException)
        {
            // The user closed the password window: they know nothing was done.
        }
        catch (ServiceUnavailableException ex)
        {
            ShowError(owner, ex.Message);
        }
    }

    /// <summary>
    /// Opens a folder in Explorer; with <paramref name="file"/>, the folder of that file with the file selected. The
    /// folders of the logs can be read by every user.
    /// </summary>
    public static void OpenFolder(IWin32Window? owner, string folder, string? file = null)
    {
        try
        {
            var start = file is null
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder } }
                : new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"");
            using var _ = System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ShowError(owner, ex.Message);
        }
    }

    /// <summary>"04.11.2026 16:41", or "—".</summary>
    public static string Time(DateTimeOffset? time) =>
        time is { } value ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "—";

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A grey "?" that explains a setting when the mouse rests on it; a long text is shown in several lines.</summary>
    public static PictureBox HelpIcon(ToolTip toolTip, string text)
    {
        var icon = new PictureBox
        {
            Image = Glyphs.Draw(Glyph.Help, 16, Glyphs.Grey),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Size = new Size(20, 20),
            Margin = new Padding(2, 3, 0, 0),
            Cursor = Cursors.Help,
            TabStop = false,
        };
        toolTip.SetToolTip(icon, LineWrap.Wrap(text));
        return icon;
    }

    /// <summary>The text of the clipboard; null when it holds no text or another program keeps it open.</summary>
    public static string? ClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// <summary>A label that wraps within its parent's width.</summary>
    public static Label Wrapping(string text, int width) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Margin = new Padding(0, 2, 0, 6),
    };

    /// <summary>A grey note under a setting.</summary>
    public static Label Note(string text, int width)
    {
        var label = Wrapping(text, width);
        label.ForeColor = SystemColors.GrayText;
        return label;
    }

    /// <summary>A bold heading inside a page.</summary>
    public static Label Heading(string text, Font baseFont) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(baseFont, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 4),
    };

    /// <summary>Sets the minimum sizes and the splitter of a <see cref="SplitContainer"/> without the exceptions it
    /// throws when a value does not fit its current size.</summary>
    public static void Split(SplitContainer split, int panel1Min, int panel2Min, int? distance)
    {
        int total = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
        int room = total - split.SplitterWidth;
        if (room <= 0)
        {
            return;
        }

        if (panel1Min + panel2Min > room)
        {
            panel1Min = Math.Max(0, room / 2 - 1);
            panel2Min = Math.Max(0, room - panel1Min - 1);
        }

        try
        {
            split.Panel1MinSize = 0;
            split.Panel2MinSize = 0;
            if (distance is { } wanted)
            {
                split.SplitterDistance = Math.Clamp(wanted, panel1Min, Math.Max(panel1Min, room - panel2Min));
            }

            split.Panel1MinSize = panel1Min;
            split.Panel2MinSize = panel2Min;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // The window is too small right now; the defaults stay.
        }
    }
}
