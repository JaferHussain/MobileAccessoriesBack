using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Enums;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// The salesman's commission — the owner's rule: <b>half of whatever he sells above the owner's
/// price</b>, earned once the customer has paid for it.
/// </summary>
public sealed class CommissionTests
{
    private static readonly DateOnly Sep1 = new(2026, 9, 1);

    // ================================================================
    //  The owner's price, and the commission on a line
    // ================================================================

    [Fact]
    public void A_retail_sale_is_measured_against_the_retail_price()
    {
        Commission.BasePrice(SaleType.Retail, retailPrice: 1000m, wholesalePrice: 900m).Should().Be(1000m);
    }

    [Fact]
    public void A_wholesale_sale_is_measured_against_the_wholesale_price_or_retail_when_there_is_none()
    {
        Commission.BasePrice(SaleType.Wholesale, 1000m, 900m).Should().Be(900m);
        Commission.BasePrice(SaleType.Wholesale, 1000m, 0m).Should().Be(1000m);
    }

    [Fact]
    public void Selling_at_1200_what_the_owner_prices_at_1000_earns_100()
    {
        // The owner's own example: Rs 200 above, half of it to the salesman.
        Commission.ForLine(effectiveUnitPrice: 1200m, basePrice: 1000m, unitsKept: 1, ratePercent: 50m)
            .Should().Be(100m);
    }

    [Fact]
    public void Every_unit_kept_earns_and_a_returned_one_does_not()
    {
        Commission.ForLine(1200m, 1000m, unitsKept: 3, ratePercent: 50m).Should().Be(300m);
        Commission.ForLine(1200m, 1000m, unitsKept: 0, ratePercent: 50m).Should().Be(0m);
    }

    [Fact]
    public void Selling_at_the_owners_price_earns_nothing()
    {
        Commission.ForLine(1000m, 1000m, 1, 50m).Should().Be(0m);
    }

    [Fact]
    public void Below_the_owners_price_is_refused_and_at_it_is_not()
    {
        Commission.IsBelowBase(effectiveUnitPrice: 999.99m, basePrice: 1000m).Should().BeTrue();
        Commission.IsBelowBase(1000m, 1000m).Should().BeFalse();
    }

    // ================================================================
    //  When it is earned: once the customer has paid
    // ================================================================

    private static CommissionDebt Sale(long id, int day, decimal net, decimal paidAtSale) =>
        new(id, Sep1.AddDays(day - 1), net, paidAtSale);

    private static CommissionCredit Paid(int day, decimal amount) => new(Sep1.AddDays(day - 1), amount);

    [Fact]
    public void A_sale_paid_in_full_at_the_counter_is_earned_the_same_day()
    {
        var paid = Commission.Settle([Sale(1, 1, 1200m, 1200m)], []);

        paid[1].Share.Should().Be(1m);
        paid[1].SettledOn.Should().Be(Sep1);
    }

    [Fact]
    public void Udhaar_is_earned_on_the_day_it_is_recovered()
    {
        // Sold on 1 September on udhaar, paid on the 20th — earned on the 20th.
        var paid = Commission.Settle([Sale(1, 1, 1200m, 0m)], [Paid(20, 1200m)]);

        paid[1].Share.Should().Be(1m);
        paid[1].SettledOn.Should().Be(Sep1.AddDays(19));
    }

    [Fact]
    public void Udhaar_recovered_the_same_day_is_earned_the_same_day()
    {
        var paid = Commission.Settle([Sale(1, 5, 1000m, 0m)], [Paid(5, 1000m)]);

        paid[1].SettledOn.Should().Be(Sep1.AddDays(4));
    }

    [Fact]
    public void Part_of_the_udhaar_recovered_earns_that_part_and_leaves_the_rest_pending()
    {
        var paid = Commission.Settle([Sale(1, 1, 1000m, 0m)], [Paid(10, 250m)]);

        paid[1].Share.Should().Be(0.25m);
        paid[1].SettledOn.Should().BeNull("it is not settled until every rupee is in");
    }

    [Fact]
    public void Payments_settle_the_oldest_debt_first()
    {
        // Two udhaar sales; a payment covering one settles the OLDER one, whoever sold it.
        var paid = Commission.Settle(
            [Sale(1, 1, 1000m, 0m), Sale(2, 3, 1000m, 0m)],
            [Paid(10, 1000m)]);

        paid[1].Share.Should().Be(1m);
        paid[2].Share.Should().Be(0m);
    }

    [Fact]
    public void An_amount_brought_forward_from_the_register_is_paid_before_any_sale()
    {
        // Opening balances are debts too, and older than anything sold through the software.
        var paid = Commission.Settle(
            [new CommissionDebt(-1, Sep1.AddDays(-30), 500m, 0m), Sale(1, 1, 1000m, 0m)],
            [Paid(10, 500m)]);

        paid[1].Share.Should().Be(0m);
    }

    [Fact]
    public void Earned_is_the_commission_times_the_share_paid()
    {
        Commission.Earned(commission: 100m, share: 0.25m).Should().Be(25m);
        Commission.Earned(commission: 100m, share: 1m).Should().Be(100m);
    }
}
