using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Time;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Controllers;

/// <summary>What the salesman's own dashboard shows about products.</summary>
public sealed record MyProducts
{
    /// <summary>What is in his bag now, at the shop's retail price.</summary>
    public IReadOnlyList<MyProductRow> Assigned { get; init; } = [];

    public int AssignedUnits { get; init; }

    public decimal AssignedValue { get; init; }

    /// <summary>What he sold in the period, product by product, net of returns.</summary>
    public IReadOnlyList<MySoldProductRow> Sold { get; init; } = [];

    public int SoldUnits { get; init; }

    public decimal SoldAmount { get; init; }
}

/// <summary>
/// The signed-in person's own products: what they carry, and what they sold. Always the caller's —
/// there is no user id to ask for. No cost and no wholesale price (FR-040): the price shown is the
/// shop's retail price. <c>from</c>/<c>to</c> are shop days, both inclusive; today when left out.
/// </summary>
[ApiController]
[Route("api/my-products")]
[Authorize]
public sealed class MyProductsController : ControllerBase
{
    private readonly IMyProductsRepository _products;
    private readonly PeriodResolver _periods;
    private readonly IClock _clock;

    public MyProductsController(IMyProductsRepository products, PeriodResolver periods, IClock clock)
    {
        _products = products;
        _periods = periods;
        _clock = clock;
    }

    [HttpGet]
    public async Task<IActionResult> Mine([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_periods.ToShopLocal(_clock.UtcNow));
        var first = from ?? to ?? today;
        var last = to ?? from ?? today;

        if (last < first)
        {
            throw new BusinessRuleViolationException("The end date cannot be before the start date.");
        }

        var range = _periods.ResolveLocalDateRange(first, last);
        var userId = CurrentUser.Id(User);

        var assigned = await _products.AssignedAsync(userId, cancellationToken);
        var sold = await _products.SoldAsync(userId, range.StartUtc, range.EndUtc, cancellationToken);

        return Ok(ApiResponse<MyProducts>.Ok(new MyProducts
        {
            Assigned = assigned,
            AssignedUnits = assigned.Sum(row => row.Quantity),
            AssignedValue = assigned.Sum(row => row.Value),
            Sold = sold,
            SoldUnits = sold.Sum(row => row.QuantitySold),
            SoldAmount = sold.Sum(row => row.Amount),
        }));
    }
}
