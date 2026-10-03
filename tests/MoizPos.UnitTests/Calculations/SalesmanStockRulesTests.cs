using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// Where the shop's owned stock physically is. quantity_on_hand is what the shop owns; what is in
/// a salesman's bag is still owned, but cannot be sold across the counter.
/// </summary>
public sealed class SalesmanStockRulesTests
{
    [Fact]
    public void What_the_counter_can_sell_is_what_is_owned_less_what_salesmen_carry() =>
        SalesmanStockRules.AtShop(onHand: 10, heldBySalesmen: 4).Should().Be(6);

    [Fact]
    public void Nothing_carried_leaves_every_owned_unit_at_the_shop() =>
        SalesmanStockRules.AtShop(onHand: 10, heldBySalesmen: 0).Should().Be(10);

    [Fact]
    public void Never_reads_below_zero_even_if_figures_were_edited_outside_the_app() =>
        SalesmanStockRules.AtShop(onHand: 2, heldBySalesmen: 5).Should().Be(0);

    [Theory]
    [InlineData(10, 4, 6, true)]
    [InlineData(10, 4, 7, false)]
    public void The_owner_can_issue_only_what_is_in_the_shop(int onHand, int held, int issue, bool allowed) =>
        SalesmanStockRules.CanIssue(onHand, held, issue).Should().Be(allowed);

    [Fact]
    public void Owned_stock_can_never_be_reduced_below_what_salesmen_carry()
    {
        // A purchase return or a counting correction that would leave the shop owning fewer units
        // than are sitting in salesmen's bags describes goods that do not exist.
        SalesmanStockRules.CanReduceOwned(onHand: 10, heldBySalesmen: 4, reduceBy: 6).Should().BeTrue();
        SalesmanStockRules.CanReduceOwned(onHand: 10, heldBySalesmen: 4, reduceBy: 7).Should().BeFalse();
    }
}
