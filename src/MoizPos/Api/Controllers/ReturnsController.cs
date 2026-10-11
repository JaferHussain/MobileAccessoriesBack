using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Errors;
using MoizPos.Domain.Enums;

namespace MoizPos.Api.Controllers;

public sealed record SaleReturnLineRequest
{
    public long InvoiceItemId { get; init; }

    public int Quantity { get; init; }
}

public sealed record CreateSaleReturnRequest
{
    public long InvoiceId { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<SaleReturnLineRequest> Items { get; init; } = [];

    /// <summary>How the refund was handed back. Required when the return refunds money.</summary>
    public PaymentMethod? RefundMethod { get; init; }

    /// <summary>The customer's account a transfer refund was sent to. Optional; refused for cash.</summary>
    public string? RefundAccountNumber { get; init; }

    /// <summary>The transfer's transaction ID. Optional; refused for cash.</summary>
    public string? RefundTransactionId { get; init; }

    /// <summary>The shop's own account a transfer refund was paid from. Optional; refused for cash.</summary>
    public long? RefundShopAccountId { get; init; }
}

public sealed record CreatePurchaseReturnRequest
{
    public long PurchaseId { get; init; }

    public int Quantity { get; init; }

    public string? Reason { get; init; }
}

public sealed class CreateSaleReturnValidator : AbstractValidator<CreateSaleReturnRequest>
{
    public CreateSaleReturnValidator()
    {
        RuleFor(x => x.InvoiceId).GreaterThan(0).WithMessage("An invoice is required.");

        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("Select at least one item to return.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.InvoiceItemId).GreaterThan(0);
            item.RuleFor(i => i.Quantity)
                .GreaterThan(0).WithMessage("Return quantity must be greater than zero.");
        });

        RuleFor(x => x.Reason).MaximumLength(255);

        // The columns' widths (0040); whether one is ALLOWED is the service's call.
        RuleFor(x => x.RefundAccountNumber).MaximumLength(50);
        RuleFor(x => x.RefundTransactionId).MaximumLength(64);
    }
}

public sealed class CreatePurchaseReturnValidator : AbstractValidator<CreatePurchaseReturnRequest>
{
    public CreatePurchaseReturnValidator()
    {
        RuleFor(x => x.PurchaseId).GreaterThan(0).WithMessage("A purchase is required.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Return quantity must be greater than zero.");

        RuleFor(x => x.Reason).MaximumLength(255);
    }
}

/// <summary>
/// Sale returns. Staff handle these at the counter — a customer bringing back a faulty charger
/// should not need the owner — and the response reveals no cost or profit (FR-040).
/// </summary>
[ApiController]
[Route("api/sale-returns")]
[Authorize]
public sealed class SaleReturnsController : ControllerBase
{
    private readonly IReturnService _returns;
    private readonly IReturnReadRepository _reads;

    public SaleReturnsController(IReturnService returns, IReturnReadRepository reads)
    {
        _returns = returns;
        _reads = reads;
    }

    /// <summary>
    /// "Return item": find a returnable sale by product name instead of an invoice number.
    /// </summary>
    [HttpGet("find")]
    public async Task<IActionResult> Find(
        [FromQuery] string search,
        [FromServices] IUserRepository users,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2)
        {
            throw new BusinessRuleViolationException("Type at least 2 letters to search.");
        }

        // A field salesman is offered only what he sold himself; everyone else, any sale.
        var rows = await _reads.FindReturnableLinesAsync(
            search, limit: 8, cancellationToken, await OwnSalesOnlyAsync(users, cancellationToken));

        // Mapped, not returned raw: the counter is sent the money already worked out, so the
        // screen and the return it records can never disagree.
        var lines = rows.Select(ReturnableSaleLine.From).ToList();

        return Ok(ApiResponse<IReadOnlyList<ReturnableSaleLine>>.Ok(lines));
    }

    /// <summary>Recent sale returns, one row per product — the general/detail list the Returns
    /// screen shows so a return is not just "recorded and forgotten".</summary>
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] SaleType? saleType = null,
        [FromQuery] string? search = null,
        [FromQuery] long? customerId = null,
        [FromServices] IUserRepository users = null!,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<SaleReturnRow>.Normalize(page, pageSize);

        var (items, total) = await _reads.SearchSaleReturnsAsync(
            normalizedPage, normalizedSize, cancellationToken, saleType, search, customerId,
            await OwnSalesOnlyAsync(users, cancellationToken));

        return Ok(ApiResponse<PagedResult<SaleReturnRow>>.Ok(
            new PagedResult<SaleReturnRow>(items, normalizedPage, normalizedSize, total)));
    }

    /// <summary>The caller's id when they are staff — they see only their own sales; null for the owner.</summary>
    private Task<long?> OwnSalesOnlyAsync(IUserRepository users, CancellationToken cancellationToken) =>
        Task.FromResult(CurrentUser.Role(User) == UserRole.Admin ? (long?)null : CurrentUser.Id(User));

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateSaleReturnRequest request,
        [FromServices] IUserRepository users,
        [FromServices] IShopAccountRepository shopAccounts,
        CancellationToken cancellationToken)
    {
        // The account must exist, be in use, and carry this kind of transfer — the same check an
        // expense and a supplier payment get. Whether an account is allowed at all (never for cash,
        // never when nothing went back) is the service's call.
        if (request.RefundShopAccountId is not null && request.RefundMethod is { } refundMethod)
        {
            await ShopAccountCheck.EnsureFitsAsync(shopAccounts, request.RefundShopAccountId, refundMethod, cancellationToken);
        }

        var result = await _returns.RecordSaleReturnAsync(
            new RecordSaleReturnRequest
            {
                InvoiceId = request.InvoiceId,
                Reason = request.Reason,
                Items = request.Items
                    .Select(i => new SaleReturnLine { InvoiceItemId = i.InvoiceItemId, Quantity = i.Quantity })
                    .ToList(),
                RefundMethod = request.RefundMethod,
                RefundAccountNumber = request.RefundAccountNumber,
                RefundTransactionId = request.RefundTransactionId,
                RefundShopAccountId = request.RefundShopAccountId,
                OwnSalesOnly = CurrentUser.Role(User) != UserRole.Admin,
                InField = (await users.FindByIdAsync(CurrentUser.Id(User), cancellationToken))?.Job == StaffJob.FieldSales,
            },
            CurrentUser.Id(User),
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created, ApiResponse<RecordSaleReturnResult>.Ok(result));
    }
}

/// <summary>
/// Purchase returns. Admin-only: this route exposes purchase cost and supplier payables.
/// </summary>
[ApiController]
[Route("api/purchase-returns")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class PurchaseReturnsController : ControllerBase
{
    private readonly IReturnService _returns;
    private readonly IReturnReadRepository _reads;

    public PurchaseReturnsController(IReturnService returns, IReturnReadRepository reads)
    {
        _returns = returns;
        _reads = reads;
    }

    /// <summary>
    /// Recent purchase returns, scoped to one supplier when given. Scoping is the everyday
    /// case: a return goes back to whichever supplier the goods came from, and "everything
    /// returned to everyone" is rarely what the owner is looking for.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] long? supplierId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<PurchaseReturnRow>.Normalize(page, pageSize);

        var (items, total) = await _reads.SearchPurchaseReturnsAsync(
            supplierId, normalizedPage, normalizedSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<PurchaseReturnRow>>.Ok(
            new PagedResult<PurchaseReturnRow>(items, normalizedPage, normalizedSize, total)));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreatePurchaseReturnRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _returns.RecordPurchaseReturnAsync(
            new RecordPurchaseReturnRequest
            {
                PurchaseId = request.PurchaseId,
                Quantity = request.Quantity,
                Reason = request.Reason,
            },
            CurrentUser.Id(User),
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created, ApiResponse<RecordPurchaseReturnResult>.Ok(result));
    }
}
