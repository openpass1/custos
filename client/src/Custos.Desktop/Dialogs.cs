using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Custos.Core.Ipc;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>A modal dialog in the M3 look: a title, the content, and the buttons at the bottom right.</summary>
internal class DialogWindow : Window
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

    public DialogWindow(string title, double width = 520)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Body = new StackPanel { Spacing = 8 };
        Content = new DockPanel
        {
            Margin = new Thickness(22, 16, 22, 14),
            Children =
            {
                Dock(new TextBlock { Text = title, Classes = { "headline" }, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap }, Avalonia.Controls.Dock.Top),
                Dock(new Border { Child = _buttons, Margin = new Thickness(0, 14, 0, 0) }, Avalonia.Controls.Dock.Bottom),
                Body,
            },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
        Opened += (_, _) => TitleBar.Paint(this);
        ScreenFit.Keep(this);
    }

    /// <summary>Where the content goes.</summary>
    public StackPanel Body { get; }

    public Button AddButton(string text, Action click, bool main = false)
    {
        var button = main ? Ui.Filled(text, click) : Ui.Text(text, click);
        if (main)
        {
            button.IsDefault = true;
        }

        _buttons.Children.Add(button);
        return button;
    }

    private static Control Dock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}

/// <summary>Questions and messages.</summary>
internal static class Dialogs
{
    public static async Task Show(Window owner, string text, string? title = null)
    {
        var dialog = new DialogWindow(title ?? Strings.AppTitle);
        dialog.Body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        dialog.AddButton(Strings.ButtonOk, () => dialog.Close(true), main: true);
        await dialog.ShowDialog<object?>(owner);
    }

    public static async Task<bool> Ask(Window owner, string text, string yes, string? no = null, string? title = null)
    {
        var dialog = new DialogWindow(title ?? Strings.AppTitle);
        dialog.Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        dialog.AddButton(no ?? Strings.ButtonCancel, () => dialog.Close(false));
        dialog.AddButton(yes, () => dialog.Close(true), main: true);
        return await dialog.ShowDialog<bool?>(owner) == true;
    }

    /// <summary>One line of text; null when cancelled.</summary>
    public static async Task<string?> AskText(Window owner, string title, string label, string? text = null, bool secret = false, string? note = null)
    {
        var dialog = new DialogWindow(title);
        var box = Ui.TextBox(text);
        if (secret)
        {
            box.PasswordChar = '•';
        }

        dialog.Body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        dialog.Body.Children.Add(box);
        if (note is not null)
        {
            dialog.Body.Children.Add(Ui.Note(note));
        }

        dialog.AddButton(Strings.ButtonCancel, () => dialog.Close(null));
        dialog.AddButton(Strings.ButtonOk, () => dialog.Close(box.Text ?? string.Empty), main: true);
        dialog.Opened += (_, _) => box.Focus();
        return await dialog.ShowDialog<string?>(owner);
    }

    /// <summary>A new password typed twice; null when cancelled.</summary>
    public static async Task<string?> NewPassword(Window owner, string title, string? note, int minLength = 1)
    {
        var dialog = new DialogWindow(title);
        var first = Ui.TextBox();
        var second = Ui.TextBox();
        first.PasswordChar = second.PasswordChar = '•';
        var error = Ui.Error();
        dialog.Body.Children.Add(Ui.Row(Strings.PasswordNew, first, labelWidth: 150));
        dialog.Body.Children.Add(Ui.Row(Strings.PasswordAgain, second, labelWidth: 150));
        if (note is not null)
        {
            dialog.Body.Children.Add(Ui.Note(note));
        }

        dialog.Body.Children.Add(error);
        dialog.AddButton(Strings.ButtonCancel, () => dialog.Close(null));
        dialog.AddButton(Strings.ButtonOk, () =>
        {
            string a = first.Text ?? string.Empty;
            if (a.Length < minLength)
            {
                Ui.Show(error, minLength > 1 ? Strings.Format(Strings.PasswordTooShortUi, minLength) : Strings.ErrorPasswordRequired);
            }
            else if (a != (second.Text ?? string.Empty))
            {
                Ui.Show(error, Strings.PasswordMismatch);
            }
            else
            {
                dialog.Close(a);
            }
        }, main: true);
        dialog.Opened += (_, _) => first.Focus();
        return await dialog.ShowDialog<string?>(owner);
    }

    /// <summary>
    /// The program password (section 4), asked when a change needs it: sent to the service, and asked again while it
    /// is wrong. False when the user gave up.
    /// </summary>
    public static async Task<bool> ProgramPassword(Window owner, ServiceMonitor monitor)
    {
        var dialog = new DialogWindow(Strings.PasswordPromptTitle, 460);
        var password = Ui.TextBox();
        password.PasswordChar = '•';
        var error = Ui.Error();
        dialog.Body.Children.Add(new TextBlock { Text = Strings.PasswordPromptText, TextWrapping = TextWrapping.Wrap });
        dialog.Body.Children.Add(Ui.Row(Strings.PasswordPromptLabel, password, labelWidth: 100));
        dialog.Body.Children.Add(error);
        dialog.AddButton(Strings.ButtonCancel, () => dialog.Close(false));
        var ok = dialog.AddButton(Strings.ButtonOk, () => { }, main: true);
        ok.OnClick(async () =>
        {
            string typed = password.Text ?? string.Empty;
            if (typed.Length == 0)
            {
                password.Focus();
                return;
            }

            ok.IsEnabled = false;
            try
            {
                await monitor.UnlockAsync(typed);
                dialog.Close(true);
            }
            catch (ServiceUnavailableException ex)
            {
                Ui.Show(error, ex.Message);
                password.Text = string.Empty;
                password.Focus();
            }
            finally
            {
                ok.IsEnabled = true;
            }
        });
        dialog.Opened += (_, _) => password.Focus();
        return await dialog.ShowDialog<bool?>(owner) == true;
    }
}
