using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>Hashes and verifies user passwords.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>Verifies a password without leaking timing information about the hash.</summary>
    bool Verify(string password, string hash);
}

/// <summary>An issued access/refresh token pair.</summary>
public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAtUtc);

/// <summary>Issues JWT access tokens and the opaque refresh tokens that renew them.</summary>
public interface ITokenService
{
    string CreateAccessToken(User user, DateTime nowUtc);

    /// <summary>A cryptographically random, opaque refresh token. Only its hash is stored.</summary>
    string CreateRefreshToken();

    /// <summary>SHA-256 of a refresh token, used for storage and lookup.</summary>
    string HashRefreshToken(string refreshToken);
}

/// <summary>Reads and writes users.</summary>
public interface IUserRepository
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<User?> FindByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(User user, CancellationToken cancellationToken = default);

    Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Deactivates or reinstates a login. Users are never deleted — the audit trail
    /// must keep naming who did what.</summary>
    Task SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default);

    Task UpdatePasswordAsync(
        long id, string passwordHash, CancellationToken cancellationToken = default);
}

/// <summary>Persists refresh tokens so a departed employee can be revoked immediately.</summary>
public interface IRefreshTokenRepository
{
    Task StoreAsync(
        long userId,
        string tokenHash,
        DateTime expiresAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<StoredRefreshToken?> FindAsync(string tokenHash, CancellationToken cancellationToken = default);

    Task RevokeAsync(long id, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Revokes every live token for a user — used on logout and on reuse detection.</summary>
    Task RevokeAllForUserAsync(long userId, DateTime nowUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// A refresh token as stored.
///
/// CONVENTION: types materialized by Dapper use init-only properties, never a positional record.
/// Dapper matches a positional record by constructor signature and will not convert types while
/// doing so — our ids are BIGINT UNSIGNED (ulong) in MySQL but long in the domain, which fails
/// constructor matching. Property-based mapping converts them without complaint.
/// </summary>
public sealed record StoredRefreshToken
{
    public long Id { get; init; }

    public long UserId { get; init; }

    public DateTime ExpiresAtUtc { get; init; }

    public DateTime? RevokedAtUtc { get; init; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}

/// <summary>The signed-in user, as the API sees them.</summary>
public sealed record AuthenticatedUser(long Id, string Username, string FullName, UserRole Role);

/// <summary>The result of a successful sign-in or refresh.</summary>
public sealed record AuthResult(TokenPair Tokens, AuthenticatedUser User);
