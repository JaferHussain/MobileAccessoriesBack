using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Errors;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// T077 — the arithmetic every sale depends on. Written before InvoiceCalculator exists
/// (Constitution Principle I). Cases come from spec.md US1 scenarios 1–2 and the discount
/// edge cases.
/// </summary>
public sealed class InvoiceCalculatorTests
{
    private static InvoiceLineInput Line(int qty, decimal price, decimal discount = 0m) =>
        new(qty, price, discount);

    // --- spec US1 scenario 1: 2 units at 1,100, cash, no discount -> 2,200 remaining 0 ---

    [Fact]
    public void Computes_total_for_a_simple_cash_sale()
    {
        var result = InvoiceCalculator.Calculate(
            [Line(2, 1100m)],
            orderDiscount: 0m,
            amountPaid: 2200m);

        result.Subtotal.Should().Be(2200m);
        result.TotalDiscount.Should().Be(0m);
        result.Total.Should().Be(2200m);
        result.AmountPaid.Should().Be(2200m);
        result.AmountRemaining.Should().Be(0m);
    }

    // --- spec US1 scenario 2: subtotal 5,000, line discount 100, order discount 400 -> 4,500 ---

    [Fact]
    public void Applies_line_and_order_discounts_together()
    {
        // Spec US1 scenario 2: a cart of 5,000 with a 100 line discount and a 400 order
        // discount is payable at 4,500. Subtotal is net of line discounts (data-model.md §7).
        var result = InvoiceCalculator.Calculate(
            [Line(1, 3000m, discount: 100m), Line(1, 2000m)],
            orderDiscount: 400m,
            amountPaid: 4500m);

        result.Subtotal.Should().Be(4900m);
        result.TotalDiscount.Should().Be(500m);
        result.Total.Should().Be(4500m);
        result.AmountRemaining.Should().Be(0m);
    }

    [Fact]
    public void Sums_multiple_lines()
    {
        var result = InvoiceCalculator.Calculate(
            [Line(3, 250m), Line(2, 1100m), Line(1, 99.50m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        result.Subtotal.Should().Be(3049.50m);
        result.Total.Should().Be(3049.50m);
    }

    // --- partial payment and credit (FR-014) ---

    [Fact]
    public void Computes_remaining_for_a_partial_payment()
    {
        var result = InvoiceCalculator.Calculate(
            [Line(1, 3000m)],
            orderDiscount: 0m,
            amountPaid: 1000m);

        result.Total.Should().Be(3000m);
        result.AmountPaid.Should().Be(1000m);
        result.AmountRemaining.Should().Be(2000m);
    }

    [Fact]
    public void Computes_remaining_for_a_full_credit_sale()
    {
        var result = InvoiceCalculator.Calculate(
            [Line(1, 3000m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        result.AmountRemaining.Should().Be(3000m);
    }

    // --- discount edge cases (spec: "discount exceeds the line or order value") ---

    [Fact]
    public void Rejects_a_line_discount_larger_than_the_line()
    {
        var act = () => InvoiceCalculator.Calculate(
            [Line(2, 100m, discount: 250m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        act.Should().Throw<DiscountExceedsTotalException>();
    }

    [Fact]
    public void Allows_a_line_discount_exactly_equal_to_the_line()
    {
        var result = InvoiceCalculator.Calculate(
            [Line(2, 100m, discount: 200m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        result.Subtotal.Should().Be(0m);
        result.Total.Should().Be(0m);
    }

    [Fact]
    public void Rejects_an_order_discount_larger_than_the_subtotal()
    {
        var act = () => InvoiceCalculator.Calculate(
            [Line(1, 1000m)],
            orderDiscount: 1500m,
            amountPaid: 0m);

        act.Should().Throw<DiscountExceedsTotalException>();
    }

    [Fact]
    public void Never_produces_a_negative_total()
    {
        var result = InvoiceCalculator.Calculate(
            [Line(1, 1000m)],
            orderDiscount: 1000m,
            amountPaid: 0m);

        result.Total.Should().Be(0m);
        result.Total.Should().BeGreaterThanOrEqualTo(0m);
    }

    // --- payment validation ---

    [Fact]
    public void Rejects_paying_more_than_the_invoice_total()
    {
        var act = () => InvoiceCalculator.Calculate(
            [Line(1, 1000m)],
            orderDiscount: 0m,
            amountPaid: 1500m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    [Fact]
    public void Rejects_a_negative_payment()
    {
        var act = () => InvoiceCalculator.Calculate(
            [Line(1, 1000m)],
            orderDiscount: 0m,
            amountPaid: -1m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    [Fact]
    public void Rejects_an_empty_cart()
    {
        var act = () => InvoiceCalculator.Calculate([], orderDiscount: 0m, amountPaid: 0m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Rejects_a_non_positive_quantity(int quantity)
    {
        var act = () => InvoiceCalculator.Calculate(
            [Line(quantity, 100m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    [Fact]
    public void Rejects_a_negative_unit_price()
    {
        var act = () => InvoiceCalculator.Calculate(
            [Line(1, -5m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    [Fact]
    public void Rejects_a_negative_discount()
    {
        var act = () => InvoiceCalculator.Calculate(
            [Line(1, 100m, discount: -10m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    // --- money precision: no floating point drift (research.md R5) ---

    [Fact]
    public void Keeps_two_decimal_precision_without_drift()
    {
        // 0.1 + 0.2 in binary floating point is famously not 0.3.
        var result = InvoiceCalculator.Calculate(
            [Line(1, 0.10m), Line(1, 0.20m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        result.Subtotal.Should().Be(0.30m);
    }

    [Fact]
    public void Rounds_a_half_paisa_away_from_zero()
    {
        // 3 x 33.335 = 100.005 -> 100.01
        var result = InvoiceCalculator.Calculate(
            [Line(3, 33.335m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        result.Subtotal.Should().Be(100.01m);
    }

    [Fact]
    public void Line_totals_sum_exactly_to_the_subtotal()
    {
        var result = InvoiceCalculator.Calculate(
            [Line(3, 33.33m), Line(7, 12.12m), Line(2, 999.99m)],
            orderDiscount: 0m,
            amountPaid: 0m);

        result.Lines.Sum(l => l.LineTotal).Should().Be(result.Subtotal);
    }
}
