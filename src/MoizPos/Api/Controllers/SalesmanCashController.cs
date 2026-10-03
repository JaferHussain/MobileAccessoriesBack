using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Enums;

namespace MoizPos.Api.Controllers;

public sealed record SalesmanHandoverRequest
{
    public decimal Amount { get; init; }

    /// <summary>Cash joins the counter drawer at day close; a transfer went into a shop account.</summary>
    public PaymentMethod? PaymentMethod { get; init; }

    public string? Note { get; init; }
}

public sealed class SalesmanHandoverValidator : AbstractValidator<SalesmanHandoverRequest>
{
    public SalesmanHandoverValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("The amount handed over must be more than zero.");

        RuleFor(x => x.PaymentMethod)
            .NotNull().WithMessage("Say how he handed it over — cash into the drawer, or by transfer.")
            .Must(method => method is not (Domain.Enums.PaymentMethod.Credit or Domain.Enums.PaymentMethod.Partial))
            .WithMessage("Money is handed over in cash or by transfer.");

        RuleFor(x => x.Note).MaximumLength(255);
    }
}

/// <summary>
/// The cash a field salesman carries, and what he hands over. The owner sees and receives from
/// anyone; a salesman sees only his own (<c>/me</c>) and can never record a handover himself.
/// </summary>
[ApiController]
[Route("api/salesman-cash")]
[Authorize]
public sealed class SalesmanCashController : ControllerBase
{
    private readonly ISalesmanCashService _cash;

    public SalesmanCashController(ISalesmanCashService cash) => _cash = cash;

    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(ApiResponse<SalesmanCashStatement>.Ok(await _cash.StatementAsync(CurrentUser.Id(User), cancellationToken)));

    [HttpGet("{userId:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Statement(long userId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<SalesmanCashStatement>.Ok(await _cash.StatementAsync(userId, cancellationToken)));

    /// <summary>"Received from salesman" — recorded by the owner, never by the salesman himself.</summary>
    [HttpPost("{userId:long}/handovers")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Receive(long userId, [FromBody] SalesmanHandoverRequest request, CancellationToken cancellationToken)
    {
        var statement = await _cash.ReceiveAsync(
            userId, request.Amount, request.PaymentMethod!.Value, request.Note, CurrentUser.Id(User), cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<SalesmanCashStatement>.Ok(statement));
    }
}
