using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class UserRepository : IUserRepository
{
    private const string SelectColumns = """
        id, username, full_name, password_hash, role, is_active, created_at_utc
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public UserRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<User?> FindByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<User>(
            $"SELECT {SelectColumns} FROM users WHERE username = @username LIMIT 1;",
            new { username });
    }

    public async Task<User?> FindByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<User>(
            $"SELECT {SelectColumns} FROM users WHERE id = @id LIMIT 1;",
            new { id });
    }

    public async Task<long> CreateAsync(User user, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, full_name, password_hash, role, is_active, created_at_utc)
            VALUES (@Username, @FullName, @PasswordHash, @Role, @IsActive, @CreatedAtUtc);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                user.Username,
                user.FullName,
                user.PasswordHash,
                Role = user.Role.ToString(),
                user.IsActive,
                CreatedAtUtc = user.CreatedAtUtc == default ? DateTime.UtcNow : user.CreatedAtUtc,
            });
    }

    /// <summary>
    /// Used to refuse deactivating the last Admin — a shop locked out of its own system has no
    /// way back in without direct database access (data-model.md §1).
    /// </summary>
    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<User>(
            $"SELECT {SelectColumns} FROM users ORDER BY is_active DESC, username;");

        return rows.AsList();
    }

    public async Task SetActiveAsync(
        long id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE users SET is_active = @isActive, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id;",
            new { id, isActive });
    }

    public async Task UpdatePasswordAsync(
        long id,
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE users SET password_hash = @passwordHash, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id;",
            new { id, passwordHash });
    }

    public async Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE role = 'Admin' AND is_active = TRUE;");
    }
}
