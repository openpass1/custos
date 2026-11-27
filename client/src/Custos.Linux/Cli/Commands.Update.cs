using Custos.Core;
using Custos.Core.Ipc;
using Custos.Core.Updates;
using Custos.Service.Platform;

namespace Custos.Linux.Cli;

internal static partial class Commands
{
    private static Task<int> UpdateAsync(string[] args) => args switch
    {
        [] or ["help"] or ["--help"] => Task.FromResult(Done(LinuxStrings.CliUpdateUsage)),
        ["channel"] => ShowUpdateChannelAsync(),
        ["channel", ProgramReleases.StableChannel or ProgramReleases.PreviewChannel] => ChangeUpdateChannelAsync(args[1]),
        ["check"] => CheckUpdateAsync(),
        ["install"] => InstallUpdateAsync(),
        _ => Task.FromResult(Fail(LinuxStrings.CliUpdateUsage)),
    };

    private static async Task<int> ShowUpdateChannelAsync()
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        var update = (await session.SnapshotAsync().ConfigureAwait(false)).ProgramUpdate;
        return Done(ManagedUpdateReason(update) ?? UpdateChannelText(update.Channel));
    }

    private static async Task<int> ChangeUpdateChannelAsync(string channel)
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        var update = (await session.SnapshotAsync().ConfigureAwait(false)).ProgramUpdate;
        if (UpdateRefusal(session, update) is { } refusal)
        {
            return Fail(refusal);
        }

        if (ProgramReleases.NormalizeChannel(update.Channel) == channel)
        {
            return Done(UpdateChannelText(channel));
        }

        if (update.Channel is null)
        {
            return Fail(LinuxStrings.CliUpdateChannelUnsupported);
        }

        return await WithUpdatePasswordAsync(session, async () =>
        {
            var result = await session.RequestAsync(PipeProtocol.Methods.ProgramChannelSet, new ProgramChannelRequest { Channel = channel },
                IpcJsonContext.Default.ProgramChannelRequest, IpcJsonContext.Default.OkResult).ConfigureAwait(false);
            return result.Ok ? Done(UpdateChannelText(channel)) : Fail(result.Message ?? LinuxStrings.CliUpdateChangeFailed);
        }).ConfigureAwait(false);
    }

    private static async Task<int> CheckUpdateAsync()
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        var update = (await session.SnapshotAsync().ConfigureAwait(false)).ProgramUpdate;
        if (UpdateRefusal(session, update) is { } refusal)
        {
            return Fail(refusal);
        }

        var result = await session.RequestAsync(PipeProtocol.Methods.ProgramCheck, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.ProgramUpdateInfo, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if ((ManagedUpdateReason(result) ?? result.Error) is { } problem)
        {
            return Fail(problem);
        }

        if (result.CheckedAt is null)
        {
            return Fail(LinuxStrings.CliUpdateCheckAgain);
        }

        Console.WriteLine(UpdateChannelText(result.Channel));
        string installed = session.Hello.ServiceVersion;
        // Available in the snapshot controls the periodic notification. An explicit check reports newer releases
        // even if automatic checks are disabled.
        if (result.Latest is { } latest && ProgramReleases.IsNewer(latest, installed))
        {
            return Done(LinuxStrings.Format(result.Prerelease ? LinuxStrings.CliUpdateNewPreview : LinuxStrings.CliUpdateNewRelease, latest, installed));
        }

        return Done(result.Latest is null ? LinuxStrings.CliUpdateNoRelease : LinuxStrings.Format(LinuxStrings.CliUpdateNoNewRelease, installed));
    }

    private static async Task<int> InstallUpdateAsync()
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        var update = (await session.SnapshotAsync().ConfigureAwait(false)).ProgramUpdate;
        if (UpdateRefusal(session, update) is { } refusal)
        {
            return Fail(refusal);
        }

        return await WithUpdatePasswordAsync(session, async () =>
        {
            // The agent makes a fresh channel-matched check and verifies the package before handing it to the updater.
            var result = await session.RequestAsync(PipeProtocol.Methods.ProgramUpdate, EmptyRequest.Instance,
                IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult, timeout: TimeSpan.FromMinutes(12)).ConfigureAwait(false);
            return result.Ok ? Done(result.Message ?? LinuxStrings.CliUpdateAccepted) : Fail(result.Message ?? LinuxStrings.CliUpdateInstallFailed);
        }).ConfigureAwait(false);
    }

    private static string? UpdateRefusal(Session session, ProgramUpdateInfo update) =>
        ManagedUpdateReason(update) ?? (session.Hello.IsAdmin ? null : LinuxStrings.CliForbidden);

    private static string? ManagedUpdateReason(ProgramUpdateInfo update) => update.ManagedByPackage ? Messages.ProgramManagedByPackage
        : update.ManagedBySite ? Messages.ProgramManagedBySite : null;

    private static string UpdateChannelText(string? channel) =>
        LinuxStrings.Format(LinuxStrings.CliUpdateChannel, ProgramReleases.NormalizeChannel(channel));

    private static async Task<int> WithUpdatePasswordAsync(Session session, Func<Task<int>> action)
    {
        if (await UnlockUpdateAsync(session).ConfigureAwait(false) is { } refusal)
        {
            return Fail(refusal);
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ServiceRequestException ex) when (ex.Code == PipeProtocol.Errors.PasswordRequired)
        {
            // A password may have been set or changed while this command was preparing its request.
            return await UnlockUpdateAsync(session).ConfigureAwait(false) is { } problem
                ? Fail(problem) : await action().ConfigureAwait(false);
        }
    }

    private static async Task<string?> UnlockUpdateAsync(Session session)
    {
        var state = await session.RequestAsync(PipeProtocol.Methods.PasswordState, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.PasswordStateResult).ConfigureAwait(false);
        if (!state.Set || state.Unlocked)
        {
            return null;
        }

        if (Console.IsInputRedirected)
        {
            return LinuxStrings.CliUpdatePasswordNeedsTerminal;
        }

        string? password = ReadSecret(LinuxStrings.CliUpdatePasswordPrompt);
        if (string.IsNullOrEmpty(password))
        {
            return LinuxStrings.CliUpdatePasswordCancelled;
        }

        var result = await session.RequestAsync(PipeProtocol.Methods.PasswordUnlock, new PasswordRequest { Password = password },
            IpcJsonContext.Default.PasswordRequest, IpcJsonContext.Default.OkResult).ConfigureAwait(false);
        return result.Ok ? null : result.Message ?? Messages.PasswordWrong;
    }
}
