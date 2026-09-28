using MoizPos.Domain.Errors;

namespace MoizPos.Application.Calculations;

/// <summary>
/// The arithmetic of carrying a customer's paper-register debt forward (FR-065 … FR-071).
///
/// <para>Pure and side-effect free so the one rule that really matters can be tested without a
/// database: recording an opening balance a second time is a <b>correction of the figure</b>,
/// never a second debt. Applying the requested amount instead of the difference would double what
/// the customer owes — an error nobody notices until the customer disputes it.</para>
/// </summary>
public static class OpeningBalanceRules
{
    /// <summary>Money is held to paisa everywhere else in this system; this is no exception.</summary>
    private const int Scale = 2;

    /// <summary>
    /// True when this customer already has a carried-forward figure, making the new one a
    /// correction. Note that a recorded <c>0.00</c> counts: "recorded as owing nothing" and
    /// "never been through the paper register" are different facts (data-model §1).
    /// </summary>
    public static bool IsCorrection(decimal? existing) => existing.HasValue;

    /// <summary>
    /// How much the customer's outstanding balance must move. The <b>difference</b>, never the
    /// requested amount — that is the whole of FR-070.
    /// </summary>
    public static decimal Delta(decimal? existing, decimal requested) =>
        Round(requested) - Round(existing ?? 0m);

    /// <summary>Refuses what the shop should never accept, before anything is written.</summary>
    public static void Validate(decimal amount, decimal? existing, string? reason)
    {
        if (amount < 0m)
        {
            // A negative figure would mean the shop owes the customer, which is a different
            // thing entirely and out of scope (FR-069).
            throw new BusinessRuleViolationException(
                "A carried-forward amount cannot be negative.");
        }

        // A first recording is a statement of fact copied from the register and needs no
        // justification. Changing a figure about money does (FR-071).
        if (IsCorrection(existing) && string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleViolationException(
                "Changing a carried-forward amount needs a reason.");
        }
    }

    /// <summary>The figure as it will be stored.</summary>
    public static decimal Normalize(decimal amount) => Round(amount);

    private static decimal Round(decimal value) =>
        Math.Round(value, Scale, MidpointRounding.AwayFromZero);
}
