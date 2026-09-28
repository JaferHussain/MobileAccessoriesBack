using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// What should be in the drawer at the end of the day, and what actually is.
///
/// <para>This is the only control the shop has over physical cash. Every other figure reconciles
/// against itself: a salesman can take a cash sale, hand over the goods, record it perfectly and
/// pocket the notes, and nothing in the system would ever disagree. Counting the drawer against
/// what the day recorded is what makes that visible.</para>
/// </summary>
public sealed class CashDrawerTests
{
    private static CashDrawerCount Count(
        decimal openingFloat = 2000m,
        decimal cashSales = 0m,
        decimal cashRecovery = 0m,
        decimal cashRefunds = 0m,
        decimal cashPaidOut = 0m,
        decimal cashToSuppliers = 0m,
        decimal counted = 0m) =>
        CashDrawer.Reconcile(
            openingFloat, cashSales, cashRecovery, cashRefunds, cashPaidOut, cashToSuppliers,
            counted);

    [Fact]
    public void Everything_that_came_in_and_went_out_adds_up()
    {
        var result = Count(
            openingFloat: 2000m,
            cashSales: 18_400m,
            cashRecovery: 3500m,
            cashRefunds: 590m,
            cashPaidOut: 1200m,
            counted: 22_110m);

        result.Expected.Should().Be(22_110m);
        result.Difference.Should().Be(0m);
        result.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void A_short_drawer_is_reported_as_a_negative_difference()
    {
        var result = Count(openingFloat: 2000m, cashSales: 1000m, counted: 2500m);

        // 500 that the day says was taken is not in the drawer.
        result.Expected.Should().Be(3000m);
        result.Difference.Should().Be(-500m);
        result.IsShort.Should().BeTrue();
        result.IsBalanced.Should().BeFalse();
    }

    [Fact]
    public void An_over_drawer_is_reported_too_rather_than_quietly_accepted()
    {
        var result = Count(openingFloat: 2000m, cashSales: 1000m, counted: 3200m);

        // More money than the day accounts for is not good news — it usually means a sale went
        // unrecorded, or change was given wrong.
        result.Difference.Should().Be(200m);
        result.IsShort.Should().BeFalse();
        result.IsBalanced.Should().BeFalse();
    }

    [Fact]
    public void Money_paid_back_to_a_customer_leaves_the_drawer()
    {
        var result = Count(openingFloat: 0m, cashSales: 1000m, cashRefunds: 300m, counted: 700m);

        result.Expected.Should().Be(700m);
        result.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void Money_taken_out_for_an_expense_leaves_the_drawer()
    {
        var result = Count(openingFloat: 0m, cashSales: 1000m, cashPaidOut: 250m, counted: 750m);

        result.Expected.Should().Be(750m);
        result.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void Recovering_an_old_debt_in_cash_puts_money_in_the_drawer()
    {
        // Not a sale — the goods left weeks ago — but the notes are in the drawer today.
        var result = Count(openingFloat: 0m, cashRecovery: 1500m, counted: 1500m);

        result.Expected.Should().Be(1500m);
        result.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void A_day_with_no_trade_still_expects_the_float_back()
    {
        var result = Count(openingFloat: 2000m, counted: 2000m);

        result.Expected.Should().Be(2000m);
        result.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_rounded_away()
    {
        var result = Count(openingFloat: 0m, cashSales: 999.99m, counted: 999.98m);

        // A paisa short is still short. Rounding it out would hide exactly the small, repeated
        // differences worth noticing.
        result.Difference.Should().Be(-0.01m);
        result.IsBalanced.Should().BeFalse();
    }

    [Fact]
    public void Paying_a_supplier_in_cash_takes_money_out_of_the_drawer()
    {
        var result = Count(openingFloat: 0m, cashSales: 5000m, cashToSuppliers: 3000m, counted: 2000m);

        // Settling a bill from the till is the second way notes leave the drawer, and the one
        // that moves the largest amounts. Leaving it out reported a short for money that had
        // been paid out perfectly legitimately.
        result.Expected.Should().Be(2000m);
        result.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void Both_ways_money_leaves_the_drawer_are_subtracted()
    {
        var result = Count(
            openingFloat: 1000m,
            cashSales: 9000m,
            cashPaidOut: 250m,
            cashToSuppliers: 4000m,
            counted: 5750m);

        result.Expected.Should().Be(5750m);
        result.Difference.Should().Be(0m);
    }

    [Fact]
    public void A_negative_payment_to_a_supplier_is_refused()
    {
        var act = () => Count(cashToSuppliers: -1m);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void A_negative_count_is_refused_rather_than_stored()
    {
        var act = () => Count(counted: -1m);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void A_negative_float_is_refused()
    {
        var act = () => Count(openingFloat: -1m);

        act.Should().Throw<Exception>();
    }
}
