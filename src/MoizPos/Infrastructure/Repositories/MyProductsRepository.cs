using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc cref="IMyProductsRepository" />
public sealed class MyProductsRepository : IMyProductsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public MyProductsRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<MyProductRow>> AssignedAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return (await connection.QueryAsync<MyProductRow>(
            """
            SELECT p.id                          AS ProductId,
                   p.name                        AS ProductName,
                   ss.quantity                   AS Quantity,
                   p.retail_price                AS Price,
                   ROUND(ss.quantity * p.retail_price, 2) AS Value,
                   ss.commission_per_unit        AS CommissionPerUnit
            FROM salesman_stock ss
            JOIN products p ON p.id = ss.product_id
            WHERE ss.user_id = @userId AND ss.quantity > 0
            ORDER BY p.name;
            """,
            new { userId })).AsList();
    }

    public async Task<IReadOnlyList<MySoldProductRow>> SoldAsync(
        long userId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Each line's worth after its share of the whole-bill discount (line_total × total/subtotal),
        // then only the units that did not come back — the same spread ReturnPricing refunds at.
        return (await connection.QueryAsync<MySoldProductRow>(
            """
            SELECT p.id   AS ProductId,
                   p.name AS ProductName,
                   CAST(SUM(ii.quantity - ii.returned_qty) AS SIGNED) AS QuantitySold,
                   ROUND(SUM(
                       ii.line_total
                       * (CASE WHEN i.subtotal > 0 THEN i.total / i.subtotal ELSE 1 END)
                       * (ii.quantity - ii.returned_qty) / ii.quantity
                   ), 2) AS Amount
            FROM invoice_items ii
            JOIN invoices i ON i.id = ii.invoice_id
            JOIN products p ON p.id = ii.product_id
            WHERE i.user_id = @userId
              AND i.invoice_date_utc >= @startUtc
              AND i.invoice_date_utc <  @endUtc
              AND ii.quantity > ii.returned_qty
            GROUP BY p.id, p.name
            ORDER BY Amount DESC, p.name;
            """,
            new { userId, startUtc, endUtc })).AsList();
    }
}
