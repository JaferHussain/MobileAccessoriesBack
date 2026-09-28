using MoizPos.Domain.Errors;

namespace MoizPos.Application.Calculations;

/// <summary>
/// What one returned unit is actually worth.
///
/// <para>An invoice line is priced BEFORE the order-level discount: a line of one charger at 600
/// on an invoice discounted by 10 records <c>line_total = 600</c> while the customer only ever
/// paid 590. Refunding the line total would hand back money that was never taken, and returning
/// every unit would add up to more than the sale was worth — which is exactly what produced
/// "Returning 600.00 exceeds the invoice's remaining value of 590.00" at the counter.</para>
///
/// <para><b>So the order discount is spread across the lines in proportion to their value.</b>
/// Each line's net value is <c>line_total x (total / subtotal)</c>, and a returned unit is worth
/// that divided by the quantity sold. Returning everything then settles at exactly the invoice
/// total, with no cap, no warning and no residue.</para>
///
/// <para>Line discounts need no spreading — they are already inside <c>line_total</c>.</para>
/// </summary>
public static class ReturnPricing
{
    private const int MoneyScale = 2;

    /// <summary>
    /// The line's share of what the customer actually paid: its own total less its proportional
    /// share of the order discount.
    /// </summary>
    public static decimal NetLineValue(decimal lineTotal, decimal invoiceSubtotal, decimal invoiceTotal)
    {
        if (lineTotal <= 0m || invoiceSubtotal <= 0m || invoiceTotal <= 0m)
        {
            // A line worth nothing, or a sale discounted to nothing, refunds nothing. The goods
            // still come back on the shelf — only the money is zero.
            return 0m;
        }

        // Defensive: with no order discount the two are equal, and a total above the subtotal
        // cannot happen — never inflate a refund above what the line recorded.
        return invoiceTotal >= invoiceSubtotal
            ? lineTotal
            : Round(lineTotal * invoiceTotal / invoiceSubtotal);
    }

    /// <summary>What one unit of this line is worth back — the figure the counter is shown.</summary>
    public static decimal EffectiveUnitPrice(
        decimal lineTotal, int quantity, decimal invoiceSubtotal, decimal invoiceTotal)
    {
        EnsurePositive(quantity);

        return Round(NetLineValue(lineTotal, invoiceSubtotal, invoiceTotal) / quantity);
    }

    /// <summary>
    /// What returning <paramref name="returnQuantity"/> units of this line is worth.
    /// Computed from the line's net value rather than from the rounded unit price, so returning
    /// every unit reproduces the line's value exactly instead of drifting by a paisa a unit.
    /// </summary>
    public static decimal RefundFor(
        decimal lineTotal,
        int quantity,
        int returnQuantity,
        decimal invoiceSubtotal,
        decimal invoiceTotal)
    {
        EnsurePositive(quantity);

        if (returnQuantity <= 0)
        {
            return 0m;
        }

        var net = NetLineValue(lineTotal, invoiceSubtotal, invoiceTotal);

        return returnQuantity >= quantity ? net : Round(net * returnQuantity / quantity);
    }

    private static void EnsurePositive(int quantity)
    {
        if (quantity <= 0)
        {
            throw new BusinessRuleViolationException("An invoice line cannot have zero quantity.");
        }
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, MoneyScale, MidpointRounding.AwayFromZero);
}
