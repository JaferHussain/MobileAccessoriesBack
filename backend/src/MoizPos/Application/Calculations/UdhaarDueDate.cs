namespace MoizPos.Application.Calculations;

/// <summary>One ledger movement, on the shop-local day it happened.</summary>
public readonly record struct UdhaarMovement(DateOnly OnDate, decimal BillAmount, decimal PaidAmount);

/// <summary>When a debt is due, and how many whole months it has gone unpaid past that.</summary>
public readonly record struct UdhaarDue(DateOnly DueOn, int MonthsOverdue);

/// <summary>
/// When udhaar falls due — the owner's rule: <b>one month after the purchase</b>. Unpaid past that,
/// the date rolls forward a month at a time and the months are counted as overdue.
///
/// <para><b>The clock runs from the oldest purchase still unpaid</b>, with payments settling the
/// oldest debt first. Counting from the last payment instead would let a customer pay Rs 100 a
/// month and never be overdue on thousands.</para>
///
/// <para>Pure, so the rule can be argued about without a database in the way.</para>
/// </summary>
public static class UdhaarDueDate
{
    private const int MonthsToPay = 1;

    /// <summary>
    /// Due one month after <paramref name="boughtOn"/>; once that has passed, the next month-date
    /// on or after <paramref name="asOf"/>. The due day itself is not overdue.
    /// </summary>
    public static UdhaarDue For(DateOnly boughtOn, DateOnly asOf)
    {
        var monthsOverdue = 0;

        // Always counted from the purchase, never from the previous due date: AddMonths clamps
        // the 31st to a short month's end, and chaining would carry the 28th forward for good.
        while (boughtOn.AddMonths(MonthsToPay + monthsOverdue) < asOf)
        {
            monthsOverdue++;
        }

        return new UdhaarDue(boughtOn.AddMonths(MonthsToPay + monthsOverdue), monthsOverdue);
    }

    /// <summary>
    /// The day of the oldest purchase not yet fully paid, applying every payment to the oldest
    /// debt first. Null when nothing is owed.
    /// </summary>
    /// <param name="movements">The customer's ledger, in the order it was written.</param>
    public static DateOnly? OldestUnpaidPurchase(IEnumerable<UdhaarMovement> movements)
    {
        var ordered = movements.ToList();
        var paidSoFar = ordered.Sum(movement => movement.PaidAmount);

        foreach (var movement in ordered.Where(movement => movement.BillAmount > 0m))
        {
            if (paidSoFar < movement.BillAmount)
            {
                return movement.OnDate;
            }

            paidSoFar -= movement.BillAmount;
        }

        return null;
    }
}
