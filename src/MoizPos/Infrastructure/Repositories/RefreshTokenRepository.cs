using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RefreshTokenRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task StoreAsync(
        long userId,
        string tokenHash,
        DateTime expiresAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            """
            INSERT INTO refresh_tokens (user_id, token_hash, expires_at_utc, created_at_utc)
            VALUES (@userId, @tokenHash, @expiresAtUtc, @nowUtc);
            """,
            new { userId, tokenHash, expiresAtUtc, nowUtc });
    }

    public async Task<StoredRefreshToken?> FindAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<StoredRefreshToken>(
            """
            SELECT id AS Id, user_id AS UserId, expires_at_utc AS ExpiresAtUtc,
                   revoked_at_utc AS RevokedAtUtc
            FROM refresh_tokens
            WHERE token_hash = @tokenHash
            LIMIT 1;
            """,
            new { tokenHash });
    }

    public async Task RevokeAsync(long id, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE refresh_tokens SET revoked_at_utc = @nowUtc WHERE id = @id AND revoked_at_utc IS NULL;",
            new { id, nowUtc });
    }

    /// <summary>
    /// Revokes every live token for a user. Used on logout, and on detecting a replayed token —
    /// replay means the token leaked, so the whole chain is burned rather than just that one.
    /// </summary>
    public async Task RevokeAllForUserAsync(
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            """
            UPDATE refresh_tokens
            SET revoked_at_utc = @nowUtc
            WHERE user_id = @userId AND revoked_at_utc IS NULL;
            """,
            new { userId, nowUtc });
    }
}
