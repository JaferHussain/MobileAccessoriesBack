using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Errors;

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
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2)
        {
            throw new BusinessRuleViolationException("Type at least 2 letters to search.");
        }

        var rows = await _reads.FindReturnableLinesAsync(search, limit: 8, cancellationToken);

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
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<SaleReturnRow>.Normalize(page, pageSize);

        var (items, total) = await _reads.SearchSaleReturnsAsync(
            normalizedPage, normalizedSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<SaleReturnRow>>.Ok(
            new PagedResult<SaleReturnRow>(items, normalizedPage, normalizedSize, total)));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateSaleReturnRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _returns.RecordSaleReturnAsync(
            new RecordSaleReturnRequest
            {
                InvoiceId = request.InvoiceId,
                Reason = request.Reason,
                Items = request.Items
                    .Select(i => new SaleReturnLine { InvoiceItemId = i.InvoiceItemId, Quantity = i.Quantity })
                    .ToList(),
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
