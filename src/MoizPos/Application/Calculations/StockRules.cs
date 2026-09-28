using MoizPos.Domain.Errors;

namespace MoizPos.Application.Calculations;

/// <summary>
/// Pure stock and cost rules. These are the decisions that keep the shelf count honest and the
/// cost basis predictable; every service that moves stock routes through here.
/// </summary>
public static class StockRules
{
    private const int CostScale = 4;

    /// <summary>
    /// A product is low on stock at or below its minimum threshold (FR-004). The boundary is
    /// inclusive on purpose: hitting the threshold is the signal to reorder, not passing it.
    /// </summary>
    public static bool IsLowStock(int quantityOnHand, int minStockThreshold) =>
        quantityOnHand <= minStockThreshold;

    /// <summary>
    /// The product's cost after a purchase.
    ///
    /// The shop uses <b>latest purchase cost</b>: the new purchase cost replaces the old one for
    /// every unit on hand, regardless of what those units actually cost (FR-011a). This is not a
    /// weighted average — buying 10 at 800, selling 5, then buying 10 at 850 leaves all 15 units
    /// costed at 850, not 825. The owner chose this deliberately so profit is measured against
    /// what it costs to replace the goods today.
    /// </summary>
    public static decimal NextCostPrice(decimal currentCost, decimal purchaseUnitCost)
    {
        _ = currentCost; // Intentionally unused: the previous cost has no bearing on the new one.

        if (purchaseUnitCost <= 0m)
        {
            throw new BusinessRuleViolationException("Purchase unit cost must be greater than zero.");
        }

        return Math.Round(purchaseUnitCost, CostScale, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The quantity after a signed stock movement, refusing anything that would drive stock
    /// below zero (FR-006). Callers must still hold a row lock — this guards the arithmetic,
    /// not the race (see research.md R4).
    /// </summary>
    public static int NextQuantity(int current, int change, string productName)
    {
        var next = current + change;

        if (next < 0)
        {
            throw new InsufficientStockException(productName, available: current, requested: -change);
        }

        return next;
    }

    /// <summary>How many units of an original sale or purchase may still be returned.</summary>
    public static int ReturnableQuantity(int original, int alreadyReturned) =>
        Math.Max(0, original - alreadyReturned);

    /// <summary>
    /// Guards a return against exceeding what was originally sold or purchased (FR-026),
    /// including the case where earlier returns have already consumed part of it.
    /// </summary>
    public static void EnsureReturnable(int original, int alreadyReturned, int requested)
    {
        if (requested <= 0)
        {
            throw new BusinessRuleViolationException("Return quantity must be greater than zero.");
        }

        if (requested > ReturnableQuantity(original, alreadyReturned))
        {
            throw new ReturnExceedsOriginalException(alreadyReturned, original, requested);
        }
    }
}
