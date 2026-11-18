using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Custos.Core.Model;

namespace Custos.Core.Remote;

/// <summary>The custos-helper script this version of CUSTOS carries (the same file as <c>helper/custos-helper</c>).</summary>
public static partial class HelperScript
{
    /// <summary>The protocol of custos-helper this agent speaks (<c>protocol</c> of <c>info</c>).</summary>
    public const int Protocol = 1;

    private static readonly Lazy<string> TextValue = new(Load);

    /// <summary>The script, LF line endings.</summary>
    public static string Text => TextValue.Value;

    /// <summary>"1.0.0", from the line <c>readonly VERSION=…</c>.</summary>
    public static string Version => VersionLine().Match(Text) is { Success: true } match ? match.Groups[1].Value : "0";

    /// <summary>SHA-256 of the script, lower-case hex, as <c>sha256sum</c> prints it.</summary>
    public static string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));

    /// <summary>Whether <paramref name="version"/> is older than <paramref name="than"/> ("1.0.0" &lt; "1.0.10").</summary>
    public static bool IsOlder(string version, string than)
    {
        static int[] Parts(string text) =>
            [.. (text ?? string.Empty).Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0)];
        int[] a = Parts(version), b = Parts(than);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x < y;
            }
        }

        return false;
    }

    private static string Load()
    {
        using var stream = typeof(HelperScript).Assembly.GetManifestResourceStream("Custos.Core.custos-helper")
            ?? throw new InvalidOperationException("custos-helper is missing from the program.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^readonly VERSION=([0-9.]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();
}

/// <summary>
/// What the wizard writes for a Linux server (CUSTOS-remote-pull.md 3.2, 4.2 and the appendix): the presets, the
/// text of <c>helper.conf</c> and the block for a root session that installs custos-helper with it.
/// </summary>
public static partial class HelperSetup
{
    /// <summary>The settings of a preset; <see cref="RemotePresets.Custom"/> has none and gives the default.</summary>
    public static RemoteHelperSettings Preset(string? preset) => preset switch
    {
        RemotePresets.IspmanagerFull => new RemoteHelperSettings
        {
            Roots = ["/var/www", "/etc", "/usr/local/mgr5/etc", "/var/spool/cron/crontabs"],
            Excludes = [.. IspmanagerExcludes],
            DbEngines = [DbEngines.MariaDb],
        },
        RemotePresets.Linux => new RemoteHelperSettings
        {
            Roots = ["/etc", "/home", "/root", "/var/www", "/var/spool/cron"],
            DbEngines = [DbEngines.MariaDb],
        },
        _ => new RemoteHelperSettings
        {
            Roots = ["/var/www", "/etc/nginx", "/var/spool/cron/crontabs"],
            Excludes = [.. IspmanagerExcludes],
            DbEngines = [DbEngines.MariaDb],
        },
    };

    private static readonly string[] IspmanagerExcludes =
        ["/var/www/*/data/logs", "/var/www/*/data/tmp", "/var/www/*/data/mod-tmp", "/var/www/httpd-logs"];

    /// <summary>Whether the roots take the whole of <c>/etc</c> (password hashes and the SSH keys of the server).</summary>
    public static bool TakesWholeEtc(RemoteHelperSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Roots.Any(r => Normalize(r) == "/etc");
    }

    /// <summary>The first problem of the settings, in words, or null when they can be written.</summary>
    public static string? Problem(RemoteHelperSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var roots = settings.Roots.Select(Normalize).Where(r => r.Length > 0).ToList();
        if (roots.Count == 0)
        {
            return Messages.RemoteNoRoots;
        }

        foreach (string path in settings.Roots.Concat(settings.Excludes).Concat(settings.DbDefaultsFile is null ? [] : [settings.DbDefaultsFile]))
        {
            string value = path.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (!value.StartsWith('/') || value.Any(char.IsControl))
            {
                return Messages.Format(Messages.RemotePathNotAbsolute, value);
            }

            if (Normalize(value).Length == 0)
            {
                return Messages.RemoteRootIsSlash;
            }
        }

        for (int i = 0; i < roots.Count; i++)
        {
            for (int j = 0; j < roots.Count; j++)
            {
                if (i != j && (roots[i] == roots[j] || roots[j].StartsWith(roots[i] + "/", StringComparison.Ordinal)))
                {
                    return Messages.Format(Messages.RemoteRootsNested, roots[i], roots[j]);
                }
            }
        }

        var engines = settings.DbEngines.Distinct(StringComparer.Ordinal).ToList();
        if (engines.Any(e => !DbEngines.IsKnown(e)))
        {
            return Messages.Format(Messages.DbEngineUnknown, string.Join(", ", engines.Where(e => !DbEngines.IsKnown(e))));
        }

        if (engines.Contains(DbEngines.MariaDb) && engines.Contains(DbEngines.MySql))
        {
            return Messages.RemoteMariaDbAndMySql;
        }

        foreach (string name in settings.DbExclude.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            if (name.Any(c => char.IsControl(c) || c == '#'))
            {
                return Messages.Format(Messages.RemoteBadDbName, name);
            }
        }

        return null;
    }

    /// <summary>The text of <c>/etc/custos/helper.conf</c>.</summary>
    public static string Config(RemoteHelperSettings settings, string preset)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var text = new StringBuilder();
        text.Append("# Written by the CUSTOS wizard (").Append(preset).Append("). Format: docs/CUSTOS-remote-pull.md, 3.2\n");
        foreach (string root in settings.Roots.Select(Normalize).Where(r => r.Length > 0))
        {
            text.Append("root       = ").Append(root).Append('\n');
        }

        foreach (string exclude in settings.Excludes.Select(Normalize).Where(r => r.Length > 0))
        {
            text.Append("exclude    = ").Append(exclude).Append('\n');
        }

        foreach (string engine in settings.DbEngines.Distinct(StringComparer.Ordinal))
        {
            text.Append("db_engine  = ").Append(engine).Append('\n');
        }

        foreach (string name in settings.DbExclude.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            text.Append("db_exclude = ").Append(name).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(settings.DbDefaultsFile))
        {
            text.Append("db_defaults_file = ").Append(settings.DbDefaultsFile.Trim()).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The authorized_keys line: the key may only run custos-helper, from the office address if there is one.</summary>
    public static string KeyLine(string publicKey, string? officeIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        string options = "command=\"sudo -n /usr/local/sbin/custos-helper\",restrict";
        if (!string.IsNullOrWhiteSpace(officeIp))
        {
            options += ",from=\"" + officeIp.Trim() + "\"";
        }

        return options + " " + publicKey.Trim();
    }

    /// <summary>The key itself in "ssh-ed25519 AAAA… comment": what tells its authorized_keys line from the others.</summary>
    public static string KeyBlob(string publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        string[] parts = publicKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    /// <summary>Whether a text can go into <c>from=</c>: addresses, networks and patterns of OpenSSH, comma-separated.</summary>
    public static bool IsValidFrom(string? text) => string.IsNullOrWhiteSpace(text) || FromPattern().IsMatch(text.Trim());

    /// <summary>
    /// The block to paste into a root session on the server (after <c>sudo -i</c>): it creates the user custos, puts
    /// custos-helper in place after checking its SHA-256, writes helper.conf, sudo for custos-helper only and the key.
    /// It runs in a <c>bash -e</c> of its own, so an error stops it without closing the session; running it again is
    /// safe. Lines end with LF.
    /// </summary>
    public static string InstallBlock(RemoteHelperSettings settings, string preset, string publicKey, string? officeIp)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string script = HelperScript.Text;
        string config = Config(settings, preset);
        string keyLine = KeyLine(publicKey, officeIp);
        string blob = KeyBlob(publicKey);
        if (script.Contains("\nCUSTOS_HELPER_EOF\n", StringComparison.Ordinal) || config.Split('\n').Any(l => l.StartsWith("CUSTOS_", StringComparison.Ordinal))
            || keyLine.Contains('\'', StringComparison.Ordinal) || keyLine.Contains('\n', StringComparison.Ordinal) || !Base64Pattern().IsMatch(blob))
        {
            throw new InvalidOperationException("The install block cannot hold this text.");
        }

        var text = new StringBuilder();
        text.Append("bash -e <<'CUSTOS_INSTALL_EOF'\n");
        text.Append("# CUSTOS: custos-helper ").Append(HelperScript.Version).Append(" for this server. Safe to run again.\n");
        text.Append("id custos >/dev/null 2>&1 || useradd --system --create-home --shell /bin/bash --password '*' custos\n\n");
        text.Append("# 1. custos-helper and the check of its SHA-256\n");
        text.Append("cat > /usr/local/sbin/custos-helper.new <<'CUSTOS_HELPER_EOF'\n");
        text.Append(script);
        if (!script.EndsWith('\n'))
        {
            text.Append('\n');
        }

        text.Append("CUSTOS_HELPER_EOF\n");
        text.Append("echo '").Append(HelperScript.Sha256).Append("  /usr/local/sbin/custos-helper.new' | sha256sum -c -\n");
        text.Append("install -m 0755 /usr/local/sbin/custos-helper.new /usr/local/sbin/custos-helper\n");
        text.Append("rm /usr/local/sbin/custos-helper.new\n\n");
        text.Append("# 2. What custos-helper gives out. The old file, if it was different, stays as helper.conf.old\n");
        text.Append("install -d -m 0755 /etc/custos\n");
        text.Append("cat > /etc/custos/helper.conf.new <<'CUSTOS_CONF_EOF'\n");
        text.Append(config);
        text.Append("CUSTOS_CONF_EOF\n");
        text.Append("chmod 0600 /etc/custos/helper.conf.new\n");
        text.Append("if [ -e /etc/custos/helper.conf ] && ! cmp -s /etc/custos/helper.conf /etc/custos/helper.conf.new; then cp -p /etc/custos/helper.conf /etc/custos/helper.conf.old; fi\n");
        text.Append("mv /etc/custos/helper.conf.new /etc/custos/helper.conf\n\n");
        text.Append("# 3. sudo for custos-helper only. visudo checks the draft first: a broken file never takes its place\n");
        text.Append("cat > /etc/sudoers.d/custos.new <<'CUSTOS_SUDO_EOF'\n");
        text.Append("Defaults:custos env_keep += \"SSH_ORIGINAL_COMMAND\"\n");
        text.Append("custos ALL=(root) NOPASSWD: /usr/local/sbin/custos-helper\n");
        text.Append("CUSTOS_SUDO_EOF\n");
        text.Append("chmod 0440 /etc/sudoers.d/custos.new\n");
        text.Append("visudo -cf /etc/sudoers.d/custos.new\n");
        text.Append("mv /etc/sudoers.d/custos.new /etc/sudoers.d/custos\n\n");
        text.Append("# 4. The key of this CUSTOS agent: custos-helper only, no terminal, no forwarding, from the office address if\n");
        text.Append("#    there is one. An older line of the same key (with another office address) is replaced, not kept\n");
        text.Append("KEY_LINE='").Append(keyLine).Append("'\n");
        text.Append("AK=~custos/.ssh/authorized_keys\n");
        text.Append("install -d -m 0700 ~custos/.ssh\n");
        text.Append("touch \"$AK\"\n");
        text.Append("{ grep -vF -- ' ").Append(blob).Append("' \"$AK\" || [ $? -eq 1 ]; echo \"$KEY_LINE\"; } > \"$AK.new\"\n");
        text.Append("chmod 0600 \"$AK.new\"\n");
        text.Append("mv \"$AK.new\" \"$AK\"\n");
        text.Append("chown -R custos: ~custos/.ssh\n\n");
        text.Append("/usr/local/sbin/custos-helper info\n");
        text.Append("echo 'OK: custos-helper installed'\n");
        text.Append("CUSTOS_INSTALL_EOF\n");
        return text.ToString();
    }

    private static string Normalize(string path)
    {
        string value = (path ?? string.Empty).Trim();
        while (value.Length > 1 && value.EndsWith('/'))
        {
            value = value[..^1];
        }

        return value == "/" ? string.Empty : value;
    }

    [GeneratedRegex(@"^[0-9A-Za-z.:/*?!\-]+(,[0-9A-Za-z.:/*?!\-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FromPattern();

    [GeneratedRegex(@"^[A-Za-z0-9+/]{16,}=*$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Pattern();
}
