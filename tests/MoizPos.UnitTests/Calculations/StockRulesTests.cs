using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Errors;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// T049 — low stock boundary, and the pure half of the latest-cost rule (T063's arithmetic).
/// The cost-rule tests encode the shop owner's own worked example.
/// </summary>
public sealed class StockRulesTests
{
    // --- low stock boundary (FR-004): "at or below" the threshold ---

    [Theory]
    [InlineData(11, 10, false)]  // above threshold
    [InlineData(10, 10, true)]   // AT threshold is low stock
    [InlineData(9, 10, true)]    // below
    [InlineData(0, 10, true)]    // out of stock
    [InlineData(0, 0, true)]     // zero threshold, zero stock
    [InlineData(1, 0, false)]    // zero threshold, some stock
    public void Flags_low_stock_at_or_below_the_threshold(int quantity, int threshold, bool expected)
    {
        StockRules.IsLowStock(quantity, threshold).Should().Be(expected);
    }

    // --- the owner's latest-cost rule (FR-011a) ---

    [Fact]
    public void Purchase_cost_replaces_the_existing_cost_entirely()
    {
        // The owner's worked example: 10 bought at 800, 5 sold, then 10 bought at 850.
        // All 15 units on hand now cost 850 — NOT a weighted average of 825.
        var newCost = StockRules.NextCostPrice(currentCost: 800m, purchaseUnitCost: 850m);

        newCost.Should().Be(850m);
        newCost.Should().NotBe(825m, "the shop uses latest-cost, not weighted average");
    }

    [Fact]
    public void Cost_falls_when_a_cheaper_purchase_is_recorded()
    {
        // The rule is symmetric: a cheaper purchase lowers the cost of all stock on hand.
        StockRules.NextCostPrice(currentCost: 850m, purchaseUnitCost: 780m).Should().Be(780m);
    }

    [Fact]
    public void Cost_of_a_product_with_no_stock_becomes_the_purchase_cost()
    {
        StockRules.NextCostPrice(currentCost: 0m, purchaseUnitCost: 800m).Should().Be(800m);
    }

    [Fact]
    public void Rejects_a_non_positive_purchase_cost()
    {
        var act = () => StockRules.NextCostPrice(800m, 0m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    // --- stock arithmetic (FR-006): never negative ---

    [Fact]
    public void Applies_a_stock_increase()
    {
        StockRules.NextQuantity(current: 5, change: 50, productName: "Cable").Should().Be(55);
    }

    [Fact]
    public void Applies_a_stock_decrease()
    {
        StockRules.NextQuantity(current: 10, change: -2, productName: "Cable").Should().Be(8);
    }

    [Fact]
    public void Allows_stock_to_reach_exactly_zero()
    {
        StockRules.NextQuantity(current: 3, change: -3, productName: "Cable").Should().Be(0);
    }

    [Fact]
    public void Refuses_to_drive_stock_negative()
    {
        // spec US1 scenario 3: 3 in stock, attempt to sell 5.
        var act = () => StockRules.NextQuantity(current: 3, change: -5, productName: "Type-C Braided 2m");

        act.Should().Throw<InsufficientStockException>()
            .Which.Message.Should().Contain("Type-C Braided 2m").And.Contain("3");
    }

    [Fact]
    public void Reports_available_and_requested_on_an_insufficient_stock_failure()
    {
        var act = () => StockRules.NextQuantity(3, -5, "Cable");

        var ex = act.Should().Throw<InsufficientStockException>().Which;
        ex.Available.Should().Be(3);
        ex.Requested.Should().Be(5);
    }

    // --- return limits (FR-026) ---

    [Theory]
    [InlineData(5, 0, 5)]   // return everything
    [InlineData(5, 2, 3)]   // partial already returned
    [InlineData(5, 5, 0)]   // nothing left to return
    public void Computes_the_remaining_returnable_quantity(int original, int alreadyReturned, int expected)
    {
        StockRules.ReturnableQuantity(original, alreadyReturned).Should().Be(expected);
    }

    [Fact]
    public void Rejects_returning_more_than_was_sold()
    {
        var act = () => StockRules.EnsureReturnable(original: 2, alreadyReturned: 0, requested: 3);

        act.Should().Throw<ReturnExceedsOriginalException>();
    }

    [Fact]
    public void Rejects_a_second_return_that_exceeds_the_remainder()
    {
        var act = () => StockRules.EnsureReturnable(original: 5, alreadyReturned: 4, requested: 2);

        act.Should().Throw<ReturnExceedsOriginalException>();
    }

    [Fact]
    public void Allows_a_return_up_to_the_remainder()
    {
        var act = () => StockRules.EnsureReturnable(original: 5, alreadyReturned: 4, requested: 1);

        act.Should().NotThrow();
    }
}
