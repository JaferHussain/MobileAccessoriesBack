using System.Text;
using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;

namespace MoizPos.Infrastructure.Repositories;

/// <summary>Read side of purchasing. Writes live in <see cref="PurchaseWriteRepository"/>.</summary>
public sealed class PurchaseRepository : IPurchaseRepository
{
    private const string SelectColumns = """
        pu.id                AS Id,
        pu.supplier_id       AS SupplierId,
        pu.product_id        AS ProductId,
        p.name               AS ProductName,
        pu.purchase_date_utc AS PurchaseDateUtc,
        pu.unit_cost         AS UnitCost,
        pu.quantity          AS Quantity,
        pu.total             AS Total,
        pu.returned_qty      AS ReturnedQty,
        pu.user_id           AS UserId
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public PurchaseRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<Purchase> Items, int TotalItems)> SearchAsync(
        long? supplierId,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? productSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var where = new StringBuilder("WHERE 1 = 1");

        if (supplierId is not null)
        {
            where.Append(" AND pu.supplier_id = @supplierId");
        }

        if (fromUtc is not null)
        {
            where.Append(" AND pu.purchase_date_utc >= @fromUtc");
        }

        if (toUtc is not null)
        {
            // Half-open: the caller's range end is exclusive (research.md R6).
            where.Append(" AND pu.purchase_date_utc < @toUtc");
        }

        // "Return item": find which purchase to return against by product name, the same way
        // sale returns can, instead of scrolling every purchase from every supplier.
        if (!string.IsNullOrWhiteSpace(productSearch))
        {
            where.Append(" AND p.name LIKE @productSearch");
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT {SelectColumns}
             FROM purchases pu
             JOIN products p ON p.id = pu.product_id
             {where}
             ORDER BY pu.purchase_date_utc DESC, pu.id DESC
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM purchases pu JOIN products p ON p.id = pu.product_id {where};
             """,
            new
            {
                supplierId, fromUtc, toUtc,
                productSearch = $"%{productSearch?.Trim()}%",
                limit = pageSize, offset = (page - 1) * pageSize,
            });

        var items = (await reader.ReadAsync<Purchase>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<Purchase?> FindByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Purchase>(
            $"SELECT {SelectColumns} FROM purchases WHERE id = @id LIMIT 1;", new { id });
    }

    /// <summary>
    /// Purchases and payments interleaved in date order, so the screen can show a running
    /// payable exactly the way the shopkeeper's own register does (FR-010).
    /// </summary>
    public async Task<IReadOnlyList<SupplierLedgerRow>> LedgerForSupplierAsync(
        long supplierId,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<SupplierLedgerRow>(
            """
            SELECT p.purchase_date_utc AS EntryDateUtc,
                   'Purchase'          AS EntryType,
                   p.id                AS ReferenceId,
                   pr.name             AS Description,
                   p.total             AS PurchaseAmount,
                   0                   AS PaymentAmount
            FROM purchases p
            JOIN products pr ON pr.id = p.product_id
            WHERE p.supplier_id = @supplierId
              AND (@fromUtc IS NULL OR p.purchase_date_utc >= @fromUtc)
              AND (@toUtc   IS NULL OR p.purchase_date_utc <  @toUtc)

            UNION ALL

            SELECT sp.payment_date_utc AS EntryDateUtc,
                   'Payment'           AS EntryType,
                   sp.id               AS ReferenceId,
                   sp.note             AS Description,
                   0                   AS PurchaseAmount,
                   sp.amount           AS PaymentAmount
            FROM supplier_payments sp
            WHERE sp.supplier_id = @supplierId
              AND (@fromUtc IS NULL OR sp.payment_date_utc >= @fromUtc)
              AND (@toUtc   IS NULL OR sp.payment_date_utc <  @toUtc)

            ORDER BY EntryDateUtc, ReferenceId;
            """,
            new { supplierId, fromUtc, toUtc });

        return rows.AsList();
    }
}
