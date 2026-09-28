namespace MoizPos.Application.Calculations;

/// <summary>
/// One sale line as the profit engine sees it. <paramref name="UnitCostPrice"/> is the cost
/// snapshotted when the sale was made, never the product's current cost.
/// </summary>
public readonly record struct ProfitLine(
    decimal UnitSalePrice,
    decimal UnitCostPrice,
    int Quantity,
    int ReturnedQty,
    decimal LineDiscount);

/// <summary>
/// Pure profit arithmetic (FR-031, FR-032).
///
/// Cost attribution follows the shop owner's decision: the latest purchase cost replaces the
/// cost of all stock on hand, and each sale line stores the cost in force at the moment of sale.
/// This calculator therefore never looks up a product — it uses only what the line recorded, so
/// a later purchase can never rewrite the profit of a sale already made (FR-011c).
/// </summary>
public static class ProfitCalculator
{
    private const int MoneyScale = 2;

    /// <summary>
    /// Gross profit for one line: (sale price − cost price) × units still sold, less the line's
    /// discount. Returned units are excluded so they contribute no profit (FR-027).
    /// </summary>
    public static decimal LineGrossProfit(
        decimal unitSalePrice,
        decimal unitCostPrice,
        int quantity,
        int returnedQty,
        decimal lineDiscount)
    {
        var soldQty = quantity - returnedQty;

        if (soldQty <= 0)
        {
            return 0m;
        }

        var margin = (unitSalePrice - unitCostPrice) * soldQty;

        return Round(margin - lineDiscount);
    }

    /// <summary>Gross profit across many lines.</summary>
    public static decimal TotalGrossProfit(IReadOnlyList<ProfitLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var total = 0m;

        foreach (var line in lines)
        {
            total += LineGrossProfit(
                line.UnitSalePrice,
                line.UnitCostPrice,
                line.Quantity,
                line.ReturnedQty,
                line.LineDiscount);
        }

        return Round(total);
    }

    /// <summary>
    /// Net profit for a period: gross profit less the expenses recorded in that period (FR-032).
    /// May be negative — a loss is a real answer, not an error.
    /// </summary>
    public static decimal NetProfit(decimal grossProfit, decimal totalExpenses) =>
        Round(grossProfit - totalExpenses);

    private static decimal Round(decimal value) =>
        Math.Round(value, MoneyScale, MidpointRounding.AwayFromZero);
}
