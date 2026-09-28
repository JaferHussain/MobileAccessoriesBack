using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <summary>
/// Read-only reporting aggregations.
///
/// <para><b>Where profit comes from.</b> Every figure is summed from what each sale line
/// recorded at the time — <c>unit_cost_price</c>, not <c>products.cost_price</c>. Under the
/// shop's latest-cost rule the product's cost moves with every purchase, so joining to it would
/// silently rewrite the profit of every past month (FR-011c).</para>
///
/// <para>All timestamps are UTC; the caller resolves shop-local period boundaries and passes a
/// half-open range, so a sale lands in exactly one bucket (FR-034, research.md R6).</para>
/// </summary>
public sealed class ReportRepository : IReportRepository
{
    /// <summary>
    /// Gross profit for one invoice line (FR-031). Returned units contribute nothing (FR-027).
    /// </summary>
    private const string LineProfitExpression = """
        ((ii.unit_sale_price - ii.unit_cost_price) * (ii.quantity - ii.returned_qty) - ii.line_discount)
        """;

    /// <summary>Sale value of the units still sold, i.e. net of returns.</summary>
    private const string LineNetSaleExpression = """
        (ii.unit_sale_price * (ii.quantity - ii.returned_qty) - ii.line_discount)
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public ReportRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<decimal> GrossProfitAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<decimal>(
            $"""
             SELECT COALESCE(SUM({LineProfitExpression}), 0)
             FROM invoice_items ii
             JOIN invoices i ON i.id = ii.invoice_id
             WHERE i.invoice_date_utc >= @startUtc AND i.invoice_date_utc < @endUtc;
             """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task<decimal> TotalSalesAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // net_amount, so returns reduce reported sales.
        return await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM(net_amount), 0) FROM invoices
            WHERE invoice_date_utc >= @startUtc AND invoice_date_utc < @endUtc;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task<decimal> TotalPurchasesAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM(p.total), 0)
                 - COALESCE((SELECT SUM(pr.total) FROM purchase_returns pr
                             WHERE pr.return_date_utc >= @startUtc AND pr.return_date_utc < @endUtc), 0)
            FROM purchases p
            WHERE p.purchase_date_utc >= @startUtc AND p.purchase_date_utc < @endUtc;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task<decimal> TotalSaleReturnsAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM(total_amount), 0) FROM sale_returns
            WHERE return_date_utc >= @startUtc AND return_date_utc < @endUtc;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task<decimal> TotalPurchaseReturnsAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM(total), 0) FROM purchase_returns
            WHERE return_date_utc >= @startUtc AND return_date_utc < @endUtc;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task<CreditBreakdown> CreditBreakdownAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Split by what was actually settled, not by payment_method: a sale labelled "Cash"
        // whose payment fell short is still credit, and the label is the one part a caller
        // controls. The two halves are mutually exclusive by construction (amount_paid = 0
        // versus amount_paid > 0), so nothing is counted twice, and together they sum to the
        // period's CreditSales — which is the point: they explain that figure.
        return await connection.QuerySingleAsync<CreditBreakdown>(
            """
            SELECT
                COALESCE(SUM(amount_paid = 0), 0)                                  AS UdhaarCount,
                COALESCE(SUM(CASE WHEN amount_paid = 0
                                  THEN amount_remaining ELSE 0 END), 0)            AS UdhaarAmount,
                COALESCE(SUM(amount_paid > 0), 0)                                  AS PartPaidCount,
                COALESCE(SUM(CASE WHEN amount_paid > 0
                                  THEN amount_remaining ELSE 0 END), 0)            AS PartPaidRemaining
            FROM invoices
            WHERE invoice_date_utc >= @startUtc AND invoice_date_utc < @endUtc
              AND amount_remaining > 0;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task<(decimal CashSales, decimal CreditSales)> SalesBySettlementAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Split by what was actually settled at the counter rather than by payment method, so a
        // partial payment contributes to both halves — which is what it is.
        var row = await connection.QuerySingleAsync<(decimal Cash, decimal Credit)>(
            """
            SELECT COALESCE(SUM(amount_paid), 0)      AS Cash,
                   COALESCE(SUM(amount_remaining), 0) AS Credit
            FROM invoices
            WHERE invoice_date_utc >= @startUtc AND invoice_date_utc < @endUtc;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });

        return row;
    }

    /// <summary>
    /// Each salesman's period. No cost, no profit: this answers "who took the money and who gave
    /// the discounts", which is what pairs with a short drawer.
    /// </summary>
    private const string SalesByUserSql = """
        SELECT
            u.id        AS UserId,
            u.full_name AS UserName,
            COUNT(i.id) AS InvoiceCount,
            -- Net of returns, so it agrees with every other sales total in the system.
            COALESCE(SUM(i.net_amount), 0)       AS TotalSales,
            COALESCE(SUM(i.amount_paid), 0)      AS CashTaken,
            COALESCE(SUM(i.amount_remaining), 0) AS CreditGiven,
            -- Both kinds. Line discounts are already inside the subtotal, so the whole-bill
            -- figure on its own would understate what was actually given away.
            COALESCE(SUM(i.order_discount), 0) + COALESCE(SUM(d.line_discounts), 0)
                AS DiscountGiven
        FROM invoices i
        JOIN users u ON u.id = i.user_id
        LEFT JOIN (
            SELECT invoice_id, SUM(line_discount) AS line_discounts
            FROM invoice_items
            GROUP BY invoice_id
        ) d ON d.invoice_id = i.id
        WHERE i.invoice_date_utc >= @startUtc AND i.invoice_date_utc < @endUtc
        GROUP BY u.id, u.full_name
        ORDER BY TotalSales DESC;
        """;

    public async Task<IReadOnlyList<UserSalesRow>> SalesByUserAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<UserSalesRow>(
            SalesByUserSql,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<SaleTypeTotalsRow>> SalesBySaleTypeAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Both sides are always returned, zero included: a day with no wholesale sales must show
        // "Wholesale 0", not hide the row and leave the owner wondering whether it was recorded.
        var rows = await connection.QueryAsync<SaleTypeTotalsRow>(
            """
            SELECT t.sale_type AS SaleType,
                   COALESCE(SUM(i.net_amount), 0) AS TotalSales,
                   COUNT(i.id)                    AS InvoiceCount,
                   COALESCE((
                       SELECT SUM(ii.quantity - ii.returned_qty)
                       FROM invoice_items ii
                       JOIN invoices i2 ON i2.id = ii.invoice_id
                       WHERE i2.sale_type = t.sale_type
                         AND i2.invoice_date_utc >= @startUtc
                         AND i2.invoice_date_utc <  @endUtc
                   ), 0)                          AS ItemsSold
            FROM (SELECT 'Retail' AS sale_type UNION ALL SELECT 'Wholesale') t
            LEFT JOIN invoices i
                   ON i.sale_type = t.sale_type
                  AND i.invoice_date_utc >= @startUtc
                  AND i.invoice_date_utc <  @endUtc
            GROUP BY t.sale_type
            ORDER BY t.sale_type;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });

        return rows.AsList();
    }

    public async Task<(IReadOnlyList<SaleListRow> Items, int TotalItems)> SalesListAsync(
        DateRangeUtc range,
        SaleType? saleType,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // A null saleType means "both", so this one query serves the whole day and either half.
        const string Filter = """
            WHERE i.invoice_date_utc >= @startUtc
              AND i.invoice_date_utc <  @endUtc
              AND (@saleType IS NULL OR i.sale_type = @saleType)
            """;

        var sql = $"""
            SELECT i.id                AS InvoiceId,
                   i.invoice_number    AS InvoiceNumber,
                   i.invoice_date_utc  AS InvoiceDateUtc,
                   i.sale_type         AS SaleType,
                   i.customer_id       AS CustomerId,
                   c.name              AS CustomerName,
                   u.full_name         AS UserName,
                   i.net_amount        AS Total,
                   i.amount_paid       AS AmountPaid,
                   i.amount_remaining  AS AmountRemaining,
                   i.payment_method    AS PaymentMethod,
                   (SELECT COALESCE(SUM(ii.quantity), 0)
                    FROM invoice_items ii WHERE ii.invoice_id = i.id) AS ItemCount
            FROM invoices i
            LEFT JOIN customers c ON c.id = i.customer_id
            JOIN users u ON u.id = i.user_id
            {Filter}
            ORDER BY i.invoice_date_utc DESC, i.id DESC
            LIMIT @limit OFFSET @offset;

            SELECT COUNT(*) FROM invoices i {Filter};
            """;

        await using var reader = await connection.QueryMultipleAsync(
            sql,
            new
            {
                startUtc = range.StartUtc,
                endUtc = range.EndUtc,
                saleType = saleType?.ToString(),
                limit = pageSize,
                offset = (page - 1) * pageSize,
            });

        var items = (await reader.ReadAsync<SaleListRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<int> ItemsSoldAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(SUM(ii.quantity - ii.returned_qty), 0)
            FROM invoice_items ii
            JOIN invoices i ON i.id = ii.invoice_id
            WHERE i.invoice_date_utc >= @startUtc AND i.invoice_date_utc < @endUtc;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task<decimal> TotalReceivablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT COALESCE(SUM(outstanding_balance), 0) FROM customers WHERE outstanding_balance > 0;");
    }

    public async Task<decimal> TotalPayablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT COALESCE(SUM(payable_balance), 0) FROM suppliers WHERE payable_balance > 0;");
    }

    public async Task<IReadOnlyList<PeriodTotalsRow>> SalesByPeriodAsync(
        DateRangeUtc range,
        ReportGroupingSql grouping,
        CancellationToken cancellationToken = default)
    {
        // Bucket labels are produced in shop-local time so "9 September" means the shop's day,
        // not the UTC one (FR-034).
        var bucket = grouping switch
        {
            ReportGroupingSql.Day => "DATE_FORMAT(CONVERT_TZ(i.invoice_date_utc, '+00:00', '+05:00'), '%Y-%m-%d')",
            ReportGroupingSql.Week => "DATE_FORMAT(CONVERT_TZ(i.invoice_date_utc, '+00:00', '+05:00'), '%x-W%v')",
            ReportGroupingSql.Month => "DATE_FORMAT(CONVERT_TZ(i.invoice_date_utc, '+00:00', '+05:00'), '%Y-%m')",
            _ => "DATE_FORMAT(CONVERT_TZ(i.invoice_date_utc, '+00:00', '+05:00'), '%Y')",
        };

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PeriodTotalsRow>(
            $"""
             SELECT {bucket}                                    AS Period,
                    COALESCE(SUM({LineNetSaleExpression}), 0)   AS TotalSales,
                    COALESCE(SUM({LineProfitExpression}), 0)    AS GrossProfit,
                    COUNT(DISTINCT i.id)                        AS InvoiceCount,
                    COALESCE(SUM(ii.quantity - ii.returned_qty), 0) AS ItemsSold
             FROM invoices i
             JOIN invoice_items ii ON ii.invoice_id = i.id
             WHERE i.invoice_date_utc >= @startUtc AND i.invoice_date_utc < @endUtc
             GROUP BY Period
             ORDER BY Period;
             """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<ProductProfitRow>> ProfitByProductAsync(
        DateRangeUtc range,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ProductProfitRow>(
            $"""
             SELECT ii.product_id                                 AS ProductId,
                    MAX(ii.product_name)                          AS ProductName,
                    COALESCE(SUM(ii.quantity - ii.returned_qty), 0) AS QuantitySold,
                    COALESCE(SUM({LineNetSaleExpression}), 0)     AS TotalSale,
                    COALESCE(SUM(ii.unit_cost_price * (ii.quantity - ii.returned_qty)), 0) AS TotalCost,
                    COALESCE(SUM({LineProfitExpression}), 0)      AS TotalProfit
             FROM invoice_items ii
             JOIN invoices i ON i.id = ii.invoice_id
             WHERE i.invoice_date_utc >= @startUtc AND i.invoice_date_utc < @endUtc
             GROUP BY ii.product_id
             ORDER BY TotalProfit DESC
             LIMIT @limit;
             """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc, limit });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<StockReportRow>> StockAsync(
        bool lowStockOnly,
        CancellationToken cancellationToken = default)
    {
        var filter = lowStockOnly
            ? "WHERE p.is_active = TRUE AND p.quantity_on_hand <= p.min_stock_threshold"
            : "WHERE p.is_active = TRUE";

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<StockReportRow>(
            $"""
             SELECT p.id   AS ProductId,
                    p.name AS ProductName,
                    c.name AS Category,
                    p.quantity_on_hand AS QuantityOnHand,
                    p.min_stock_threshold AS MinStockThreshold,
                    (p.quantity_on_hand <= p.min_stock_threshold) AS IsLowStock,
                    p.cost_price AS CostPrice,
                    ROUND(p.cost_price * p.quantity_on_hand, 2) AS StockValue
             FROM products p
             JOIN categories c ON c.id = p.category_id
             {filter}
             ORDER BY (p.quantity_on_hand <= p.min_stock_threshold) DESC, p.name;
             """);

        return rows.AsList();
    }

    public async Task<IReadOnlyList<ReceivableRow>> ReceivablesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ReceivableRow>(
            """
            SELECT id AS CustomerId, name AS CustomerName, mobile_number AS MobileNumber,
                   outstanding_balance AS OutstandingBalance
            FROM customers
            WHERE outstanding_balance <> 0
            ORDER BY outstanding_balance DESC;
            """);

        return rows.AsList();
    }

    public async Task<IReadOnlyList<PayableRow>> PayablesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PayableRow>(
            """
            SELECT id AS SupplierId, name AS SupplierName, contact_number AS ContactNumber,
                   payable_balance AS PayableBalance
            FROM suppliers
            WHERE payable_balance <> 0
            ORDER BY payable_balance DESC;
            """);

        return rows.AsList();
    }

    public async Task<(IReadOnlyList<StockMovementReportRow> Items, int TotalItems)> StockMovementsAsync(
        DateRangeUtc? range,
        string? reason,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = "WHERE 1 = 1";

        if (range is not null)
        {
            filter += " AND sm.created_at_utc >= @startUtc AND sm.created_at_utc < @endUtc";
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            filter += " AND sm.reason = @reason";
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT sm.id             AS Id,
                    sm.created_at_utc AS CreatedAtUtc,
                    p.name            AS ProductName,
                    sm.change_qty     AS ChangeQty,
                    sm.resulting_qty  AS ResultingQty,
                    sm.reason         AS Reason,
                    sm.reference_id   AS ReferenceId,
                    sm.note           AS Note,
                    u.full_name       AS UserName
             FROM stock_movements sm
             JOIN products p ON p.id = sm.product_id
             JOIN users u ON u.id = sm.user_id
             {filter}
             ORDER BY sm.created_at_utc DESC, sm.id DESC
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM stock_movements sm {filter};
             """,
            new
            {
                startUtc = range?.StartUtc,
                endUtc = range?.EndUtc,
                reason,
                limit = pageSize,
                offset = (page - 1) * pageSize,
            });

        var items = (await reader.ReadAsync<StockMovementReportRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }
}
