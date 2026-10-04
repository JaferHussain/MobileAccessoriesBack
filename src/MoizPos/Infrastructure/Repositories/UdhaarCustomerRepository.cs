using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class UdhaarCustomerRepository : IUdhaarCustomerRepository
{
    // Whether each side is on file, never the path itself.
    private const string Columns =
        """
        id                                   AS Id,
        name                                 AS Name,
        mobile_number                        AS MobileNumber,
        outstanding_balance                  AS OutstandingBalance,
        credit_allowed                       AS IsUdhaarCustomer,
        id_card_front_path IS NOT NULL       AS HasIdCardFront,
        id_card_back_path IS NOT NULL        AS HasIdCardBack,
        (credit_allowed AND (id_card_front_path IS NULL OR id_card_back_path IS NULL)) AS IdCardMissing
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public UdhaarCustomerRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<UdhaarCustomerRow>> ListAsync(string? search, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var filter = string.IsNullOrWhiteSpace(search) ? string.Empty : " AND (name LIKE @search OR mobile_number LIKE @search)";

        var rows = await connection.QueryAsync<UdhaarCustomerRow>(
            $"""
             SELECT {Columns}
             FROM customers
             WHERE is_active = TRUE AND credit_allowed = TRUE{filter}
             ORDER BY name, id
             LIMIT 500;
             """,
            new { search = $"%{search?.Trim()}%" });

        return rows.AsList();
    }

    public async Task<UdhaarCustomerRow?> FindAsync(long customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<UdhaarCustomerRow>(
            $"SELECT {Columns} FROM customers WHERE id = @customerId;", new { customerId });
    }

    private sealed record Paths
    {
        public string? Front { get; init; }

        public string? Back { get; init; }
    }

    public async Task<(string? Front, string? Back)> IdCardPathsAsync(long customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var paths = await connection.QuerySingleOrDefaultAsync<Paths>(
            "SELECT id_card_front_path AS Front, id_card_back_path AS Back FROM customers WHERE id = @customerId;",
            new { customerId });

        return (paths?.Front, paths?.Back);
    }

    public async Task<long> CreateAsync(
        string name, string mobileNumber, string frontPath, string backPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO customers
                (name, mobile_number, sale_type, credit_allowed, id_card_front_path, id_card_back_path,
                 outstanding_balance, is_active, created_at_utc)
            VALUES (@name, @mobileNumber, 'Retail', TRUE, @frontPath, @backPath, 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name, mobileNumber, frontPath, backPath });
    }

    public async Task RegisterAsync(
        long customerId, string? mobileNumber, string? frontPath, string? backPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            """
            UPDATE customers
            SET credit_allowed     = TRUE,
                mobile_number      = COALESCE(@mobileNumber, mobile_number),
                id_card_front_path = COALESCE(@frontPath, id_card_front_path),
                id_card_back_path  = COALESCE(@backPath, id_card_back_path),
                updated_at_utc     = UTC_TIMESTAMP(6)
            WHERE id = @customerId;
            """,
            new { customerId, mobileNumber, frontPath, backPath });
    }

    public async Task RemoveAsync(long customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE customers SET credit_allowed = FALSE, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @customerId;",
            new { customerId });
    }
}
