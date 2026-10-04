using System.Globalization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>One product line, stocked: what it did to the product.</summary>
public sealed record StockedLine(
    long PurchaseId,
    long ProductId,
    string ProductName,
    int Quantity,
    decimal UnitCost,
    decimal Total,
    int NewQuantityOnHand,
    decimal NewCostPrice,
    decimal NewRetailPrice,
    decimal NewWholesalePrice);

/// <summary>
/// Stocks ONE product line inside the caller's transaction — the purchase row, stock up, the
/// latest-cost rule, the selling prices, the stock movement and the product's audit entries.
///
/// <para>Shared by a single purchase and a purchase bill, so the costing rule (FR-011a) and the
/// first-stocking price rule live in exactly one place. The supplier's payable is the caller's:
/// a bill raises it once, by its total.</para>
/// </summary>
internal static class PurchaseLineWriter
{
    /// <param name="product">The product row, already LOCKED by the caller.</param>
    public static async Task<StockedLine> StockAsync(
        IUnitOfWork uow,
        IPurchaseWriteRepository purchases,
        IStockMovementWriter stockMovements,
        IAuditWriter audit,
        ProductStockSnapshot product,
        long supplierId,
        long? purchaseBillId,
        int quantity,
        decimal unitCost,
        decimal? newRetailPrice,
        decimal? newWholesalePrice,
        DateTime purchaseDateUtc,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var total = Round(unitCost * quantity);

        // The first delivery is what turns a catalogue entry into something sellable, so it is
        // the one that must state a price. Checked against the LOCKED row, before any write, so
        // two first-purchases racing cannot both see "no price yet".
        if (product.RetailPrice <= 0m && newRetailPrice is null or <= 0m)
        {
            throw new BusinessRuleViolationException(
                $"'{product.Name}' has no selling price yet. Set the retail price on this " +
                "purchase — it is what makes the product sellable.");
        }

        var purchaseId = await purchases.InsertPurchaseAsync(
            uow, supplierId, product.Id, purchaseDateUtc, unitCost, quantity, total, userId, nowUtc,
            cancellationToken, purchaseBillId);

        // Stock up; cost overwritten for ALL units on hand; prices set or left standing.
        var newQuantity = StockRules.NextQuantity(product.QuantityOnHand, quantity, product.Name);
        var newCost = StockRules.NextCostPrice(product.CostPrice, unitCost);

        // Omitted means "leave it as it is" — a repeat delivery at the same price should not
        // require the shopkeeper to retype what the shop already knows.
        var retail = newRetailPrice ?? product.RetailPrice;
        var wholesale = newWholesalePrice ?? product.WholesalePrice;

        await purchases.UpdateProductStockAndPricingAsync(
            uow, product.Id, newQuantity, newCost, retail, wholesale, nowUtc, cancellationToken);

        await stockMovements.AppendAsync(
            uow, product.Id, quantity, newQuantity, StockMovementReason.Purchase, purchaseId, userId,
            note: null, nowUtc, cancellationToken);

        await AuditAsync(audit, uow, product.Id, "quantity_on_hand", product.QuantityOnHand, newQuantity, userId, nowUtc, cancellationToken);
        await AuditAsync(audit, uow, product.Id, "cost_price", product.CostPrice, newCost, userId, nowUtc, cancellationToken);

        if (retail != product.RetailPrice)
        {
            await AuditAsync(audit, uow, product.Id, "retail_price", product.RetailPrice, retail, userId, nowUtc, cancellationToken);
        }

        if (wholesale != product.WholesalePrice)
        {
            await AuditAsync(audit, uow, product.Id, "wholesale_price", product.WholesalePrice, wholesale, userId, nowUtc, cancellationToken);
        }

        return new StockedLine(
            purchaseId, product.Id, product.Name, quantity, unitCost, total, newQuantity, newCost, retail, wholesale);
    }

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static Task AuditAsync<T>(
        IAuditWriter audit, IUnitOfWork uow, long productId, string field, T oldValue, T newValue, long userId,
        DateTime nowUtc, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            uow, "Product", productId, field,
            Convert.ToString(oldValue, CultureInfo.InvariantCulture),
            Convert.ToString(newValue, CultureInfo.InvariantCulture),
            "Purchase", userId, nowUtc, cancellationToken);
}
