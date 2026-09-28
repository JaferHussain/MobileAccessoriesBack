using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc cref="IReturnReadRepository" />
public sealed class ReturnReadRepository : IReturnReadRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ReturnReadRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<ReturnableLineRow>> FindReturnableLinesAsync(
        string search,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return (await connection.QueryAsync<ReturnableLineRow>(
            """
            SELECT
                i.id               AS InvoiceId,
                i.invoice_number   AS InvoiceNumber,
                ii.id              AS InvoiceItemId,
                p.name             AS ProductName,
                ii.quantity        AS Quantity,
                ii.returned_qty    AS ReturnedQty,
                ii.unit_sale_price AS UnitSalePrice,
                ii.line_discount   AS LineDiscount,
                ii.line_total      AS LineTotal,
                -- Both halves of the order discount travel with the row: the search list shows
                -- what a unit is really worth back, and the arithmetic stays in ReturnPricing.
                i.subtotal         AS InvoiceSubtotal,
                i.total            AS InvoiceTotal,
                i.amount_remaining AS AmountRemaining
            FROM invoice_items ii
            JOIN invoices i ON i.id = ii.invoice_id
            JOIN products p ON p.id = ii.product_id
            LEFT JOIN customers c ON c.id = i.customer_id
            WHERE (p.name LIKE @search
                   OR i.invoice_number LIKE @search
                   OR c.mobile_number LIKE @search)
              AND ii.quantity > ii.returned_qty
            ORDER BY i.invoice_date_utc DESC, ii.id DESC
            LIMIT @limit;
            """,
            new { search = $"%{search.Trim()}%", limit }))
            .AsList();
    }

    public async Task<(IReadOnlyList<SaleReturnRow> Items, int TotalItems)> SearchSaleReturnsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            """
            SELECT
                sr.id              AS ReturnId,
                sr.return_number   AS ReturnNumber,
                sr.return_date_utc AS ReturnDateUtc,
                sr.invoice_id      AS InvoiceId,
                i.invoice_number   AS InvoiceNumber,
                p.name             AS ProductName,
                sri.quantity       AS Quantity,
                -- Billed, adjusted and refunded, all three — the discount is part of the record.
                sri.unit_sale_price * sri.quantity AS BilledTotal,
                sri.discount_total AS DiscountTotal,
                sri.line_total     AS LineTotal,
                sr.refund_due      AS RefundDue,
                sr.reason          AS Reason
            FROM sale_return_items sri
            JOIN sale_returns sr ON sr.id = sri.sale_return_id
            JOIN invoices i ON i.id = sr.invoice_id
            JOIN products p ON p.id = sri.product_id
            ORDER BY sr.return_date_utc DESC, sr.id DESC, sri.id
            LIMIT @limit OFFSET @offset;

            SELECT COUNT(*) FROM sale_return_items;
            """,
            new { limit = pageSize, offset = (page - 1) * pageSize });

        var items = (await reader.ReadAsync<SaleReturnRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<(IReadOnlyList<PurchaseReturnRow> Items, int TotalItems)> SearchPurchaseReturnsAsync(
        long? supplierId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = supplierId is null ? "" : "WHERE pr.supplier_id = @supplierId";

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT
                 pr.id              AS ReturnId,
                 pr.return_number   AS ReturnNumber,
                 pr.return_date_utc AS ReturnDateUtc,
                 pr.supplier_id     AS SupplierId,
                 s.name             AS SupplierName,
                 p.name             AS ProductName,
                 pr.quantity        AS Quantity,
                 pr.total           AS Total,
                 pr.reason          AS Reason
             FROM purchase_returns pr
             JOIN suppliers s ON s.id = pr.supplier_id
             JOIN products p ON p.id = pr.product_id
             {filter}
             ORDER BY pr.return_date_utc DESC, pr.id DESC
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM purchase_returns pr {filter};
             """,
            new { supplierId, limit = pageSize, offset = (page - 1) * pageSize });

        var items = (await reader.ReadAsync<PurchaseReturnRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }
}
