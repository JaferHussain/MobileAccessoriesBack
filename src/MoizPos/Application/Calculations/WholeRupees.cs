namespace MoizPos.Application.Calculations;

/// <summary>
/// The owner's rule: <b>every price is a whole number of rupees</b> — the cost paid for a unit,
/// the retail price and the wholesale price, and the price a sale charges. Paisa are not used
/// at this shop.
///
/// <para>Checked where a price is ENTERED: a purchase (single or bill) and a sale line. Figures
/// the shop works out — a total, a refund after a spread discount — are not prices and may still
/// carry paisa.</para>
/// </summary>
public static class WholeRupees
{
    public const string Message = "Prices are whole rupees — no paisa.";

    public static bool IsWhole(decimal amount) => decimal.Truncate(amount) == amount;

    /// <summary>A price that is absent is fine; one that is given must be whole.</summary>
    public static bool IsWhole(decimal? amount) => amount is null || IsWhole(amount.Value);
}
