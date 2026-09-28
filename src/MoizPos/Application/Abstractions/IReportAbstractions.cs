using MoizPos.Domain.Enums;
using MoizPos.Application.Time;

namespace MoizPos.Application.Abstractions;

public sealed record ExpenseCategory
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsActive { get; init; }
}

public sealed record ExpenseRow
{
    public long Id { get; init; }

    public long CategoryId { get; init; }

    public string CategoryName { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public DateTime ExpenseDateUtc { get; init; }

    /// <summary>
    /// Where the money came from. Null only on rows recorded before this was asked for; every
    /// expense saved since states one, and only Till leaves the cash drawer.
    /// </summary>
    public PaymentSource? PaymentSource { get; init; }

    public string? Note { get; init; }
}

public interface IExpenseRepository
{
    Task<IReadOnlyList<ExpenseCategory>> ListCategoriesAsync(CancellationToken cancellationToken = default);

    Task<long> CreateCategoryAsync(string name, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<ExpenseRow> Items, int TotalItems)> SearchAsync(
        long? categoryId, DateRangeUtc? range, int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<long> CreateAsync(
        long categoryId, decimal amount, DateTime expenseDateUtc, PaymentSource paymentSource,
        string? note, long userId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Total expenses in a period — the second half of net profit (FR-030).</summary>
    Task<decimal> SumForPeriodAsync(DateRangeUtc range, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(string Category, decimal Total)>> SumByCategoryAsync(
        DateRangeUtc range, CancellationToken cancellationToken = default);
}

/// <summary>One bucket of a rolled-up report.</summary>
public sealed record PeriodTotalsRow
{
    /// <summary>The bucket label in shop-local time, e.g. "2026-09-09" or "2026-09".</summary>
    public string Period { get; init; } = string.Empty;

    public decimal TotalSales { get; init; }

    public decimal GrossProfit { get; init; }

    public int InvoiceCount { get; init; }

    public int ItemsSold { get; init; }
}

public sealed record ProductProfitRow
{
    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int QuantitySold { get; init; }

    public decimal TotalSale { get; init; }

    public decimal TotalCost { get; init; }

    public decimal TotalProfit { get; init; }
}

public sealed record StockReportRow
{
    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public int QuantityOnHand { get; init; }

    public int MinStockThreshold { get; init; }

    public bool IsLowStock { get; init; }

    public decimal CostPrice { get; init; }

    public decimal StockValue { get; init; }
}

public sealed record ReceivableRow
{
    public long CustomerId { get; init; }

    public string CustomerName { get; init; } = string.Empty;

    public string? MobileNumber { get; init; }

    public decimal OutstandingBalance { get; init; }
}

public sealed record PayableRow
{
    public long SupplierId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public string? ContactNumber { get; init; }

    public decimal PayableBalance { get; init; }
}

public sealed record StockMovementReportRow
{
    public long Id { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int ChangeQty { get; init; }

    public int ResultingQty { get; init; }

    public string Reason { get; init; } = string.Empty;

    public long? ReferenceId { get; init; }

    public string? Note { get; init; }

    public string UserName { get; init; } = string.Empty;
}

/// <summary>
/// Read-only aggregations. This interface introduces no write path: reporting must never be able
/// to change what it reports on.
/// </summary>
/// <summary>
/// One side of the retail/wholesale split for a period (FR-014a).
/// </summary>
/// <summary>
/// One salesman's day.
///
/// <para>Pairs with the day's drawer count: a short is only answerable once you know who was
/// selling. Carries no cost and no profit - this is about accountability for cash and discounts,
/// not about margin.</para>
/// </summary>
public sealed record UserSalesRow
{
    public long UserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public int InvoiceCount { get; init; }

    /// <summary>Net of returns - what the sales are worth today.</summary>
    public decimal TotalSales { get; init; }

    /// <summary>What they actually took at the counter.</summary>
    public decimal CashTaken { get; init; }

    /// <summary>What they let leave on credit.</summary>
    public decimal CreditGiven { get; init; }

    /// <summary>Line discounts plus whole-bill discounts - what they gave away.</summary>
    public decimal DiscountGiven { get; init; }
}

public sealed record SaleTypeTotalsRow
{
    /// <summary>"Retail" or "Wholesale".</summary>
    public string SaleType { get; init; } = string.Empty;

    public decimal TotalSales { get; init; }

    public int InvoiceCount { get; init; }

    public int ItemsSold { get; init; }
}

/// <summary>
/// One sale in the drill-down behind a retail or wholesale total: what was sold, to whom, and
/// by which salesman.
/// </summary>
public sealed record SaleListRow
{
    public long InvoiceId { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateTime InvoiceDateUtc { get; init; }

    public string SaleType { get; init; } = string.Empty;

    public long? CustomerId { get; init; }

    /// <summary>Null for a fully paid walk-in, which the UI shows as "Walk-in".</summary>
    public string? CustomerName { get; init; }

    /// <summary>The salesman who rang the sale up — "sold by" in the drill-down.</summary>
    public string UserName { get; init; } = string.Empty;

    public decimal Total { get; init; }

    public decimal AmountPaid { get; init; }

    public decimal AmountRemaining { get; init; }

    public string PaymentMethod { get; init; } = string.Empty;

    public int ItemCount { get; init; }
}

/// <summary>
/// The period's credit, split by how it arose.
///
/// <para>Derived from the MONEY, never from <c>payment_method</c>: a sale labelled "Cash" whose
/// payment fell short is still credit, and the label is the one part a caller controls — the
/// same reasoning that decides credit authority in InvoiceService.</para>
/// </summary>
/// <param name="UdhaarCount">Sales that paid nothing and owe something.</param>
/// <param name="UdhaarAmount">What those sales left owing — their whole value.</param>
/// <param name="PartPaidCount">Sales that paid something and still owe something.</param>
/// <param name="PartPaidRemaining">Only the unpaid remainder of those sales.</param>
public readonly record struct CreditBreakdown(
    int UdhaarCount,
    decimal UdhaarAmount,
    int PartPaidCount,
    decimal PartPaidRemaining);

public interface IReportRepository
{
    /// <summary>
    /// Gross profit for a period, summed from what each sale line actually recorded:
    /// (sale price − cost snapshotted at sale) × units still sold, less the line discount.
    /// Returned units are excluded (FR-031, FR-027).
    /// </summary>
    Task<decimal> GrossProfitAsync(DateRangeUtc range, CancellationToken cancellationToken = default);

    Task<decimal> TotalSalesAsync(DateRangeUtc range, CancellationToken cancellationToken = default);

    Task<decimal> TotalPurchasesAsync(DateRangeUtc range, CancellationToken cancellationToken = default);

    /// <summary>
    /// Value returned by customers in the period. Already netted out of
    /// <see cref="TotalSalesAsync"/> via <c>invoices.net_amount</c> — this is a separate figure
    /// so the owner can see how much came back, not just the number it was already folded into.
    /// </summary>
    Task<decimal> TotalSaleReturnsAsync(DateRangeUtc range, CancellationToken cancellationToken = default);

    /// <summary>Value the shop sent back to suppliers in the period.</summary>
    Task<decimal> TotalPurchaseReturnsAsync(DateRangeUtc range, CancellationToken cancellationToken = default);

    /// <summary>
    /// How the period's credit broke down: sales taken wholly on udhaar, and what is still
    /// owed on sales that were part paid. Together they reconstruct <c>CreditSales</c> — this
    /// explains that figure rather than adding a second one beside it.
    /// </summary>
    Task<CreditBreakdown> CreditBreakdownAsync(
        DateRangeUtc range, CancellationToken cancellationToken = default);

    Task<(decimal CashSales, decimal CreditSales)> SalesBySettlementAsync(
        DateRangeUtc range, CancellationToken cancellationToken = default);

    Task<int> ItemsSoldAsync(DateRangeUtc range, CancellationToken cancellationToken = default);

    Task<decimal> TotalReceivablesAsync(CancellationToken cancellationToken = default);

    Task<decimal> TotalPayablesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PeriodTotalsRow>> SalesByPeriodAsync(
        DateRangeUtc range, ReportGroupingSql grouping, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductProfitRow>> ProfitByProductAsync(
        DateRangeUtc range, int limit, CancellationToken cancellationToken = default);

    /// <summary>The period's takings split into retail and wholesale (FR-014a).</summary>
    /// <summary>Each salesman's totals for the period, biggest seller first.</summary>
    Task<IReadOnlyList<UserSalesRow>> SalesByUserAsync(
        DateRangeUtc range, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaleTypeTotalsRow>> SalesBySaleTypeAsync(
        DateRangeUtc range, CancellationToken cancellationToken = default);

    /// <summary>
    /// The individual sales behind one of those totals. <paramref name="saleType"/> null means
    /// both, so the same query serves "the day's sales" and "the day's wholesale sales".
    /// </summary>
    Task<(IReadOnlyList<SaleListRow> Items, int TotalItems)> SalesListAsync(
        DateRangeUtc range, SaleType? saleType, int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockReportRow>> StockAsync(
        bool lowStockOnly, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReceivableRow>> ReceivablesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PayableRow>> PayablesAsync(CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<StockMovementReportRow> Items, int TotalItems)> StockMovementsAsync(
        DateRangeUtc? range, string? reason, int page, int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>How a report groups its buckets, expressed for the SQL layer.</summary>
public enum ReportGroupingSql
{
    Day = 1,
    Week = 2,
    Month = 3,
    Year = 4,
}
