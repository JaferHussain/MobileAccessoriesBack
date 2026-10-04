using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Controllers;

/// <summary>A bill as the screen shows it: what it was, what was paid, and what is still owed.</summary>
public sealed record PurchaseBillSummary(
    long Id,
    long SupplierId,
    string SupplierName,
    string? BillNumber,
    DateOnly BillDate,
    decimal Total,
    decimal Paid,
    decimal Returned,
    decimal Due,
    /// <summary>Paid, PartPaid or Unpaid.</summary>
    string Status,
    int ItemCount,
    bool HasBillImage,
    string? Note,
    string RecordedBy);

public sealed record PurchaseBillPaymentDto(long Id, decimal Amount, string PaymentMethod, DateOnly PaidOn, string? Note, bool HasProof);

public sealed record PurchaseBillDetail(
    long Id,
    long SupplierId,
    string SupplierName,
    string? BillNumber,
    DateOnly BillDate,
    decimal Total,
    decimal Paid,
    decimal Returned,
    decimal Due,
    string Status,
    int ItemCount,
    bool HasBillImage,
    string? Note,
    string RecordedBy,
    IReadOnlyList<PurchaseBillLineRow> Lines,
    IReadOnlyList<PurchaseBillPaymentDto> Payments);

/// <summary>
/// Purchase bills — a supplier's bill with all its products, and the payment made with it or
/// against it later. Owner only: a bill is purchase cost, which a salesman never sees (FR-040).
/// </summary>
[ApiController]
[Route("api/purchase-bills")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class PurchaseBillsController : ControllerBase
{
    private readonly IPurchaseBillService _bills;
    private readonly IPurchaseBillRepository _read;
    private readonly IShopAccountRepository _accounts;
    private readonly PeriodResolver _periods;

    public PurchaseBillsController(
        IPurchaseBillService bills, IPurchaseBillRepository read, IShopAccountRepository accounts, PeriodResolver periods)
    {
        _bills = bills;
        _read = read;
        _accounts = accounts;
        _periods = periods;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] long? supplierId, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var rows = await _read.ListAsync(supplierId, Math.Clamp(limit, 1, 200), cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<PurchaseBillSummary>>.Ok(rows.Select(Summarise).ToList()));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var row = await _read.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Purchase bill", id);
        var summary = Summarise(row);
        var lines = await _read.LinesAsync(id, cancellationToken);
        var payments = (await _read.PaymentsAsync(id, cancellationToken))
            .Select(payment => new PurchaseBillPaymentDto(
                payment.Id, payment.Amount, payment.PaymentMethod,
                DateOnly.FromDateTime(_periods.ToShopLocal(payment.PaymentDateUtc)), payment.Note, payment.HasProof))
            .ToList();

        return Ok(ApiResponse<PurchaseBillDetail>.Ok(new PurchaseBillDetail(
            summary.Id, summary.SupplierId, summary.SupplierName, summary.BillNumber, summary.BillDate, summary.Total,
            summary.Paid, summary.Returned, summary.Due, summary.Status, summary.ItemCount, summary.HasBillImage,
            summary.Note, summary.RecordedBy, lines, payments)));
    }

    /// <summary>
    /// Records a supplier's bill: every line into stock, the payable up by its total, then — if paid
    /// there and then — the payment. One transaction.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Record([FromBody] RecordPurchaseBillRequest request, CancellationToken cancellationToken)
    {
        if (request.Payment is { } payment)
        {
            await ShopAccountCheck.EnsureFitsAsync(_accounts, payment.ShopAccountId, payment.PaymentMethod, cancellationToken);
        }

        var result = await _bills.RecordAsync(request, CurrentUser.Id(User), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<RecordPurchaseBillResult>.Ok(result));
    }

    /// <summary>Pays a bill later — never more than it still owes.</summary>
    [HttpPost("{id:long}/payments")]
    public async Task<IActionResult> Pay(long id, [FromBody] PurchaseBillPayment payment, CancellationToken cancellationToken)
    {
        await ShopAccountCheck.EnsureFitsAsync(_accounts, payment.ShopAccountId, payment.PaymentMethod, cancellationToken);

        var result = await _bills.PayAsync(id, payment, CurrentUser.Id(User), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<PayPurchaseBillResult>.Ok(result));
    }

    private static PurchaseBillSummary Summarise(PurchaseBillRow row)
    {
        var due = Math.Max(0m, Math.Round(row.Total - row.Returned - row.Paid, 2, MidpointRounding.AwayFromZero));
        var status = due <= 0m ? "Paid" : row.Paid > 0m ? "PartPaid" : "Unpaid";

        return new PurchaseBillSummary(
            row.Id, row.SupplierId, row.SupplierName, row.BillNumber, row.BillDate, row.Total, row.Paid, row.Returned,
            due, status, row.ItemCount, row.HasBillImage, row.Note, row.RecordedBy);
    }
}
