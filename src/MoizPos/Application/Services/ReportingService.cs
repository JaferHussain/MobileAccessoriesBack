using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>Everything the owner sees on opening the dashboard (FR-035).</summary>
public sealed record DashboardDto
{
    public DashboardPeriod Period { get; init; }

    public DateTime FromUtc { get; init; }

    public DateTime ToUtc { get; init; }

    public decimal TotalSales { get; init; }

    public decimal TotalPurchases { get; init; }

    /// <summary>
    /// Value returned by customers this period. Already netted into <see cref="TotalSales"/>
    /// via <c>invoices.net_amount</c> — shown again on its own so the owner can see how much
    /// came back, not just the smaller sales figure it produced.
    /// </summary>
    public decimal TotalSaleReturns { get; init; }

    /// <summary>Value sent back to suppliers this period, already netted into <see cref="TotalPurchases"/>.</summary>
    public decimal TotalPurchaseReturns { get; init; }

    public decimal GrossProfit { get; init; }

    public decimal TotalExpenses { get; init; }

    public decimal NetProfit { get; init; }

    public decimal CashSales { get; init; }

    public decimal CreditSales { get; init; }

    /// <summary>
    /// How <see cref="CreditSales"/> was made up. These are VISIBILITY, not new money: every
    /// rupee here is already inside <see cref="CreditSales"/> and <see cref="TotalReceivables"/>.
    /// Adding them to either would count the same debt twice.
    /// </summary>
    public int UdhaarSalesCount { get; init; }

    /// <summary>The value of sales taken wholly on credit — nothing was paid at the counter.</summary>
    public decimal UdhaarSalesAmount { get; init; }

    public int PartPaidSalesCount { get; init; }

    /// <summary>Only the unpaid remainder of part-paid sales; what came in is already in
    /// <see cref="CashSales"/>.</summary>
    public decimal PartPaidRemaining { get; init; }

    public decimal TotalReceivables { get; init; }

    public decimal TotalPayables { get; init; }

    public int ItemsSoldCount { get; init; }

    /// <summary>
    /// The period's takings split into retail and wholesale. Always both rows, zero included,
    /// so a day with no wholesale sales reads as "Wholesale 0" rather than going missing.
    /// </summary>
    public IReadOnlyList<SaleTypeTotalsRow> SalesByType { get; init; } = [];

    public IReadOnlyList<StockReportRow> LowStockProducts { get; init; } = [];
}

/// <summary>A profit rollup: gross, expenses and net for each bucket in the range.</summary>
public sealed record ProfitReportRow
{
    public string Period { get; init; } = string.Empty;

    public decimal TotalSales { get; init; }

    public decimal GrossProfit { get; init; }

    public decimal Expenses { get; init; }

    public decimal NetProfit { get; init; }
}

public sealed record ExpenseReportDto
{
    public decimal Total { get; init; }

    public IReadOnlyList<ExpenseCategoryTotal> ByCategory { get; init; } = [];
}

public sealed record ExpenseCategoryTotal(string Category, decimal Total);

public interface IReportingService
{
    Task<DashboardDto> DashboardAsync(
        DashboardPeriod period, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PeriodTotalsRow>> SalesAsync(
        DateOnly from, DateOnly to, ReportGrouping grouping, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProfitReportRow>> ProfitAsync(
        DateOnly from, DateOnly to, ReportGrouping grouping, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductProfitRow>> ProfitByProductAsync(
        DateOnly from, DateOnly to, int limit, CancellationToken cancellationToken = default);

    Task<decimal> TotalPurchasesAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockReportRow>> StockAsync(
        bool lowStockOnly, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReceivableRow>> ReceivablesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PayableRow>> PayablesAsync(CancellationToken cancellationToken = default);

    Task<ExpenseReportDto> ExpensesAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<PagedResult<StockMovementReportRow>> StockMovementsAsync(
        DateOnly? from, DateOnly? to, string? reason, int page, int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>The retail/wholesale split for a date range (FR-014a).</summary>
    /// <summary>Each salesman's totals for the period — who took the money, who gave the
    /// discounts. Pairs with the day's drawer count, which asks whose shift was short.</summary>
    Task<IReadOnlyList<UserSalesRow>> SalesByUserAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaleTypeTotalsRow>> SalesByTypeAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// The individual sales behind a retail or wholesale total — the drill-down. A null
    /// <paramref name="saleType"/> lists both.
    /// </summary>
    Task<PagedResult<SaleListRow>> SalesListAsync(
        DateOnly from, DateOnly to, SaleType? saleType, int page, int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The reporting layer.
///
/// <para>Read-only by construction: it composes the report repository and the expense repository
/// and introduces no write path of its own, so running a report can never alter what it
/// reports.</para>
///
/// <para>Net profit is gross profit less the expenses recorded in the same period (FR-032), with
/// both sides resolved against shop-local period boundaries so a sale and an expense on the same
/// evening land in the same bucket (FR-034).</para>
/// </summary>
public sealed class ReportingService : IReportingService
{
    /// <summary>Enough rows for a shop's catalogue without an unbounded query.</summary>
    private const int ProductProfitLimit = 500;

    private readonly IReportRepository _reports;
    private readonly IExpenseRepository _expenses;
    private readonly PeriodResolver _periods;
    private readonly IClock _clock;

    public ReportingService(
        IReportRepository reports,
        IExpenseRepository expenses,
        PeriodResolver periods,
        IClock clock)
    {
        _reports = reports;
        _expenses = expenses;
        _periods = periods;
        _clock = clock;
    }

    public async Task<DashboardDto> DashboardAsync(
        DashboardPeriod period,
        CancellationToken cancellationToken = default)
    {
        var range = _periods.Resolve(period, _clock.UtcNow);

        var totalSales = await _reports.TotalSalesAsync(range, cancellationToken);
        var totalPurchases = await _reports.TotalPurchasesAsync(range, cancellationToken);
        var saleReturns = await _reports.TotalSaleReturnsAsync(range, cancellationToken);
        var purchaseReturns = await _reports.TotalPurchaseReturnsAsync(range, cancellationToken);
        var grossProfit = await _reports.GrossProfitAsync(range, cancellationToken);
        var expenses = await _expenses.SumForPeriodAsync(range, cancellationToken);
        var (cashSales, creditSales) = await _reports.SalesBySettlementAsync(range, cancellationToken);
        var credit = await _reports.CreditBreakdownAsync(range, cancellationToken);
        var itemsSold = await _reports.ItemsSoldAsync(range, cancellationToken);
        var receivables = await _reports.TotalReceivablesAsync(cancellationToken);
        var payables = await _reports.TotalPayablesAsync(cancellationToken);
        var lowStock = await _reports.StockAsync(lowStockOnly: true, cancellationToken);
        var salesByType = await _reports.SalesBySaleTypeAsync(range, cancellationToken);

        return new DashboardDto
        {
            Period = period,
            FromUtc = range.StartUtc,
            ToUtc = range.EndUtc,
            TotalSales = totalSales,
            TotalPurchases = totalPurchases,
            TotalSaleReturns = saleReturns,
            TotalPurchaseReturns = purchaseReturns,
            GrossProfit = grossProfit,
            TotalExpenses = expenses,
            NetProfit = ProfitCalculator.NetProfit(grossProfit, expenses),
            CashSales = cashSales,
            CreditSales = creditSales,
            UdhaarSalesCount = credit.UdhaarCount,
            UdhaarSalesAmount = credit.UdhaarAmount,
            PartPaidSalesCount = credit.PartPaidCount,
            PartPaidRemaining = credit.PartPaidRemaining,
            TotalReceivables = receivables,
            TotalPayables = payables,
            ItemsSoldCount = itemsSold,
            SalesByType = salesByType,
            LowStockProducts = lowStock,
        };
    }

    public Task<IReadOnlyList<PeriodTotalsRow>> SalesAsync(
        DateOnly from,
        DateOnly to,
        ReportGrouping grouping,
        CancellationToken cancellationToken = default) =>
        _reports.SalesByPeriodAsync(Range(from, to), Map(grouping), cancellationToken);

    public async Task<IReadOnlyList<ProfitReportRow>> ProfitAsync(
        DateOnly from,
        DateOnly to,
        ReportGrouping grouping,
        CancellationToken cancellationToken = default)
    {
        var buckets = await _reports.SalesByPeriodAsync(
            Range(from, to), Map(grouping), cancellationToken);

        var rows = new List<ProfitReportRow>(buckets.Count);

        foreach (var bucket in buckets)
        {
            // Expenses are summed for the same bucket, so net profit reconciles bucket by bucket
            // rather than only across the whole range.
            var bucketRange = BucketRange(bucket.Period, grouping);

            var expenses = bucketRange is null
                ? 0m
                : await _expenses.SumForPeriodAsync(bucketRange.Value, cancellationToken);

            rows.Add(new ProfitReportRow
            {
                Period = bucket.Period,
                TotalSales = bucket.TotalSales,
                GrossProfit = bucket.GrossProfit,
                Expenses = expenses,
                NetProfit = ProfitCalculator.NetProfit(bucket.GrossProfit, expenses),
            });
        }

        return rows;
    }

    public Task<IReadOnlyList<ProductProfitRow>> ProfitByProductAsync(
        DateOnly from,
        DateOnly to,
        int limit,
        CancellationToken cancellationToken = default) =>
        _reports.ProfitByProductAsync(
            Range(from, to), Math.Clamp(limit, 1, ProductProfitLimit), cancellationToken);

    public Task<decimal> TotalPurchasesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        _reports.TotalPurchasesAsync(Range(from, to), cancellationToken);

    public Task<IReadOnlyList<StockReportRow>> StockAsync(
        bool lowStockOnly,
        CancellationToken cancellationToken = default) =>
        _reports.StockAsync(lowStockOnly, cancellationToken);

    public Task<IReadOnlyList<ReceivableRow>> ReceivablesAsync(
        CancellationToken cancellationToken = default) =>
        _reports.ReceivablesAsync(cancellationToken);

    public Task<IReadOnlyList<PayableRow>> PayablesAsync(
        CancellationToken cancellationToken = default) =>
        _reports.PayablesAsync(cancellationToken);

    public async Task<ExpenseReportDto> ExpensesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var range = Range(from, to);

        var total = await _expenses.SumForPeriodAsync(range, cancellationToken);
        var byCategory = await _expenses.SumByCategoryAsync(range, cancellationToken);

        return new ExpenseReportDto
        {
            Total = total,
            ByCategory = byCategory
                .Select(row => new ExpenseCategoryTotal(row.Category, row.Total))
                .ToList(),
        };
    }

    public async Task<PagedResult<StockMovementReportRow>> StockMovementsAsync(
        DateOnly? from,
        DateOnly? to,
        string? reason,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) =
            PagedResult<StockMovementReportRow>.Normalize(page, pageSize);

        DateRangeUtc? range = from is not null && to is not null ? Range(from.Value, to.Value) : null;

        var (items, total) = await _reports.StockMovementsAsync(
            range, reason, normalizedPage, normalizedSize, cancellationToken);

        return new PagedResult<StockMovementReportRow>(items, normalizedPage, normalizedSize, total);
    }

    public Task<IReadOnlyList<UserSalesRow>> SalesByUserAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        _reports.SalesByUserAsync(_periods.ResolveLocalDateRange(from, to), cancellationToken);

    public Task<IReadOnlyList<SaleTypeTotalsRow>> SalesByTypeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        _reports.SalesBySaleTypeAsync(Range(from, to), cancellationToken);

    public async Task<PagedResult<SaleListRow>> SalesListAsync(
        DateOnly from,
        DateOnly to,
        SaleType? saleType,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<SaleListRow>.Normalize(page, pageSize);

        var (items, total) = await _reports.SalesListAsync(
            Range(from, to), saleType, normalizedPage, normalizedSize, cancellationToken);

        return new PagedResult<SaleListRow>(items, normalizedPage, normalizedSize, total);
    }

    private DateRangeUtc Range(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new BusinessRuleViolationException(
                "The end of the date range cannot be before its start.");
        }

        return _periods.ResolveLocalDateRange(from, to);
    }

    private static ReportGroupingSql Map(ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.Day => ReportGroupingSql.Day,
        ReportGrouping.Week => ReportGroupingSql.Week,
        ReportGrouping.Month => ReportGroupingSql.Month,
        _ => ReportGroupingSql.Year,
    };

    /// <summary>
    /// Turns a bucket label back into the local date range it covers, so expenses can be summed
    /// for exactly that bucket. Week labels are skipped — an ISO week label does not map back to
    /// dates without a calendar, and the week rollup reports gross profit only.
    /// </summary>
    private DateRangeUtc? BucketRange(string period, ReportGrouping grouping)
    {
        switch (grouping)
        {
            case ReportGrouping.Day when DateOnly.TryParse(period, out var day):
                return _periods.ResolveLocalDateRange(day, day);

            case ReportGrouping.Month when DateOnly.TryParse($"{period}-01", out var monthStart):
                return _periods.ResolveLocalDateRange(
                    monthStart, monthStart.AddMonths(1).AddDays(-1));

            case ReportGrouping.Year when int.TryParse(period, out var year):
                return _periods.ResolveLocalDateRange(
                    new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

            default:
                return null;
        }
    }
}
