using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Reports;

/// <summary>
/// When a discount is big enough for the owner's watch list. A flag to look at, not an accusation:
/// most have an ordinary reason, and the note on the bill is where it goes.
/// </summary>
public sealed class WatchRulesTests
{
    [Theory]
    [InlineData(1000, 100, true)]    // exactly 10%
    [InlineData(1000, 150, true)]
    [InlineData(1000, 99.99, false)]
    [InlineData(1000, 0, false)]
    public void A_discount_of_a_tenth_or_more_of_the_bill_is_flagged(decimal gross, decimal discount, bool flagged)
    {
        WatchRules.IsBigDiscount(gross, discount).Should().Be(flagged);
    }

    [Fact]
    public void A_bill_worth_nothing_is_never_flagged()
    {
        WatchRules.IsBigDiscount(0m, 0m).Should().BeFalse();
    }

    [Fact]
    public void The_share_is_worked_out_to_the_percent()
    {
        WatchRules.DiscountPercent(1000m, 150m).Should().Be(15m);
    }
}
