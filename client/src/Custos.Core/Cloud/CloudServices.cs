using Custos.Core.Model;

namespace Custos.Core.Cloud;

/// <summary>
/// How CUSTOS signs in to one cloud drive (section 6.3): the addresses of its OAuth 2.0 server, the rights asked for,
/// and how the code of the sign-in comes back to the program.
/// </summary>
public sealed class CloudService
{
    /// <summary>The loopback port of Yandex Disk and Dropbox: those services take only the exact redirect addresses
    /// registered with the application, so the port cannot be chosen freely.</summary>
    public const int FixedPort = 47901;

    private CloudService(string kind)
    {
        Kind = kind;
    }

    /// <summary>One of <see cref="DestinationKinds"/>: gdrive, yadisk, dropbox.</summary>
    public string Kind { get; }

    /// <summary>Where the browser signs in.</summary>
    public Uri AuthorizeUri { get; private init; } = null!;

    /// <summary>Where the service gives out tokens.</summary>
    public Uri TokenUri { get; private init; } = null!;

    /// <summary>The rights asked for; null = those the application was registered with (Yandex).</summary>
    public string? Scope { get; private init; }

    /// <summary>More parameters of the sign-in page (a refresh token, the choice of account).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Extra { get; private init; } = [];

    /// <summary>The port the code comes back to on this computer; 0 = any free port (Google lets a desktop
    /// application use any).</summary>
    public int LoopbackPort { get; private init; }

    /// <summary>The host of the loopback address as registered with the application.</summary>
    public string LoopbackHost { get; private init; } = "127.0.0.1";

    /// <summary>
    /// The redirect address under which the service shows the code on its own page for the person to copy, when the
    /// port cannot be listened on: an address (Yandex), an empty string for "no redirect address at all" (Dropbox),
    /// or null where there is no such way (Google: the address line of the browser holds the code).
    /// </summary>
    public string? CodeOnScreen { get; private init; }

    /// <summary>The service cannot do without the client secret (Google, even for desktop applications); the others
    /// take one when the application has it.</summary>
    public bool NeedsSecret { get; private init; }

    public static CloudService GDrive { get; } = new(DestinationKinds.GDrive)
    {
        AuthorizeUri = new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        TokenUri = new Uri("https://oauth2.googleapis.com/token"),
        // Only the files CUSTOS made itself; the rest of the Drive, mail and documents stay out of reach.
        Scope = "https://www.googleapis.com/auth/drive.file",
        Extra = [new("access_type", "offline"), new("prompt", "consent select_account")],
        LoopbackPort = 0,
        NeedsSecret = true,
    };

    public static CloudService YaDisk { get; } = new(DestinationKinds.YaDisk)
    {
        AuthorizeUri = new Uri("https://oauth.yandex.ru/authorize"),
        TokenUri = new Uri("https://oauth.yandex.ru/token"),
        // The rights are those of the application: the app folder and the facts about the Disk.
        Scope = null,
        Extra = [new("force_confirm", "yes")],
        LoopbackPort = FixedPort,
        LoopbackHost = "localhost",
        CodeOnScreen = "https://oauth.yandex.ru/verification_code",
    };

    public static CloudService Dropbox { get; } = new(DestinationKinds.Dropbox)
    {
        AuthorizeUri = new Uri("https://www.dropbox.com/oauth2/authorize"),
        TokenUri = new Uri("https://api.dropboxapi.com/oauth2/token"),
        Scope = "account_info.read files.metadata.read files.metadata.write files.content.read files.content.write",
        Extra = [new("token_access_type", "offline"), new("force_reapprove", "true")],
        LoopbackPort = FixedPort,
        LoopbackHost = "localhost",
        CodeOnScreen = string.Empty,
    };

    /// <summary>The service of a destination or account kind.</summary>
    /// <exception cref="ArgumentException">Not a cloud drive.</exception>
    public static CloudService Of(string kind) => kind switch
    {
        DestinationKinds.GDrive => GDrive,
        DestinationKinds.YaDisk => YaDisk,
        DestinationKinds.Dropbox => Dropbox,
        _ => throw new ArgumentException("Not a cloud drive: " + kind, nameof(kind)),
    };

    /// <summary>The loopback redirect address for a port this computer listens on.</summary>
    public string LoopbackRedirect(int port) => "http://" + LoopbackHost + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";

    /// <summary>The name of the service for people: «Google Drive», «Яндекс Диск», «Dropbox».</summary>
    public string Name => NameOf(Kind);

    public static string NameOf(string kind) => kind switch
    {
        DestinationKinds.GDrive => Messages.CloudGDrive,
        DestinationKinds.YaDisk => Messages.CloudYaDisk,
        DestinationKinds.Dropbox => Messages.CloudDropbox,
        _ => kind,
    };

    /// <summary>The folder of CUSTOS in the account, for people: «CUSTOS» in Google Drive, the app folder elsewhere.</summary>
    public static string AppFolderOf(string kind) => kind == DestinationKinds.GDrive ? "CUSTOS" : Messages.CloudAppFolder;
}
