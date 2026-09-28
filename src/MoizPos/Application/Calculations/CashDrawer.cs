using MoizPos.Domain.Errors;

namespace MoizPos.Application.Calculations;

/// <summary>
/// The drawer at the end of a trading day: what the day says should be in it, what was counted,
/// and the difference between them.
/// </summary>
/// <param name="Expected">Float, plus cash taken in, less cash paid out.</param>
/// <param name="Counted">What the shopkeeper physically counted.</param>
/// <param name="Difference">
/// Counted less expected. Negative is <b>short</b> — money the day accounts for that is not
/// there. Positive is over, which is not good news either: it usually means a sale went
/// unrecorded or change was given wrong.
/// </param>
public readonly record struct CashDrawerCount(
    decimal Expected,
    decimal Counted,
    decimal Difference)
{
    public bool IsBalanced => Difference == 0m;

    public bool IsShort => Difference < 0m;
}

/// <summary>
/// Reconciling physical cash against the day's record.
///
/// <para><b>Why this exists.</b> Every other figure in this system reconciles against itself. A
/// salesman can take a cash sale, hand over the goods, record it perfectly, and pocket the notes —
/// and no report will ever disagree, because the sale WAS recorded correctly. Counting the drawer
/// against what the day took is the only thing that makes that visible.</para>
///
/// <para><b>Nothing is rounded.</b> A paisa short is still short. Smoothing small differences away
/// would hide precisely the small, repeated ones worth noticing — a rupee here every day is a
/// pattern, and a pattern is the point.</para>
///
/// <para>Pure: no clock, no database. The figures are gathered by the service and handed in.</para>
/// </summary>
public static class CashDrawer
{
    public static CashDrawerCount Reconcile(
        decimal openingFloat,
        decimal cashSales,
        decimal cashRecovery,
        decimal cashRefunds,
        decimal cashPaidOut,
        decimal cashToSuppliers,
        decimal counted)
    {
        EnsureNotNegative(openingFloat, "The opening float");
        EnsureNotNegative(cashSales, "Cash sales");
        EnsureNotNegative(cashRecovery, "Cash recovered against udhaar");
        EnsureNotNegative(cashRefunds, "Cash refunded");
        EnsureNotNegative(cashPaidOut, "Cash paid out");
        EnsureNotNegative(cashToSuppliers, "Cash paid to suppliers");
        EnsureNotNegative(counted, "The counted amount");

        // Two ways notes leave the drawer: an expense taken from the till, and a supplier bill
        // settled in cash. The second moves the largest amounts, and leaving it out reported a
        // short for money that had been paid out perfectly legitimately.
        var expected =
            openingFloat + cashSales + cashRecovery - cashRefunds - cashPaidOut - cashToSuppliers;

        return new CashDrawerCount(expected, counted, counted - expected);
    }

    private static void EnsureNotNegative(decimal value, string what)
    {
        if (value < 0m)
        {
            throw new BusinessRuleViolationException($"{what} cannot be negative.");
        }
    }
}
