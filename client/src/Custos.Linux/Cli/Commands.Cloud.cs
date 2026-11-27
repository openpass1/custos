using System.Text;
using Custos.Core.Cloud;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Service.Platform;

namespace Custos.Linux.Cli;

internal static partial class Commands
{
    private static Task<int> CloudAsync(string[] args) => args switch
    {
        [] or ["help"] or ["--help"] => Task.FromResult(Done(LinuxStrings.CliCloudUsage)),
        ["list"] => ListCloudAsync(),
        ["login", var kind] when DestinationKinds.IsCloudDrive(kind) => LoginCloudAsync(kind, null),
        ["login", var kind, "--account", var id] when DestinationKinds.IsCloudDrive(kind) && !string.IsNullOrWhiteSpace(id) => LoginCloudAsync(kind, id),
        ["app", var kind] when DestinationKinds.IsCloudDrive(kind) => SetCloudAppAsync(kind),
        _ => Task.FromResult(Fail(LinuxStrings.CliCloudUsage)),
    };

    private static async Task<int> ListCloudAsync()
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        if (!session.Hello.IsAdmin)
        {
            return Fail(LinuxStrings.CliForbidden);
        }

        var accounts = (await session.SnapshotAsync().ConfigureAwait(false)).Credentials.Where(c => CredentialKinds.IsCloud(c.Kind)).ToList();
        if (accounts.Count == 0)
        {
            return Done(LinuxStrings.CliCloudNoAccounts);
        }

        foreach (var account in accounts)
        {
            Console.WriteLine($"{account.Id}\t{account.Kind}\t{account.DisplayName}");
        }

        return 0;
    }

    private static async Task<int> LoginCloudAsync(string kind, string? id)
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        if (!session.Hello.IsAdmin)
        {
            return Fail(LinuxStrings.CliForbidden);
        }

        var service = CloudService.Of(kind);
        // Google no longer shows an authorization code on a page. Forward its loopback port over SSH when the
        // browser is on another computer. Yandex and Dropbox can show a code and need no listening socket.
        using var receiver = service.CodeOnScreen is null ? LoopbackReceiver.TryStart(service.LoopbackPort) : null;
        if (receiver is null && service.CodeOnScreen is null)
        {
            return Fail(LinuxStrings.CliCloudNoPort);
        }

        var begun = await session.RequestAsync(PipeProtocol.Methods.CloudBegin,
            new CloudBeginRequest { Kind = kind, CredentialId = id, RedirectUri = receiver is null ? null : service.LoopbackRedirect(receiver.Port) },
            IpcJsonContext.Default.CloudBeginRequest, IpcJsonContext.Default.CloudBeginResult).ConfigureAwait(false);

        if (receiver is not null)
        {
            Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliCloudForward, receiver.Port));
        }

        Console.WriteLine(LinuxStrings.CliCloudOpenBrowser);
        Console.WriteLine(begun.Url);

        string? answer;
        if (receiver is null)
        {
            answer = ReadSecret(LinuxStrings.CliCloudCode);
        }
        else
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            try
            {
                answer = await receiver.WaitAsync(LinuxStrings.CliCloudBrowserReceived, timeout.Token, begun.State).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return Fail(LinuxStrings.CliCloudTimedOut);
            }
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            return Fail(LinuxStrings.CliCloudCancelled);
        }

        var result = await session.RequestAsync(PipeProtocol.Methods.CloudFinish,
            new CloudFinishRequest { State = begun.State, Answer = answer }, IpcJsonContext.Default.CloudFinishRequest,
            IpcJsonContext.Default.CredentialSaveResult, timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        return Done(LinuxStrings.Format(LinuxStrings.CliCloudConnected, result.Credential.DisplayName, result.Credential.Id));
    }

    private static async Task<int> SetCloudAppAsync(string kind)
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        if (!session.Hello.IsAdmin)
        {
            return Fail(LinuxStrings.CliForbidden);
        }

        Console.Write(LinuxStrings.CliCloudClientId + " ");
        string? id = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(id))
        {
            return Fail(LinuxStrings.CliCloudCancelled);
        }

        var service = CloudService.Of(kind);
        string? secret = ReadSecret(service.NeedsSecret ? LinuxStrings.CliCloudClientSecret : LinuxStrings.CliCloudClientSecretOptional);
        if (secret is null || (service.NeedsSecret && string.IsNullOrWhiteSpace(secret)))
        {
            return Fail(LinuxStrings.CliCloudCancelled);
        }

        var app = new CloudApp { ClientId = id, ClientSecret = secret };
        var apps = new CloudAppsFile
        {
            Gdrive = kind == DestinationKinds.GDrive ? app : null,
            Yadisk = kind == DestinationKinds.YaDisk ? app : null,
            Dropbox = kind == DestinationKinds.Dropbox ? app : null,
        };
        await session.RequestAsync(PipeProtocol.Methods.CloudAppsSave, new CloudAppsSaveRequest { Apps = apps, Kind = kind },
            IpcJsonContext.Default.CloudAppsSaveRequest, IpcJsonContext.Default.OkResult).ConfigureAwait(false);
        return Done(LinuxStrings.Format(LinuxStrings.CliCloudAppSaved, service.Name));
    }

    private static string? ReadSecret(string prompt)
    {
        Console.Write(prompt + " ");
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return text.ToString();
                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return null;
                case ConsoleKey.Backspace when text.Length > 0:
                    text.Length--;
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        text.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }
}
