using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class SupplierRepository : ISupplierRepository
{
    private const string SelectColumns = """
        id              AS Id,
        name            AS Name,
        contact_number  AS ContactNumber,
        address         AS Address,
        cnic            AS Cnic,
        email           AS Email,
        bank_name           AS BankName,
        bank_account_title  AS BankAccountTitle,
        bank_account_number AS BankAccountNumber,
        notes           AS Notes,
        payable_balance AS PayableBalance,
        is_active       AS IsActive
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public SupplierRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<Supplier> Items, int TotalItems)> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = string.IsNullOrWhiteSpace(search)
            ? "WHERE is_active = TRUE"
            : "WHERE is_active = TRUE AND (name LIKE @search OR contact_number LIKE @search)";

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT {SelectColumns}
             FROM suppliers
             {filter}
             ORDER BY name, id
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM suppliers {filter};
             """,
            new
            {
                search = $"%{search?.Trim()}%",
                limit = pageSize,
                offset = (page - 1) * pageSize,
            });

        var items = (await reader.ReadAsync<Supplier>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<Supplier?> FindByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Supplier>(
            $"SELECT {SelectColumns} FROM suppliers WHERE id = @id LIMIT 1;", new { id });
    }

    public async Task<long> CreateAsync(Supplier supplier, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO suppliers (
                name, contact_number, address, cnic, email,
                bank_name, bank_account_title, bank_account_number, notes,
                payable_balance, is_active, created_at_utc)
            VALUES (
                @Name, @ContactNumber, @Address, @Cnic, @Email,
                @BankName, @BankAccountTitle, @BankAccountNumber, @Notes,
                0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            supplier);
    }

    public async Task UpdateAsync(Supplier supplier, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // payable_balance is deliberately absent: it moves only inside purchase, payment and
        // purchase-return transactions, never by editing the supplier's contact details.
        await connection.ExecuteAsync(
            """
            UPDATE suppliers
            SET name = @Name,
                contact_number = @ContactNumber,
                address = @Address,
                cnic = @Cnic,
                email = @Email,
                bank_name = @BankName,
                bank_account_title = @BankAccountTitle,
                bank_account_number = @BankAccountNumber,
                notes = @Notes,
                updated_at_utc = UTC_TIMESTAMP(6)
            WHERE id = @Id;
            """,
            supplier);
    }
}

/// <inheritdoc />
public sealed class StockMovementRepository : IStockMovementRepository
{
    private const string SelectColumns = """
        id             AS Id,
        product_id     AS ProductId,
        change_qty     AS ChangeQty,
        resulting_qty  AS ResultingQty,
        reason         AS Reason,
        reference_id   AS ReferenceId,
        user_id        AS UserId,
        note           AS Note,
        created_at_utc AS CreatedAtUtc
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public StockMovementRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<StockMovement> Items, int TotalItems)> ListForProductAsync(
        long productId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT {SelectColumns}
             FROM stock_movements
             WHERE product_id = @productId
             ORDER BY created_at_utc DESC, id DESC
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM stock_movements WHERE product_id = @productId;
             """,
            new { productId, limit = pageSize, offset = (page - 1) * pageSize });

        var items = (await reader.ReadAsync<StockMovement>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<StockMovement?> LatestForProductAsync(
        long productId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<StockMovement>(
            $"""
             SELECT {SelectColumns}
             FROM stock_movements
             WHERE product_id = @productId
             ORDER BY id DESC
             LIMIT 1;
             """,
            new { productId });
    }
}
