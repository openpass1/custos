using System.Security.Cryptography;
using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Remote;
using Custos.Core.Text;

namespace Custos.Service.Agent;

/// <summary>
/// Linux servers as sources (CUSTOS-remote-pull.md 4.1–4.2): the SSH key of a task is made and kept here, and the
/// wizard tests a server through here, with the key the runs use.
/// </summary>
internal sealed partial class AgentService
{
    /// <summary>A listing of a big server takes a while; longer than this is not a test any more.</summary>
    private static readonly TimeSpan RemoteTestTimeout = TimeSpan.FromMinutes(10);

    public Credential CreateSshKey(RemoteKeyRequest request, string user)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.ConfigSshHostRequired);
        }

        var credential = _config.CreateSshKey($"{request.User.Trim()}@{request.Host.Trim()}", "custos@" + Platform.ComputerName);
        Log(LogLineLevels.Info, Messages.Format(Messages.RemoteKeyCreated, credential.DisplayName, user));
        PublishSnapshot();
        return credential;
    }

    /// <summary>
    /// "Check the connection" in the wizard. Without the right pinned key the answer only shows the key of the server
    /// (the administrator confirms it and asks again); with it, custos-helper tells its version, databases and files.
    /// </summary>
    public RemoteTestResult TestRemote(RemoteTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.Source;
        var result = new RemoteTestResult();
        if (string.IsNullOrWhiteSpace(source.Host))
        {
            result.Error = Messages.ConfigSshHostRequired;
            return result;
        }

        byte[] seed;
        try
        {
            seed = _config.GetSshKey(source.CredentialId ?? string.Empty);
        }
        catch (ConfigException ex)
        {
            result.Error = ex.Message;
            return result;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        timeout.CancelAfter(RemoteTestTimeout);
        try
        {
            var target = new SshTarget(source.Host.Trim(), source.Port ?? 22, source.User ?? "custos", seed, source.HostKeySha256);
            using var session = Platform.Ssh.Connect(target, timeout.Token);
            result.HostKeySha256 = session.HostKeySha256;
            var helper = new HelperClient(session);
            var info = helper.Info(timeout.Token);
            result.HelperVersion = info.HelperVersion;
            result.HelperOutdated = HelperScript.IsOlder(info.HelperVersion, HelperScript.Version);
            result.Hostname = info.Hostname;
            result.Os = info.Os;
            result.Roots = [.. info.Roots.Select(r => r.Path)];
            result.ServerFreeBytes = info.Roots.Where(r => r.FreeBytes is not null).Select(r => r.FreeBytes).Min();
            if (info.Protocol != HelperScript.Protocol)
            {
                result.Error = Messages.Format(Messages.RemoteHelperIncompatible, BackupLabel(source), info.HelperVersion, info.Protocol, HelperScript.Version);
                return result;
            }

            foreach (string engine in info.DbEngines)
            {
                var databases = new RemoteDatabases { Engine = engine };
                try
                {
                    databases.Names = helper.DbList(engine, timeout.Token);
                }
                catch (HelperException ex)
                {
                    databases.Error = ex.Message;
                }

                result.Databases.Add(databases);
            }

            var (entries, warnings) = helper.Manifest(timeout.Token);
            result.Entries = entries.Count;
            result.Files = entries.Count(e => e.IsFile);
            result.Bytes = entries.Where(e => e.IsFile).Sum(e => e.Size);
            result.Warnings = warnings.Length > 0 ? HelperClient.Brief(warnings) : null;
            result.Ok = true;
        }
        catch (SshHostKeyException ex)
        {
            // Unknown or changed: the administrator has to look at it before anything is read.
            result.HostKeySha256 = ex.Actual;
            result.HostKeyUnconfirmed = true;
            result.Error = ex.Expected is null ? null : ex.Message;
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            result.Error = Messages.Format(Messages.RemoteTestTooLong, (int)RemoteTestTimeout.TotalMinutes);
        }
        catch (Exception ex) when (ex is SshFailedException or HelperException or IOException)
        {
            result.Error = ex.Message;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        return result;
    }

    private static string BackupLabel(TaskSource source) => Core.Backup.BackupEngine.RemoteLabel(source);
}
