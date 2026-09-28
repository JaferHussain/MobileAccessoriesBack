using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// T127 / T129 — the profit engine. Encodes the worked example from spec.md US4 scenario 1
/// (sale 1,100, cost 800 -> profit 300) and the net profit rollup from scenario 2.
/// </summary>
public sealed class ProfitCalculatorTests
{
    // --- spec US4 scenario 1 ---

    [Fact]
    public void Gross_profit_is_sale_less_cost_times_quantity()
    {
        var profit = ProfitCalculator.LineGrossProfit(
            unitSalePrice: 1100m,
            unitCostPrice: 800m,
            quantity: 1,
            returnedQty: 0,
            lineDiscount: 0m);

        profit.Should().Be(300m);
    }

    [Fact]
    public void Gross_profit_scales_with_quantity()
    {
        var profit = ProfitCalculator.LineGrossProfit(1100m, 800m, quantity: 4, returnedQty: 0, lineDiscount: 0m);

        profit.Should().Be(1200m);
    }

    [Fact]
    public void Line_discount_reduces_gross_profit()
    {
        var profit = ProfitCalculator.LineGrossProfit(1100m, 800m, quantity: 2, returnedQty: 0, lineDiscount: 100m);

        // (1100 - 800) * 2 = 600, less the 100 discount = 500.
        profit.Should().Be(500m);
    }

    // --- returns must not contribute profit (FR-027) ---

    [Fact]
    public void Returned_units_do_not_contribute_profit()
    {
        var profit = ProfitCalculator.LineGrossProfit(1100m, 800m, quantity: 3, returnedQty: 1, lineDiscount: 0m);

        // Only 2 units remain sold.
        profit.Should().Be(600m);
    }

    [Fact]
    public void A_fully_returned_line_contributes_no_profit()
    {
        var profit = ProfitCalculator.LineGrossProfit(1100m, 800m, quantity: 2, returnedQty: 2, lineDiscount: 0m);

        profit.Should().Be(0m);
    }

    [Fact]
    public void Selling_below_cost_produces_a_loss()
    {
        var profit = ProfitCalculator.LineGrossProfit(700m, 800m, quantity: 1, returnedQty: 0, lineDiscount: 0m);

        profit.Should().Be(-100m);
    }

    [Fact]
    public void A_zero_quantity_line_contributes_nothing()
    {
        var profit = ProfitCalculator.LineGrossProfit(1100m, 800m, quantity: 0, returnedQty: 0, lineDiscount: 0m);

        profit.Should().Be(0m);
    }

    // --- the owner's latest-cost rule: profit uses the cost recorded on the line ---

    [Fact]
    public void Uses_the_cost_snapshotted_on_the_line_not_a_current_cost()
    {
        // A sale recorded when the cost was 800 keeps reporting against 800, even though the
        // product's current cost has since moved to 850 (FR-011c, spec US4 scenario 3).
        const decimal costAtSaleTime = 800m;

        var profit = ProfitCalculator.LineGrossProfit(1100m, costAtSaleTime, 1, 0, 0m);

        profit.Should().Be(300m);
    }

    [Fact]
    public void A_sale_after_a_cost_rise_reports_against_the_new_cost()
    {
        // After a purchase at 850, the latest-cost rule means every unit on hand costs 850 —
        // including units bought earlier at 800 (FR-011a). Profit narrows accordingly.
        var profit = ProfitCalculator.LineGrossProfit(1100m, 850m, 1, 0, 0m);

        profit.Should().Be(250m);
    }

    // --- spec US4 scenario 2: gross 50,000 less expenses 12,000 -> net 38,000 ---

    [Fact]
    public void Net_profit_is_gross_profit_less_expenses()
    {
        var net = ProfitCalculator.NetProfit(grossProfit: 50_000m, totalExpenses: 12_000m);

        net.Should().Be(38_000m);
    }

    [Fact]
    public void Net_profit_can_be_negative_when_expenses_exceed_gross_profit()
    {
        var net = ProfitCalculator.NetProfit(grossProfit: 5_000m, totalExpenses: 12_000m);

        net.Should().Be(-7_000m);
    }

    [Fact]
    public void Net_profit_with_no_expenses_equals_gross_profit()
    {
        ProfitCalculator.NetProfit(50_000m, 0m).Should().Be(50_000m);
    }

    // --- aggregation ---

    [Fact]
    public void Sums_gross_profit_across_lines()
    {
        var lines = new[]
        {
            new ProfitLine(UnitSalePrice: 1100m, UnitCostPrice: 800m, Quantity: 2, ReturnedQty: 0, LineDiscount: 0m),
            new ProfitLine(500m, 300m, 1, 0, 50m),
            new ProfitLine(250m, 200m, 4, 1, 0m),
        };

        // 600 + 150 + 150 = 900
        ProfitCalculator.TotalGrossProfit(lines).Should().Be(900m);
    }

    [Fact]
    public void Total_gross_profit_of_no_lines_is_zero()
    {
        ProfitCalculator.TotalGrossProfit([]).Should().Be(0m);
    }

    // --- precision (research.md R5) ---

    [Fact]
    public void Keeps_paisa_precision()
    {
        var profit = ProfitCalculator.LineGrossProfit(100.10m, 100m, quantity: 3, returnedQty: 0, lineDiscount: 0m);

        profit.Should().Be(0.30m);
    }
}
