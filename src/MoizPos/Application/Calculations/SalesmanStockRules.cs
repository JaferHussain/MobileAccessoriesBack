namespace MoizPos.Application.Calculations;

/// <summary>
/// Where the shop's owned stock physically is. <c>products.quantity_on_hand</c> is what the shop
/// OWNS; units in a field salesman's bag are still owned — so stock value is unchanged — but they
/// are not on the shelf, and the counter must not sell them.
/// </summary>
public static class SalesmanStockRules
{
    /// <summary>Units physically in the shop: owned, less everything salesmen carry.</summary>
    public static int AtShop(int onHand, int heldBySalesmen) => Math.Max(0, onHand - heldBySalesmen);

    /// <summary>The owner can issue only what is on the shelf.</summary>
    public static bool CanIssue(int onHand, int heldBySalesmen, int quantity) =>
        quantity <= AtShop(onHand, heldBySalesmen);

    /// <summary>
    /// A purchase return or stock correction may not leave the shop owning fewer units than
    /// salesmen are carrying — that would describe goods that do not exist.
    /// </summary>
    public static bool CanReduceOwned(int onHand, int heldBySalesmen, int reduceBy) =>
        onHand - reduceBy >= heldBySalesmen;
}
