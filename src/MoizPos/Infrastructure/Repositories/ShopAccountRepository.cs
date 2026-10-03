using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class ShopAccountRepository : IShopAccountRepository
{
    private const string Columns =
        """
        id AS Id, name AS Name, account_type AS AccountType, account_number AS AccountNumber,
        account_title AS AccountTitle, is_active AS IsActive
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public ShopAccountRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<ShopAccount>> ListAsync(
        bool includeInactive, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ShopAccount>(
            $"""
             SELECT {Columns}
             FROM shop_accounts
             WHERE @includeInactive OR is_active = TRUE
             ORDER BY is_active DESC, name;
             """,
            new { includeInactive });

        return rows.AsList();
    }

    public async Task<ShopAccount?> FindAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ShopAccount>(
            $"SELECT {Columns} FROM shop_accounts WHERE id = @id LIMIT 1;", new { id });
    }

    public async Task<bool> NameTakenAsync(string name, long? exceptId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM shop_accounts WHERE name = @name AND (@exceptId IS NULL OR id <> @exceptId));",
            new { name, exceptId });
    }

    public async Task<long> CreateAsync(ShopAccountInput input, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO shop_accounts (name, account_type, account_number, account_title, is_active, created_at_utc)
            VALUES (@Name, @AccountType, @AccountNumber, @AccountTitle, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            // The enum spelled out: Dapper would otherwise write its int, which the ENUM rejects.
            new { input.Name, AccountType = input.AccountType.ToString(), input.AccountNumber, input.AccountTitle });
    }

    public async Task UpdateAsync(long id, ShopAccountInput input, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            """
            UPDATE shop_accounts
            SET name = @Name, account_type = @AccountType, account_number = @AccountNumber,
                account_title = @AccountTitle, updated_at_utc = UTC_TIMESTAMP(6)
            WHERE id = @id;
            """,
            new { id, input.Name, AccountType = input.AccountType.ToString(), input.AccountNumber, input.AccountTitle });
    }

    public async Task SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE shop_accounts SET is_active = @isActive, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id;",
            new { id, isActive });
    }
}
