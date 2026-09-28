using System.Text;
using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class CategoryRepository : ICategoryRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public CategoryRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<Category> Items, int TotalItems)> SearchAsync(
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
                   is_active   AS IsActive
            FROM categories
            {where}
            ORDER BY name, id
            LIMIT @limit OFFSET @offset;

            SELECT COUNT(*) FROM categories {where};
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var reader = await connection.QueryMultipleAsync(sql, parameters);

        var items = (await reader.ReadAsync<Category>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<Category?> FindByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Category>(
            """
            SELECT id AS Id, name AS Name, description AS Description, is_active AS IsActive
            FROM categories WHERE id = @id LIMIT 1;
            """,
            new { id });
    }

    public async Task<long> CreateAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO categories (name, description, is_active, created_at_utc)
            VALUES (@Name, @Description, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            category);
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Renaming here renames it on every product at once — the point of the module.
        await connection.ExecuteAsync(
            """
            UPDATE categories
            SET name = @Name, description = @Description, updated_at_utc = UTC_TIMESTAMP(6)
            WHERE id = @Id;
            """,
            category);
    }

    public async Task SetActiveAsync(
        long id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Never hard-deleted: products reference this row, and the foreign key is RESTRICT.
        await connection.ExecuteAsync(
            "UPDATE categories SET is_active = @isActive, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id;",
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
            SELECT COUNT(*) FROM categories
            WHERE name = @name AND (@excludingId IS NULL OR id <> @excludingId);
            """,
            new { name, excludingId }) > 0;
    }

    public async Task<int> ProductCountAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM products WHERE category_id = @id;",
            new { id });
    }
}
