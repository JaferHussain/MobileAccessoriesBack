using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;

namespace MoizPos.Api.Controllers;

public sealed record CloseDayApiRequest
{
    public DateOnly ClosingDate { get; init; }

    public decimal OpeningFloat { get; init; }

    public decimal CountedCash { get; init; }

    /// <summary>Where "Rs 300 to the delivery boy, not entered" goes.</summary>
    public string? Note { get; init; }
}

public sealed class CloseDayValidator : AbstractValidator<CloseDayApiRequest>
{
    public CloseDayValidator()
    {
        RuleFor(x => x.OpeningFloat)
            .GreaterThanOrEqualTo(0).WithMessage("The opening float cannot be negative.");

        RuleFor(x => x.CountedCash)
            .GreaterThanOrEqualTo(0).WithMessage("The counted amount cannot be negative.");

        RuleFor(x => x.Note).MaximumLength(500);
    }
}

/// <summary>
/// Counting the drawer at the end of a trading day.
///
/// <para><b>Admin only.</b> This is the control OVER the salesman's handling of cash, so it
/// cannot be one the salesman performs — a short that the person responsible for it can close
/// away is not a control at all.</para>
/// </summary>
[ApiController]
[Route("api/day-closings")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class DayClosingsController : ControllerBase
{
    private readonly IDayClosingService _closings;

    public DayClosingsController(IDayClosingService closings) => _closings = closings;

    /// <summary>What the day took, before anyone counts it. Saves nothing.</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] DateOnly date,
        [FromQuery] decimal openingFloat = 0m,
        CancellationToken cancellationToken = default)
    {
        var preview = await _closings.PreviewAsync(date, openingFloat, cancellationToken);

        return Ok(ApiResponse<DayClosingDto>.Ok(preview));
    }

    [HttpPost]
    public async Task<IActionResult> Close(
        [FromBody] CloseDayApiRequest request,
        CancellationToken cancellationToken)
    {
        var closing = await _closings.CloseAsync(
            new CloseDayRequest
            {
                ClosingDate = request.ClosingDate,
                OpeningFloat = request.OpeningFloat,
                CountedCash = request.CountedCash,
                Note = request.Note,
            },
            CurrentUser.Id(User),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<DayClosingDto>.Ok(closing));
    }

    /// <summary>Recent closings — where a pattern of small shorts becomes visible.</summary>
    [HttpGet]
    public async Task<IActionResult> Recent(
        [FromQuery] int count = 30,
        CancellationToken cancellationToken = default)
    {
        var closings = await _closings.RecentAsync(Math.Clamp(count, 1, 200), cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<DayClosingDto>>.Ok(closings));
    }
}
