using System.Security.Cryptography;
using System.Text;

namespace Custos.Core.Agent;

/// <summary>
/// The program password (section 4 of the client spec): when it is set, changes and restores need it on top of the
/// rights of the user. Kept only as a hash in the form of A.2, <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>,
/// the same form the monitoring server makes, so a password set on the site works here and the other way round.
/// </summary>
public static class ProgramPassword
{
    /// <summary>The iterations of a hash made here (A.2). Hashes from the server may use another number.</summary>
    public const int Iterations = 200_000;

    /// <summary>The shortest password, the same as on the site.</summary>
    public const int MinLength = 4;

    /// <summary>The longest password: a longer one is refused before it is hashed.</summary>
    public const int MaxLength = 256;

    /// <summary>How long an interface stays unlocked after the password was typed, counted from its last change.</summary>
    public static readonly TimeSpan UnlockTime = TimeSpan.FromMinutes(30);

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>A new hash of <paramref name="password"/> with a random salt.</summary>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Whether <paramref name="password"/> is the one <paramref name="hash"/> was made of. A hash in a wrong
    /// form matches no password.</summary>
    public static bool Verify(string password, string hash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hash);
        if (password.Length > MaxLength || !ConfigStore.IsPasswordHash(hash))
        {
            return false;
        }

        string[] parts = hash.Split('$');
        int iterations = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        byte[] salt = Convert.FromBase64String(parts[2]);
        byte[] expected = Convert.FromBase64String(parts[3]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Why <paramref name="password"/> cannot become the program password, or null if it can.</summary>
    public static string? Problem(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinLength)
        {
            return Messages.Format(Messages.PasswordTooShort, MinLength);
        }

        return password.Length > MaxLength ? Messages.Format(Messages.PasswordTooLong, MaxLength) : null;
    }
}

/// <summary>
/// Whether one interface (one pipe connection) has typed the program password lately (section 4). Opens with the
/// password and stays open for <see cref="ProgramPassword.UnlockTime"/> after the last change made through it; closes
/// when the window closes, and when the password changes (here or on the site) in the meantime.
/// </summary>
public sealed class PasswordGate
{
    private readonly Lock _lock = new();
    private string? _hash;
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    /// <summary>Whether changes may be made now; without a program password (<paramref name="hash"/> null) always.</summary>
    public bool IsOpen(string? hash, DateTimeOffset now)
    {
        if (hash is null)
        {
            return true;
        }

        lock (_lock)
        {
            return _hash == hash && now < _until;
        }
    }

    /// <summary>The password was typed, or a change was just made: open for <see cref="ProgramPassword.UnlockTime"/> more.</summary>
    public void Open(string? hash, DateTimeOffset now)
    {
        if (hash is null)
        {
            return;
        }

        lock (_lock)
        {
            _hash = hash;
            _until = now + ProgramPassword.UnlockTime;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _hash = null;
            _until = DateTimeOffset.MinValue;
        }
    }
}
