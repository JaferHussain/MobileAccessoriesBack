using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;

namespace MoizPos.Api.Controllers;

public sealed record SalesmanStockItemRequest
{
    public long ProductId { get; init; }

    public int Quantity { get; init; }
}

public sealed record SalesmanStockMoveRequest
{
    public IReadOnlyList<SalesmanStockItemRequest> Items { get; init; } = [];

    public string? Note { get; init; }
}

public sealed class SalesmanStockMoveValidator : AbstractValidator<SalesmanStockMoveRequest>
{
    public SalesmanStockMoveValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Choose at least one product.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).GreaterThan(0);
            item.RuleFor(i => i.Quantity).GreaterThan(0).WithMessage("Each quantity must be more than zero.");
        });
        RuleFor(x => x.Note).MaximumLength(255);
    }
}

/// <summary>
/// The stock a field salesman carries. The owner issues goods to him and takes them back, and sees
/// anyone's bag; a salesman sees only his own (<c>/me</c>) and never moves stock himself — his
/// sales and the returns customers hand him move it for him.
/// </summary>
[ApiController]
[Route("api/salesman-stock")]
[Authorize]
public sealed class SalesmanStockController : ControllerBase
{
    private readonly ISalesmanStockService _stock;

    public SalesmanStockController(ISalesmanStockService stock) => _stock = stock;

    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(ApiResponse<SalesmanStockStatement>.Ok(await _stock.StatementAsync(CurrentUser.Id(User), cancellationToken)));

    [HttpGet("{userId:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Statement(long userId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<SalesmanStockStatement>.Ok(await _stock.StatementAsync(userId, cancellationToken)));

    [HttpPost("{userId:long}/issue")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Issue(long userId, [FromBody] SalesmanStockMoveRequest request, CancellationToken cancellationToken)
    {
        var statement = await _stock.IssueAsync(userId, Lines(request), request.Note, CurrentUser.Id(User), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<SalesmanStockStatement>.Ok(statement));
    }

    /// <summary>Goods he brings back to the shop.</summary>
    [HttpPost("{userId:long}/return")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Return(long userId, [FromBody] SalesmanStockMoveRequest request, CancellationToken cancellationToken)
    {
        var statement = await _stock.ReturnAsync(userId, Lines(request), request.Note, CurrentUser.Id(User), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<SalesmanStockStatement>.Ok(statement));
    }

    private static List<SalesmanStockLine> Lines(SalesmanStockMoveRequest request) =>
        request.Items.Select(item => new SalesmanStockLine(item.ProductId, item.Quantity)).ToList();
}
