using System.Net.Http;
using System.Security.Authentication;
using Custos.Core.Model;

namespace Custos.Core;

/// <summary>
/// Which edition of CUSTOS this is (client/Directory.Build.props):
/// <list type="bullet">
/// <item>standard: .NET 10, 64-bit, Windows 10 / Server 2019 and later;</item>
/// <item>legacy: .NET 6, 32-bit, Windows 7 SP1 and 8.1 (built with <c>-p:CustosLegacy=true</c>). It is released as
/// is and updated only in emergencies, so what may not work on those Windows is switched off here instead of being
/// left half-working: cloud drives (their sign-in needs a current browser), the dark theme (Windows 7 has none),
/// the 7-Zip update from the site (the site keeps the 64-bit 7-Zip).</item>
/// </list>
/// The site learns the edition from the <c>edition</c> field of the registration and the heartbeat (A.3, A.4) and
/// hides what the legacy edition cannot do; update packages carry it in manifest.json (A.10).
/// </summary>
public static class CustosEdition
{
    /// <summary>The value of <c>edition</c> for the legacy edition (A.3, A.4, A.10).</summary>
    public const string LegacyName = "legacy";

#if CUSTOS_LEGACY
    /// <summary>True in the build for Windows 7 and 8.1.</summary>
    public static bool IsLegacy => true;
#else
    /// <summary>True in the build for Windows 7 and 8.1.</summary>
    public static bool IsLegacy => false;
#endif

    /// <summary>What the agent sends as <c>edition</c> and expects in manifest.json; null (not sent) for the standard edition.</summary>
    public static string? Wire => IsLegacy ? LegacyName : null;

    /// <summary>Tags of the program on GitHub: <c>client-vX.Y.Z</c>, or <c>client-legacy-vX.Y.Z</c> for the legacy edition.</summary>
    public static string ReleaseTagPrefix => IsLegacy ? "client-legacy-v" : "client-v";

    /// <summary>The update package of a version: <c>custos-1.1.0.zip</c> or <c>custos-legacy-1.1.0.zip</c>.</summary>
    public static string PackageName(string version) => IsLegacy ? $"custos-legacy-{version}.zip" : $"custos-{version}.zip";

    /// <summary>
    /// The string the site signs for an update package (A.10): <c>"{version}\n{sha256}"</c>, and for the legacy
    /// edition <c>"legacy\n{version}\n{sha256}"</c>, so that a signature of one edition's package is never taken for
    /// the other's.
    /// </summary>
    public static string PackageSignedString(string version, string sha256) =>
        IsLegacy ? $"{LegacyName}\n{version}\n{sha256}" : $"{version}\n{sha256}";

    /// <summary>Destination kinds this edition can write to: the legacy edition has no cloud drives.</summary>
    public static bool Supports(string kind) => !(IsLegacy && DestinationKinds.IsCloudDrive(kind));

    /// <summary>Why a destination of this kind does not work here.</summary>
    public static string DestinationNotSupported(string kind) =>
        IsLegacy && DestinationKinds.IsCloudDrive(kind)
            ? Messages.Format(Messages.DestinationKindNotInLegacy, kind)
            : Messages.Format(Messages.DestinationKindNotSupported, kind);

    /// <summary>
    /// Connections over TLS: Windows 7 offers only TLS 1.0 unless a program asks for TLS 1.2 itself, and neither
    /// Windows 7 nor 8.1 knows TLS 1.3. The legacy edition therefore asks for TLS 1.2; the standard one leaves the choice
    /// to Windows.
    /// </summary>
    public static SslProtocols TlsProtocols => IsLegacy ? SslProtocols.Tls12 : SslProtocols.None;

    /// <summary>Applies <see cref="TlsProtocols"/> to a handler; returns it.</summary>
    public static SocketsHttpHandler WithTls(this SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (IsLegacy)
        {
            handler.SslOptions.EnabledSslProtocols = TlsProtocols;
        }

        return handler;
    }
}
