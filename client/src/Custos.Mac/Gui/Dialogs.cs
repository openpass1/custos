using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Mac.Gui;

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
            Margin = new Thickness(24, 20, 24, 18),
            Children =
            {
                Dock(new TextBlock { Text = title, Classes = { "headline" }, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap }, Avalonia.Controls.Dock.Top),
                Dock(new Border { Child = _buttons, Margin = new Thickness(0, 20, 0, 0) }, Avalonia.Controls.Dock.Bottom),
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
    /// An account for a network folder or a server: user (and domain for SMB) and password. Saved in the service;
    /// returns the saved account, or null when cancelled.
    /// </summary>
    public static async Task<Credential?> EditAccount(Window owner, ServiceMonitor monitor, string kind, Credential? existing)
    {
        var dialog = new DialogWindow(existing is null ? Strings.AccountAddTitle : Strings.Format(Strings.AccountEditTitle, existing.DisplayName));
        bool smb = kind == CredentialKinds.Windows;
        var user = Ui.TextBox(existing?.User);
        var domain = Ui.TextBox(existing?.Domain);
        var password = Ui.TextBox();
        password.PasswordChar = '•';
        var error = Ui.Error();
        dialog.Body.Children.Add(Ui.Row(kind == CredentialKinds.S3 ? MacStrings.AccountAccessKey : Strings.AccountUser, user, labelWidth: 170));
        if (smb)
        {
            dialog.Body.Children.Add(Ui.Row(Strings.AccountDomain, domain, MacStrings.AccountDomainNote, labelWidth: 170));
        }

        dialog.Body.Children.Add(Ui.Row(kind == CredentialKinds.S3 ? MacStrings.AccountSecretKey : Strings.AccountPassword, password,
            existing?.HasSecret == true ? Strings.AccountKeepPasswordNote : MacStrings.AccountPasswordNote, labelWidth: 170));
        dialog.Body.Children.Add(error);
        dialog.AddButton(Strings.ButtonCancel, () => dialog.Close(null));
        var ok = dialog.AddButton(Strings.ButtonOk, () => { }, main: true);
        ok.OnClick(async () =>
        {
            string name = (user.Text ?? string.Empty).Trim();
            string secret = password.Text ?? string.Empty;
            if (name.Length == 0)
            {
                Ui.Show(error, Strings.ErrorUserRequired);
                return;
            }

            if (secret.Length == 0 && existing?.HasSecret != true)
            {
                Ui.Show(error, Strings.ErrorPasswordRequired);
                return;
            }

            var credential = new Credential
            {
                Id = existing?.Id ?? TaskDefinition.NewId(),
                Kind = kind,
                User = name,
                Domain = smb && !string.IsNullOrWhiteSpace(domain.Text) ? domain.Text.Trim() : null,
            };
            var input = new CredentialInput { Credential = credential, Password = secret.Length > 0 ? secret : null };
            try
            {
                var saved = await monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, input,
                    IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
                dialog.Close(saved.Credential);
            }
            catch (ServiceUnavailableException ex)
            {
                Ui.Show(error, ex.Message);
            }
        });
        return await dialog.ShowDialog<Credential?>(owner);
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
