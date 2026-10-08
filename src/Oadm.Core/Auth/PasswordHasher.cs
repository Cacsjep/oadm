using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Oadm.Core.Auth;

/// <summary>
/// PBKDF2-SHA256 password hashes, stored as <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt base64&gt;$&lt;hash base64&gt;</c>
/// with a 16-byte random salt and a 32-byte hash. The iteration count is part of the hash, so a later increase keeps
/// old hashes verifiable.
/// </summary>
public sealed class PasswordHasher(int iterations = PasswordHasher.DefaultIterations)
{
    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 210_000;

    public const int SaltSize = 16;
    public const int HashSize = 32;
    private const string Prefix = "pbkdf2-sha256";

    /// <summary>A hash that never verifies, used to spend the same time for unknown user names.</summary>
    private readonly Lazy<string> _dummy = new(() => new PasswordHasher(iterations).Hash(Guid.NewGuid().ToString()));

    public int Iterations { get; } = iterations > 0 ? iterations : throw new ArgumentOutOfRangeException(nameof(iterations));

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return string.Join('$', Prefix, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Constant-time check of <paramref name="password"/> against a stored hash; false for a malformed hash.</summary>
    public static bool Verify(string password, string? storedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        var parts = (storedHash ?? string.Empty).Split('$');
        if (parts.Length != 4 || parts[0] != Prefix
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, count, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Spends the time of one verification (unknown user names take as long as wrong passwords).</summary>
    public void VerifyDummy(string password) => Verify(password, _dummy.Value);
}
