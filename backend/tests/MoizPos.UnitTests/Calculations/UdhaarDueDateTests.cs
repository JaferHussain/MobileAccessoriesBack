using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// When udhaar falls due: one month after the purchase that is still unpaid, rolled forward a
/// month at a time — and counted as overdue — once that date has passed.
/// </summary>
public sealed class UdhaarDueDateTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // ================================================================
    //  The due date
    // ================================================================

    [Fact]
    public void Bought_on_the_first_of_september_is_due_on_the_first_of_october()
    {
        // The owner's own example.
        var due = UdhaarDueDate.For(boughtOn: D(2026, 9, 1), asOf: D(2026, 9, 1));

        due.DueOn.Should().Be(D(2026, 10, 1));
        due.MonthsOverdue.Should().Be(0);
    }

    [Fact]
    public void Unpaid_through_october_rolls_to_november_and_says_one_month_overdue()
    {
        // The owner's own example: not paid in October, so the message asks for 1 November.
        var due = UdhaarDueDate.For(boughtOn: D(2026, 9, 1), asOf: D(2026, 10, 15));

        due.DueOn.Should().Be(D(2026, 11, 1));
        due.MonthsOverdue.Should().Be(1);
    }

    [Fact]
    public void The_due_day_itself_is_not_yet_overdue()
    {
        var due = UdhaarDueDate.For(boughtOn: D(2026, 9, 1), asOf: D(2026, 10, 1));

        due.DueOn.Should().Be(D(2026, 10, 1));
        due.MonthsOverdue.Should().Be(0);
    }

    [Fact]
    public void Several_months_late_counts_every_month()
    {
        var due = UdhaarDueDate.For(boughtOn: D(2026, 9, 1), asOf: D(2027, 1, 20));

        due.DueOn.Should().Be(D(2027, 2, 1));
        due.MonthsOverdue.Should().Be(4);
    }

    [Fact]
    public void A_purchase_on_the_31st_falls_due_on_the_last_day_of_a_short_month()
    {
        UdhaarDueDate.For(boughtOn: D(2026, 1, 31), asOf: D(2026, 1, 31))
            .DueOn.Should().Be(D(2026, 2, 28));
    }

    [Fact]
    public void Rolling_forward_does_not_drift_to_the_short_months_day()
    {
        // Counted from the purchase every time, never from the previous due date — otherwise one
        // short February would move every later due date from the 31st to the 28th for good.
        UdhaarDueDate.For(boughtOn: D(2026, 1, 31), asOf: D(2026, 3, 1))
            .DueOn.Should().Be(D(2026, 3, 31));
    }

    // ================================================================
    //  Which purchase is the unpaid one
    // ================================================================

    private static UdhaarMovement Bill(int month, int day, decimal amount, decimal paidAtCounter = 0m) =>
        new(D(2026, month, day), amount, paidAtCounter);

    private static UdhaarMovement Paid(int month, int day, decimal amount) =>
        new(D(2026, month, day), 0m, amount);

    [Fact]
    public void Payments_settle_the_oldest_purchase_first()
    {
        // 1,000 on 1 Sep and 1,000 on 10 Sep. Paying 1,000 clears September the 1st — so what is
        // left is the 10th's, and the clock runs from the 10th.
        var unpaidSince = UdhaarDueDate.OldestUnpaidPurchase(
        [
            Bill(9, 1, 1000m),
            Bill(9, 10, 1000m),
            Paid(9, 20, 1000m),
        ]);

        unpaidSince.Should().Be(D(2026, 9, 10));
    }

    [Fact]
    public void A_part_payment_does_not_restart_the_clock()
    {
        // Paying a little every month must not keep the debt forever "not yet due".
        var unpaidSince = UdhaarDueDate.OldestUnpaidPurchase(
        [
            Bill(9, 1, 1000m),
            Paid(9, 25, 100m),
            Paid(10, 25, 100m),
        ]);

        unpaidSince.Should().Be(D(2026, 9, 1));
    }

    [Fact]
    public void What_was_paid_at_the_counter_counts_against_that_same_bill()
    {
        // A 1,000 sale with 1,000 paid on the spot owes nothing; the 500 sale after it is the debt.
        var unpaidSince = UdhaarDueDate.OldestUnpaidPurchase(
        [
            Bill(9, 1, 1000m, paidAtCounter: 1000m),
            Bill(9, 5, 500m),
        ]);

        unpaidSince.Should().Be(D(2026, 9, 5));
    }

    [Fact]
    public void An_amount_brought_forward_from_the_register_is_the_oldest_debt()
    {
        var unpaidSince = UdhaarDueDate.OldestUnpaidPurchase(
        [
            Bill(8, 15, 3000m),
            Bill(9, 1, 1000m),
        ]);

        unpaidSince.Should().Be(D(2026, 8, 15));
    }

    [Fact]
    public void A_settled_account_has_nothing_falling_due()
    {
        UdhaarDueDate.OldestUnpaidPurchase([Bill(9, 1, 1000m), Paid(9, 20, 1000m)])
            .Should().BeNull();

        // An overpayment the owner confirmed leaves the shop owing the customer, not the reverse.
        UdhaarDueDate.OldestUnpaidPurchase([Bill(9, 1, 1000m), Paid(9, 20, 1200m)])
            .Should().BeNull();

        UdhaarDueDate.OldestUnpaidPurchase([]).Should().BeNull();
    }
}
