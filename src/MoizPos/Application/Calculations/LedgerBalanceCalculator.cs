using MoizPos.Domain.Errors;

namespace MoizPos.Application.Calculations;

/// <summary>One movement in a customer's account: what was billed and what was paid.</summary>
public readonly record struct LedgerMovement(decimal BillAmount, decimal PaidAmount);

/// <summary>
/// Pure udhaar arithmetic. Each entry's balance is the previous balance plus the new bill less
/// the new payment (FR-020) — the rule the shopkeeper already keeps by hand in a register.
/// </summary>
public static class LedgerBalanceCalculator
{
    private const int MoneyScale = 2;

    /// <summary>
    /// The balance after one movement. A payment larger than the outstanding balance is refused
    /// unless <paramref name="confirmOverpayment"/> is set, so a balance never goes negative by
    /// accident (FR-022).
    /// </summary>
    public static decimal NextBalance(
        decimal previousBalance,
        decimal billAmount,
        decimal paidAmount,
        bool confirmOverpayment = false)
    {
        if (billAmount < 0m)
        {
            throw new BusinessRuleViolationException("Bill amount cannot be negative.");
        }

        if (paidAmount < 0m)
        {
            throw new BusinessRuleViolationException("Paid amount cannot be negative.");
        }

        var outstanding = Round(previousBalance + billAmount);
        var balance = Round(outstanding - paidAmount);

        if (balance < 0m && !confirmOverpayment)
        {
            throw new OverpaymentNotConfirmedException(paidAmount, outstanding);
        }

        return balance;
    }

    /// <summary>
    /// The running balance after each movement in order — what the ledger screen shows.
    /// </summary>
    public static IEnumerable<decimal> RunningBalances(
        decimal openingBalance,
        IReadOnlyList<LedgerMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);

        var balance = Round(openingBalance);

        foreach (var movement in movements)
        {
            balance = NextBalance(balance, movement.BillAmount, movement.PaidAmount);
            yield return balance;
        }
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, MoneyScale, MidpointRounding.AwayFromZero);
}
