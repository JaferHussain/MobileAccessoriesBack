using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Enums;

namespace MoizPos.Api.Controllers;

/// <summary>
/// The dashboard. Admin-only in full: every figure here is either cost, profit, or something a
/// margin can be derived from (FR-035, FR-040).
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class DashboardController : ControllerBase
{
    private readonly IReportingService _reporting;

    public DashboardController(IReportingService reporting) => _reporting = reporting;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DashboardPeriod period = DashboardPeriod.Today,
        CancellationToken cancellationToken = default)
    {
        var dashboard = await _reporting.DashboardAsync(period, cancellationToken);

        return Ok(ApiResponse<DashboardDto>.Ok(dashboard));
    }
}

/// <summary>The full report set (FR-036). Admin-only throughout.</summary>
[ApiController]
[Route("api/reports")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportingService _reporting;

    public ReportsController(IReportingService reporting) => _reporting = reporting;

    /// <summary>Daily, weekly, monthly or annual sales (FR-036).</summary>
    [HttpGet("sales")]
    public async Task<IActionResult> Sales(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] ReportGrouping groupBy = ReportGrouping.Day,
        CancellationToken cancellationToken = default)
    {
        var rows = await _reporting.SalesAsync(from, to, groupBy, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<PeriodTotalsRow>>.Ok(rows));
    }

    /// <summary>
    /// The period's takings split into retail and wholesale (FR-014a) — the day-end figure the
    /// owner reads to see how much of the day was counter trade and how much was bulk.
    /// </summary>
    /// <summary>
    /// Each salesman's totals for the period. No cost and no profit — this answers "who took the
    /// money and who gave the discounts", which is what a short drawer needs alongside it.
    /// </summary>
    [HttpGet("sales-by-user")]
    public async Task<IActionResult> SalesByUser(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var rows = await _reporting.SalesByUserAsync(from, to, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<UserSalesRow>>.Ok(rows));
    }

    [HttpGet("sales-by-type")]
    public async Task<IActionResult> SalesByType(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var rows = await _reporting.SalesByTypeAsync(from, to, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<SaleTypeTotalsRow>>.Ok(rows));
    }

    /// <summary>
    /// The individual sales behind one of those totals: invoice, time, customer, the salesman who
    /// sold it, and the amount. Omit <paramref name="saleType"/> to list both.
    /// </summary>
    [HttpGet("sales-list")]
    public async Task<IActionResult> SalesList(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] SaleType? saleType = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _reporting.SalesListAsync(
            from, to, saleType, page, pageSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<SaleListRow>>.Ok(result));
    }

    /// <summary>Gross and net profit per bucket (FR-032, FR-033).</summary>
    [HttpGet("profit")]
    public async Task<IActionResult> Profit(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] ReportGrouping groupBy = ReportGrouping.Month,
        CancellationToken cancellationToken = default)
    {
        var rows = await _reporting.ProfitAsync(from, to, groupBy, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<ProfitReportRow>>.Ok(rows));
    }

    /// <summary>Product-wise profit: total sale, total cost, total profit (FR-033).</summary>
    [HttpGet("profit-by-product")]
    public async Task<IActionResult> ProfitByProduct(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var rows = await _reporting.ProfitByProductAsync(from, to, limit, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<ProductProfitRow>>.Ok(rows));
    }

    [HttpGet("purchases")]
    public async Task<IActionResult> Purchases(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var total = await _reporting.TotalPurchasesAsync(from, to, cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { from, to, total }));
    }

    /// <summary>Current stock, with value at the product's present cost.</summary>
    [HttpGet("stock")]
    public async Task<IActionResult> Stock(
        [FromQuery] bool lowStockOnly = false,
        CancellationToken cancellationToken = default)
    {
        var rows = await _reporting.StockAsync(lowStockOnly, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<StockReportRow>>.Ok(rows));
    }

    /// <summary>Full stock movement history with its reasons (FR-036).</summary>
    [HttpGet("stock-movements")]
    public async Task<IActionResult> StockMovements(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? reason,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var rows = await _reporting.StockMovementsAsync(
            from, to, reason, page, pageSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<StockMovementReportRow>>.Ok(rows));
    }

    /// <summary>Who owes the shop money.</summary>
    [HttpGet("receivables")]
    public async Task<IActionResult> Receivables(CancellationToken cancellationToken)
    {
        var rows = await _reporting.ReceivablesAsync(cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<ReceivableRow>>.Ok(rows));
    }

    /// <summary>Who the shop owes money to.</summary>
    [HttpGet("payables")]
    public async Task<IActionResult> Payables(CancellationToken cancellationToken)
    {
        var rows = await _reporting.PayablesAsync(cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<PayableRow>>.Ok(rows));
    }

    [HttpGet("expenses")]
    public async Task<IActionResult> Expenses(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var report = await _reporting.ExpensesAsync(from, to, cancellationToken);

        return Ok(ApiResponse<ExpenseReportDto>.Ok(report));
    }
}
