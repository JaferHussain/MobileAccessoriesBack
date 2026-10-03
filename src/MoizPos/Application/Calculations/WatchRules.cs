namespace MoizPos.Application.Calculations;

/// <summary>
/// What lands on the owner's watch list. Pure, so the thresholds are tested without a database.
///
/// <para>A flag is something to look at, never an accusation: most have an ordinary reason. That
/// is why the thresholds are deliberately plain — the owner can read one and know at once why a
/// bill was flagged.</para>
/// </summary>
public static class WatchRules
{
    /// <summary>A discount of a tenth or more of what the goods were listed at.</summary>
    public const decimal BigDiscountShare = 0.10m;

    /// <param name="gross">What the goods were listed at, before any discount.</param>
    /// <param name="discount">Every discount given on the bill — line and whole-bill together.</param>
    public static bool IsBigDiscount(decimal gross, decimal discount) =>
        gross > 0m && discount > 0m && discount >= gross * BigDiscountShare;

    public static decimal DiscountPercent(decimal gross, decimal discount) =>
        gross <= 0m ? 0m : Math.Round(discount / gross * 100m, 1, MidpointRounding.AwayFromZero);
}
