using System.Security.Cryptography;
using System.Text;

namespace ModernFTP.Engine;

public abstract class PasswordCredential
{
    public abstract bool Verify(string password);
}

/// <summary>PBKDF2 with HMAC SHA256. The stored form is base64 hash, base64 salt and an iteration count.</summary>
public sealed class Pbkdf2Credential : PasswordCredential
{
    public const int DefaultIterations = 600_000;
    public const int HashSize = 32;
    public const int SaltSize = 16;

    public Pbkdf2Credential(byte[] hash, byte[] salt, int iterations)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(salt);
        if (hash.Length == 0 || salt.Length == 0)
        {
            throw new ArgumentException("Hash and salt must not be empty.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        Hash = hash;
        Salt = salt;
        Iterations = iterations;
    }

    /// <summary>Fixed credential verified when the user name is unknown or disabled (no password ever matches).</summary>
    internal static Pbkdf2Credential Dummy { get; } = new(new byte[HashSize], new byte[SaltSize], DefaultIterations);

    public byte[] Hash { get; }

    public byte[] Salt { get; }

    public int Iterations { get; }

    public static Pbkdf2Credential Create(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return new Pbkdf2Credential(hash, salt, iterations);
    }

    public override bool Verify(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, Salt, Iterations, HashAlgorithmName.SHA256, Hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, Hash);
    }
}

/// <summary>Plain text password, for development configs only (the config layer gates it).</summary>
public sealed class PlaintextCredential(string password) : PasswordCredential
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(password ?? throw new ArgumentNullException(nameof(password)));

    public override bool Verify(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), _expected);
    }
}

/// <summary>Accepts any password. Used only for the anonymous account when the config enables it.</summary>
public sealed class AnyPasswordCredential : PasswordCredential
{
    public static AnyPasswordCredential Instance { get; } = new();

    public override bool Verify(string password) => true;
}
