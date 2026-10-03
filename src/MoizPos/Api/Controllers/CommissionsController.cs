using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Enums;

namespace MoizPos.Api.Controllers;

public sealed record CommissionPayoutRequest
{
    public decimal Amount { get; init; }

    /// <summary>Required: a cash payout comes out of the drawer at day close; a transfer does not.</summary>
    public PaymentMethod? PaymentMethod { get; init; }

    public string? Note { get; init; }
}

public sealed class CommissionPayoutValidator : AbstractValidator<CommissionPayoutRequest>
{
    public CommissionPayoutValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("The amount paid must be more than zero.");

        RuleFor(x => x.PaymentMethod)
            .NotNull().WithMessage("Say how the commission was paid — cash, bank transfer, JazzCash…")
            .Must(method => method is not (Domain.Enums.PaymentMethod.Credit or Domain.Enums.PaymentMethod.Partial))
            .WithMessage("Commission is paid in cash or by transfer.");

        RuleFor(x => x.Note).MaximumLength(255);
    }
}

/// <summary>
/// The field salesman's commission: his account, and what the owner has paid him.
///
/// <para>The owner sees and pays anyone's. A salesman sees only his own (<c>/me</c>) — it carries
/// his sale prices and the owner's price for each, never the cost, so the Staff protections hold.</para>
/// </summary>
[ApiController]
[Route("api/commissions")]
[Authorize]
public sealed class CommissionsController : ControllerBase
{
    private readonly ICommissionService _commission;

    public CommissionsController(ICommissionService commission) => _commission = commission;

    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(ApiResponse<CommissionStatement>.Ok(await _commission.StatementAsync(CurrentUser.Id(User), cancellationToken)));

    [HttpGet("{userId:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Statement(long userId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<CommissionStatement>.Ok(await _commission.StatementAsync(userId, cancellationToken)));

    [HttpPost("{userId:long}/payouts")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Pay(long userId, [FromBody] CommissionPayoutRequest request, CancellationToken cancellationToken)
    {
        var statement = await _commission.PayAsync(
            userId, request.Amount, request.PaymentMethod!.Value, request.Note, CurrentUser.Id(User), cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<CommissionStatement>.Ok(statement));
    }
}
