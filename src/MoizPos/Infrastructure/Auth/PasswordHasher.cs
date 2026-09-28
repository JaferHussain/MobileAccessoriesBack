using System.Security.Cryptography;
using System.Text;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing.
///
/// Stored format: <c>{iterations}.{base64 salt}.{base64 hash}</c>. Keeping the iteration count
/// in the record means the work factor can be raised later without invalidating existing
/// passwords — old hashes still verify with the count they were created under.
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int DefaultIterations = 210_000;

    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    private readonly int _iterations;

    public PasswordHasher() : this(DefaultIterations)
    {
    }

    public PasswordHasher(int iterations)
    {
        if (iterations < 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations), iterations, "Iteration count is too low to be safe.");
        }

        _iterations = iterations;
    }

    public string Hash(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, _iterations);

        return string.Join('.', _iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
        {
            return false;
        }

        var parts = hash.Split('.');

        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            // A corrupted stored hash fails the sign-in rather than crashing the endpoint.
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, Algorithm, expected.Length);

        // Fixed-time comparison: a byte-by-byte early exit would leak how much of the hash matched.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, Algorithm, HashBytes);
}
