using FluentAssertions;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// A returned unit is worth what the customer paid for it, not what the line was priced at
/// before the order discount came off. The whole point is that returning everything settles at
/// the invoice total — never above it.
/// </summary>
public sealed class ReturnPricingTests
{
    // The exact case from the counter: one charger at 600, invoice discounted by 10.
    [Fact]
    public void A_unit_is_worth_its_share_of_the_discounted_total()
    {
        ReturnPricing.EffectiveUnitPrice(
            lineTotal: 600m, quantity: 1, invoiceSubtotal: 600m, invoiceTotal: 590m)
            .Should().Be(590m);
    }

    [Fact]
    public void Returning_everything_settles_at_the_invoice_total_and_never_above_it()
    {
        // Two lines, 600 and 400, on an invoice discounted by 100 -> total 900.
        var first = ReturnPricing.RefundFor(600m, 2, 2, 1000m, 900m);
        var second = ReturnPricing.RefundFor(400m, 1, 1, 1000m, 900m);

        (first + second).Should().Be(900m, "a full return can never exceed what the sale was worth");
        first.Should().Be(540m);
        second.Should().Be(360m);
    }

    [Fact]
    public void Without_an_order_discount_a_unit_is_worth_the_line_price()
    {
        ReturnPricing.EffectiveUnitPrice(2200m, 2, 2200m, 2200m).Should().Be(1100m);
        ReturnPricing.RefundFor(2200m, 2, 1, 2200m, 2200m).Should().Be(1100m);
    }

    [Fact]
    public void A_line_discount_is_already_inside_the_line_total()
    {
        // 2 x 600 less a 200 line discount = 1,000; no order discount.
        ReturnPricing.EffectiveUnitPrice(1000m, 2, 1000m, 1000m).Should().Be(500m);
    }

    [Fact]
    public void A_part_return_is_priced_from_the_net_line_value()
    {
        // 3 units, line 900, invoice 900 -> 600 discounted to 850? Use a clean proportion:
        // line 900 of a 1,000 subtotal discounted to 500 -> net 450, one of three units = 150.
        ReturnPricing.RefundFor(900m, 3, 1, 1000m, 500m).Should().Be(150m);
    }

    [Fact]
    public void A_sale_discounted_to_nothing_refunds_nothing()
    {
        ReturnPricing.RefundFor(600m, 1, 1, 600m, 0m).Should().Be(0m);
        ReturnPricing.EffectiveUnitPrice(600m, 1, 600m, 0m).Should().Be(0m);
    }

    [Fact]
    public void A_total_above_the_subtotal_never_inflates_the_refund()
    {
        ReturnPricing.RefundFor(600m, 1, 1, 600m, 700m).Should().Be(600m);
    }

    [Fact]
    public void Returning_no_units_is_worth_nothing()
    {
        ReturnPricing.RefundFor(600m, 2, 0, 600m, 600m).Should().Be(0m);
    }
}

/// <summary>
/// The search result the counter is sent. Every figure must be a real, populated property —
/// a missing one reaches the shop as "Rs NaN" and no return can be recorded at all.
/// </summary>
public sealed class ReturnableSaleLineTests
{
    private static ReturnableLineRow Row(
        int quantity = 2, int returnedQty = 0, decimal unitSalePrice = 600m,
        decimal lineTotal = 1200m, decimal subtotal = 1200m, decimal total = 1190m) =>
        new()
        {
            InvoiceId = 7,
            InvoiceNumber = "INV-2026-000001",
            InvoiceItemId = 9,
            ProductName = "Charger 18W QC3.0",
            Quantity = quantity,
            ReturnedQty = returnedQty,
            UnitSalePrice = unitSalePrice,
            LineTotal = lineTotal,
            InvoiceSubtotal = subtotal,
            InvoiceTotal = total,
            AmountRemaining = 0m,
        };

    [Fact]
    public void Names_every_amount_the_counter_needs()
    {
        var line = ReturnableSaleLine.From(Row());

        line.UnitSalePrice.Should().Be(600m, "what the receipt shows");
        line.RefundPerUnit.Should().Be(595m, "what a unit is actually worth back");
        line.DiscountPerUnit.Should().Be(5m);
        line.MaxRefund.Should().Be(1190m, "both units, at the discounted value");
    }

    [Fact]
    public void Counts_what_is_still_available_rather_than_making_the_screen_subtract()
    {
        var line = ReturnableSaleLine.From(Row(quantity: 3, returnedQty: 1));

        line.QuantitySold.Should().Be(3);
        line.QuantityReturned.Should().Be(1);
        line.QuantityAvailable.Should().Be(2);
    }

    [Fact]
    public void An_undiscounted_sale_refunds_the_billed_price_and_shows_no_discount()
    {
        var line = ReturnableSaleLine.From(
            Row(unitSalePrice: 1100m, lineTotal: 2200m, subtotal: 2200m, total: 2200m));

        line.RefundPerUnit.Should().Be(1100m);
        line.DiscountPerUnit.Should().Be(0m);
        line.MaxRefund.Should().Be(2200m);
    }

    [Fact]
    public void A_fully_returned_line_is_worth_nothing_more()
    {
        var line = ReturnableSaleLine.From(Row(quantity: 2, returnedQty: 2));

        line.QuantityAvailable.Should().Be(0);
        line.MaxRefund.Should().Be(0m);
    }
}
