using System.Text;
using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class BrandRepository : IBrandRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public BrandRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<Brand> Items, int TotalItems)> SearchAsync(
        string? search,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var where = new StringBuilder("WHERE 1 = 1");
        var parameters = new DynamicParameters();

        if (!includeInactive)
        {
            where.Append(" AND is_active = TRUE");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Append(" AND name LIKE @search");
            parameters.Add("search", $"%{search.Trim()}%");
        }

        parameters.Add("offset", (page - 1) * pageSize);
        parameters.Add("limit", pageSize);

        var sql = $"""
            SELECT id          AS Id,
                   name        AS Name,
                   description AS Description,
                   is_local    AS IsLocal,
                   is_active   AS IsActive
            FROM brands
            {where}
            ORDER BY name, id
            LIMIT @limit OFFSET @offset;

            SELECT COUNT(*) FROM brands {where};
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var reader = await connection.QueryMultipleAsync(sql, parameters);

        var items = (await reader.ReadAsync<Brand>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<Brand?> FindByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Brand>(
            """
            SELECT id AS Id, name AS Name, description AS Description, is_local AS IsLocal, is_active AS IsActive
            FROM brands WHERE id = @id LIMIT 1;
            """,
            new { id });
    }

    public async Task<long> CreateAsync(Brand brand, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO brands (name, description, is_local, is_active, created_at_utc)
            VALUES (@Name, @Description, @IsLocal, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            brand);
    }

    public async Task UpdateAsync(Brand brand, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Renaming here renames it on every product at once — the point of the module.
        await connection.ExecuteAsync(
            """
            UPDATE brands
            SET name = @Name, description = @Description, is_local = @IsLocal,
                updated_at_utc = UTC_TIMESTAMP(6)
            WHERE id = @Id;
            """,
            brand);
    }

    public async Task SetActiveAsync(
        long id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Never hard-deleted: products reference this row, and the foreign key is RESTRICT.
        await connection.ExecuteAsync(
            "UPDATE brands SET is_active = @isActive, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id;",
            new { id, isActive });
    }

    public async Task<bool> NameExistsAsync(
        string name,
        long? excludingId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM brands
            WHERE name = @name AND (@excludingId IS NULL OR id <> @excludingId);
            """,
            new { name, excludingId }) > 0;
    }

    public async Task<int> ProductCountAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM products WHERE brand_id = @id;",
            new { id });
    }
}
