using Custos.Core.Agent;
using Custos.Core.Ipc;

namespace Custos.Gui;

/// <summary>
/// Asks for the program password (section 4) when the service wants it: before a change or a restore. A wrong
/// password is said in the window, which stays open; "Cancel" gives up the change.
/// </summary>
internal sealed class PasswordPromptForm : DialogForm
{
    private readonly ServiceMonitor _monitor;
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Width = 260, MaxLength = ProgramPassword.MaxLength };
    private readonly Label _error = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.ErrorText, Margin = new Padding(0, 4, 0, 4), Visible = false };

    public PasswordPromptForm(ServiceMonitor monitor)
        : base(Strings.PasswordPromptTitle, new Size(440, 190))
    {
        _monitor = monitor;
        AddWide(Ui.Wrapping(Strings.PasswordPromptText, 400));
        AddRow(Strings.PasswordPromptLabel, _password);
        AddWide(_error);
        EndBuild();
        ActiveControl = _password;
    }

    /// <summary>Shows the window over <paramref name="owner"/> (or over the active window); true once the service
    /// took the password.</summary>
    public static Task<bool> AskAsync(ServiceMonitor monitor, IWin32Window? owner)
    {
        using var form = new PasswordPromptForm(monitor);
        owner ??= Form.ActiveForm;
        if (owner is null)
        {
            form.StartPosition = FormStartPosition.CenterScreen;
            form.TopMost = true;
        }

        return Task.FromResult(form.ShowDialog(owner) == DialogResult.OK);
    }

    protected override void OnOk()
    {
        string password = _password.Text;
        if (password.Length == 0)
        {
            _password.Focus();
            return;
        }

        SetBusy(true);
        _error.Visible = false;
        Ui.Run(this, async () =>
        {
            try
            {
                await _monitor.UnlockAsync(password);
                DialogResult = DialogResult.OK;
            }
            catch (ServiceUnavailableException ex)
            {
                _error.Text = ex.Message;
                _error.Visible = true;
                _password.Clear();
                _password.Focus();
            }
            finally
            {
                if (!IsDisposed)
                {
                    SetBusy(false);
                }
            }
        });
    }
}

/// <summary>Sets or changes the program password: typed twice. When one is set already, the service first asks for it.</summary>
internal sealed class ProgramPasswordForm : DialogForm
{
    private readonly ServiceMonitor _monitor;
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Width = 260, MaxLength = ProgramPassword.MaxLength };
    private readonly TextBox _again = new() { UseSystemPasswordChar = true, Width = 260, MaxLength = ProgramPassword.MaxLength };
    private readonly Label _error = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.ErrorText, Margin = new Padding(0, 4, 0, 4), Visible = false };

    public ProgramPasswordForm(ServiceMonitor monitor)
        : base(Strings.PasswordSetTitle, new Size(460, 230))
    {
        _monitor = monitor;
        AddRow(Strings.PasswordNew, _password);
        AddRow(Strings.PasswordAgain, _again);
        AddWide(_error);
        var note = AddNote(Strings.Format(Strings.PasswordSetNote, ProgramPassword.MinLength));
        note.MaximumSize = new Size(420, 0);
        EndBuild();
        ActiveControl = _password;
    }

    protected override void OnOk()
    {
        string? problem = _password.Text.Length < ProgramPassword.MinLength ? Strings.Format(Strings.PasswordTooShortUi, ProgramPassword.MinLength)
            : _password.Text != _again.Text ? Strings.PasswordMismatch
            : null;
        if (problem is not null)
        {
            _error.Text = problem;
            _error.Visible = true;
            _again.Clear();
            (_password.Text.Length < ProgramPassword.MinLength ? _password : _again).Focus();
            return;
        }

        string password = _password.Text;
        SetBusy(true);
        Ui.Run(this, async () =>
        {
            try
            {
                await _monitor.RequestAsync(PipeProtocol.Methods.PasswordSet, new PasswordRequest { Password = password },
                    IpcJsonContext.Default.PasswordRequest, IpcJsonContext.Default.OkResult);
                DialogResult = DialogResult.OK;
            }
            finally
            {
                if (!IsDisposed)
                {
                    SetBusy(false);
                }
            }
        });
    }
}
