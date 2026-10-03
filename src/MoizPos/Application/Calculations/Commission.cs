using MoizPos.Domain.Enums;

namespace MoizPos.Application.Calculations;

/// <summary>A debt a customer owes: a sale (by its invoice id) or an amount brought forward.</summary>
/// <param name="PaidAtSale">What was paid at the counter — it settles this debt first.</param>
public readonly record struct CommissionDebt(long Id, DateOnly OnDate, decimal Amount, decimal PaidAtSale);

/// <summary>Money a customer paid later, against whatever they owed.</summary>
public readonly record struct CommissionCredit(DateOnly OnDate, decimal Amount);

/// <summary>How much of a debt has been paid, and the day it was paid in full (if it has been).</summary>
public readonly record struct DebtSettlement(decimal Share, DateOnly? SettledOn);

/// <summary>
/// The salesman's commission — the owner's rule: <b>half of whatever he sells above the owner's
/// price, earned once the customer has paid for it</b>.
///
/// <para>Pure, so every part of the rule is tested without a database: the owner's price, the
/// commission on a line, and when it is earned.</para>
/// </summary>
public static class Commission
{
    /// <summary>The owner's rule: half the extra goes to the salesman.</summary>
    public const decimal DefaultRatePercent = 50m;

    private const int MoneyScale = 2;

    /// <summary>
    /// The owner's price for the sale being made — what the counter already quotes: the retail
    /// price, or for a wholesale sale the wholesale price, falling back to retail where none is set.
    /// </summary>
    public static decimal BasePrice(SaleType saleType, decimal retailPrice, decimal wholesalePrice) =>
        saleType == SaleType.Wholesale && wholesalePrice > 0m ? wholesalePrice : retailPrice;

    /// <summary>A salesman may not sell below the owner's price — the owner's rule, not a discount he can give.</summary>
    public static bool IsBelowBase(decimal effectiveUnitPrice, decimal basePrice) => effectiveUnitPrice < basePrice;

    /// <summary>
    /// The commission on one line: the rate times what each unit fetched above the owner's price,
    /// for every unit still sold (a returned unit earns nothing).
    /// </summary>
    /// <param name="effectiveUnitPrice">What one unit really fetched — after its line discount and its share of the whole-bill discount.</param>
    public static decimal ForLine(decimal effectiveUnitPrice, decimal basePrice, int unitsKept, decimal ratePercent)
    {
        var extraPerUnit = Math.Max(0m, effectiveUnitPrice - basePrice);

        return Round(extraPerUnit * unitsKept * ratePercent / 100m);
    }

    /// <summary>The part of a commission already earned: its share of the sale that has been paid.</summary>
    public static decimal Earned(decimal commission, decimal share) => Round(commission * Math.Clamp(share, 0m, 1m));

    /// <summary>
    /// How much of each debt has been paid. What was paid at the counter settles that sale first;
    /// every later payment settles the <b>oldest</b> debt first — the same order the udhaar
    /// due date uses — so a customer paying a little never leaves an old sale forever unpaid.
    /// </summary>
    /// <returns>Keyed by debt id.</returns>
    public static IReadOnlyDictionary<long, DebtSettlement> Settle(
        IEnumerable<CommissionDebt> debts, IEnumerable<CommissionCredit> credits)
    {
        var ordered = debts.OrderBy(debt => debt.OnDate).ThenBy(debt => debt.Id).ToList();
        var outstanding = new Dictionary<long, decimal>();
        var settledOn = new Dictionary<long, DateOnly?>();

        foreach (var debt in ordered)
        {
            var remaining = Math.Max(0m, debt.Amount - Math.Max(0m, debt.PaidAtSale));
            outstanding[debt.Id] = remaining;
            settledOn[debt.Id] = remaining == 0m ? debt.OnDate : null;
        }

        foreach (var credit in credits.OrderBy(credit => credit.OnDate))
        {
            var left = credit.Amount;

            foreach (var debt in ordered)
            {
                if (left <= 0m)
                {
                    break;
                }

                var owed = outstanding[debt.Id];

                if (owed <= 0m)
                {
                    continue;
                }

                var applied = Math.Min(owed, left);
                outstanding[debt.Id] = owed - applied;
                left -= applied;

                if (outstanding[debt.Id] == 0m)
                {
                    // Paid in full by this payment — the commission on it is earned today.
                    settledOn[debt.Id] = credit.OnDate;
                }
            }
        }

        return ordered.ToDictionary(
            debt => debt.Id,
            debt => new DebtSettlement(
                debt.Amount <= 0m ? 1m : Math.Round((debt.Amount - outstanding[debt.Id]) / debt.Amount, 4),
                settledOn[debt.Id]));
    }

    private static decimal Round(decimal value) => Math.Round(value, MoneyScale, MidpointRounding.AwayFromZero);
}
