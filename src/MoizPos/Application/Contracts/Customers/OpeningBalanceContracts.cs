using FluentValidation;

namespace MoizPos.Application.Contracts.Customers;

/// <summary>
/// Records what a customer already owed before this software was in use (FR-065).
///
/// Sending this a second time is a <b>correction</b> of the figure, never an additional debt
/// (FR-070) — which is why <see cref="Reason"/> becomes mandatory once a figure exists.
/// </summary>
public sealed record SetOpeningBalanceRequest
{
    public decimal Amount { get; init; }

    /// <summary>
    /// Why this figure is being set. Required when correcting an existing carried-forward amount
    /// (FR-071); optional on the first recording, which is a statement of fact from the shop's
    /// paper register. The service enforces the conditional part, because only it knows whether
    /// a figure already exists.
    /// </summary>
    public string? Reason { get; init; }
}

public sealed record OpeningBalanceResult
{
    public long CustomerId { get; init; }

    /// <summary>The figure now in force.</summary>
    public decimal OpeningBalance { get; init; }

    /// <summary>Null on a first recording; the superseded figure on a correction.</summary>
    public decimal? PreviousOpeningBalance { get; init; }

    /// <summary>
    /// Everything this customer owes after the change — the carried-forward amount plus what has
    /// been sold on credit, less what has been paid and returned.
    /// </summary>
    public decimal OutstandingBalance { get; init; }

    public bool WasCorrection { get; init; }
}

public sealed class SetOpeningBalanceValidator : AbstractValidator<SetOpeningBalanceRequest>
{
    public SetOpeningBalanceValidator()
    {
        // The shop owing the customer is a different thing entirely and out of scope (FR-069).
        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(0)
            .WithMessage("A carried-forward amount cannot be negative.");

        RuleFor(x => x.Reason)
            .MaximumLength(255)
            .WithMessage("The reason cannot exceed 255 characters.");
    }
}
