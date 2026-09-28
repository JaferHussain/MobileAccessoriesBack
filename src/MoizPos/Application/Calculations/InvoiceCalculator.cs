using MoizPos.Domain.Errors;

namespace MoizPos.Application.Calculations;

/// <summary>One cart line as submitted. Prices are always <see cref="decimal"/> (research.md R5).</summary>
/// <param name="Quantity">Units sold. Must be greater than zero.</param>
/// <param name="UnitSalePrice">Price actually charged per unit; may differ from the catalogue price.</param>
/// <param name="LineDiscount">Discount on this line as a whole, not per unit.</param>
public readonly record struct InvoiceLineInput(int Quantity, decimal UnitSalePrice, decimal LineDiscount);

/// <summary>A priced line after calculation.</summary>
public readonly record struct InvoiceLineResult(
    int Quantity,
    decimal UnitSalePrice,
    decimal LineDiscount,
    decimal LineTotal);

/// <summary>The authoritative totals for a sale.</summary>
public readonly record struct InvoiceTotals(
    IReadOnlyList<InvoiceLineResult> Lines,
    decimal Subtotal,
    decimal TotalDiscount,
    decimal Total,
    decimal AmountPaid,
    decimal AmountRemaining);

/// <summary>
/// Pure invoice arithmetic — no I/O, no clock, no database. This is the single place sale totals
/// are decided, and the server always recomputes them here rather than trusting anything the
/// counter sends (FR-013, Constitution Principle IV).
/// </summary>
public static class InvoiceCalculator
{
    /// <summary>Money is held to 2 decimal places; half a paisa rounds away from zero.</summary>
    private const int MoneyScale = 2;

    public static InvoiceTotals Calculate(
        IReadOnlyList<InvoiceLineInput> lines,
        decimal orderDiscount,
        decimal amountPaid)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            throw new BusinessRuleViolationException("A sale must contain at least one line item.");
        }

        if (orderDiscount < 0m)
        {
            throw new BusinessRuleViolationException("Order discount cannot be negative.");
        }

        if (amountPaid < 0m)
        {
            throw new BusinessRuleViolationException("Amount paid cannot be negative.");
        }

        var results = new List<InvoiceLineResult>(lines.Count);
        var subtotal = 0m;

        foreach (var line in lines)
        {
            var lineResult = CalculateLine(line);
            results.Add(lineResult);
            subtotal += lineResult.LineTotal;
        }

        subtotal = Round(subtotal);

        // The order discount applies to what is left after line discounts, so it can never
        // exceed the subtotal (spec edge case: "a discount exceeds the line or order value").
        if (orderDiscount > subtotal)
        {
            throw new DiscountExceedsTotalException(orderDiscount, subtotal);
        }

        var total = Round(subtotal - orderDiscount);

        if (amountPaid > total)
        {
            throw new BusinessRuleViolationException(
                $"Amount paid ({amountPaid:0.00}) cannot exceed the invoice total ({total:0.00}). " +
                "Record an overpayment against the customer's ledger instead.");
        }

        var lineDiscountTotal = Round(results.Sum(l => l.LineDiscount));

        return new InvoiceTotals(
            Lines: results,
            Subtotal: subtotal,
            TotalDiscount: Round(lineDiscountTotal + orderDiscount),
            Total: total,
            AmountPaid: Round(amountPaid),
            AmountRemaining: Round(total - amountPaid));
    }

    private static InvoiceLineResult CalculateLine(InvoiceLineInput line)
    {
        if (line.Quantity <= 0)
        {
            throw new BusinessRuleViolationException("Line quantity must be greater than zero.");
        }

        if (line.UnitSalePrice < 0m)
        {
            throw new BusinessRuleViolationException("Unit sale price cannot be negative.");
        }

        if (line.LineDiscount < 0m)
        {
            throw new BusinessRuleViolationException("Line discount cannot be negative.");
        }

        var gross = Round(line.UnitSalePrice * line.Quantity);

        if (line.LineDiscount > gross)
        {
            throw new DiscountExceedsTotalException(line.LineDiscount, gross);
        }

        return new InvoiceLineResult(
            Quantity: line.Quantity,
            UnitSalePrice: line.UnitSalePrice,
            LineDiscount: Round(line.LineDiscount),
            LineTotal: Round(gross - line.LineDiscount));
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, MoneyScale, MidpointRounding.AwayFromZero);
}
