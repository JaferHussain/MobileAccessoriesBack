using MoizPos.Application.Calculations;
using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>An invoice line as the return needs it, locked for update.</summary>
public sealed record InvoiceItemSnapshot
{
    public long Id { get; init; }

    public long InvoiceId { get; init; }

    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int Quantity { get; init; }

    public int ReturnedQty { get; init; }

    public decimal UnitSalePrice { get; init; }

    /// <summary>The cost recorded when the sale was made, not the product's cost today.</summary>
    public decimal UnitCostPrice { get; init; }

    public decimal LineDiscount { get; init; }

    /// <summary>
    /// What the line was billed at — <c>unit_sale_price x quantity</c> less its line discount,
    /// but BEFORE the order discount. Returns price against this, not against the unit price,
    /// so a discounted line refunds what was actually charged (<see cref="ReturnPricing"/>).
    /// </summary>
    public decimal LineTotal { get; init; }
}

/// <summary>The invoice being returned against, locked for update.</summary>
public sealed record InvoiceSnapshot
{
    public long Id { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public long? CustomerId { get; init; }

    /// <summary>The sum of the line totals, before the order discount.</summary>
    public decimal Subtotal { get; init; }

    /// <summary>Taken off the whole sale, so it belongs to no single line until it is spread.</summary>
    public decimal OrderDiscount { get; init; }

    public decimal Total { get; init; }

    public decimal AmountPaid { get; init; }

    public decimal AmountRemaining { get; init; }

    public decimal NetAmount { get; init; }
}

/// <summary>A purchase being returned against, locked for update.</summary>
public sealed record PurchaseSnapshot
{
    public long Id { get; init; }

    public long SupplierId { get; init; }

    public long ProductId { get; init; }

    public int Quantity { get; init; }

    public int ReturnedQty { get; init; }

    public decimal UnitCost { get; init; }
}

/// <summary>
/// One product line of a return, as it is written down. All three money figures are recorded:
/// what the item was billed at, what it is worth back, and the discount between them — the
/// adjustment the shopkeeper explains to the customer at the counter.
/// </summary>
/// <param name="UnitSalePrice">What the item was BILLED at per unit — 600.</param>
/// <param name="UnitRefundPrice">What one unit is worth BACK — 575.</param>
/// <param name="DiscountTotal">The adjustment for this whole line — 25.</param>
/// <param name="LineTotal">What is actually refunded: refund price x quantity.</param>
public sealed record SaleReturnItemToWrite(
    long InvoiceItemId,
    long ProductId,
    string ProductName,
    int Quantity,
    decimal UnitSalePrice,
    decimal UnitRefundPrice,
    decimal UnitCostPrice,
    decimal DiscountTotal,
    decimal LineTotal);

public interface IReturnWriteRepository
{
    Task<InvoiceSnapshot?> LockInvoiceAsync(
        IUnitOfWork unitOfWork, long invoiceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvoiceItemSnapshot>> LockInvoiceItemsAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        IReadOnlyList<long> invoiceItemIds,
        CancellationToken cancellationToken = default);

    Task<PurchaseSnapshot?> LockPurchaseAsync(
        IUnitOfWork unitOfWork, long purchaseId, CancellationToken cancellationToken = default);

    Task<long> InsertSaleReturnAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        string returnNumber,
        DateTime returnDateUtc,
        decimal totalAmount,
        decimal refundDue,
        string? reason,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task InsertSaleReturnItemsAsync(
        IUnitOfWork unitOfWork,
        long saleReturnId,
        IReadOnlyList<SaleReturnItemToWrite> items,
        CancellationToken cancellationToken = default);

    Task IncrementInvoiceItemReturnedAsync(
        IUnitOfWork unitOfWork,
        long invoiceItemId,
        int quantity,
        CancellationToken cancellationToken = default);

    Task ReduceInvoiceNetAmountAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        decimal newNetAmount,
        CancellationToken cancellationToken = default);

    Task<long> InsertPurchaseReturnAsync(
        IUnitOfWork unitOfWork,
        long purchaseId,
        long supplierId,
        long productId,
        string returnNumber,
        DateTime returnDateUtc,
        int quantity,
        decimal unitCost,
        decimal total,
        string? reason,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task IncrementPurchaseReturnedAsync(
        IUnitOfWork unitOfWork,
        long purchaseId,
        int quantity,
        CancellationToken cancellationToken = default);

    Task<string> NextReturnNumberAsync(
        IUnitOfWork unitOfWork, string prefix, int year, CancellationToken cancellationToken = default);
}

/// <summary>
/// One product line of a sale return, as the Returns screen lists it — general details plus the
/// stock effect (a return puts the quantity back on the shelf).
/// </summary>
public sealed record SaleReturnRow
{
    public long ReturnId { get; init; }

    public string ReturnNumber { get; init; } = string.Empty;

    public DateTime ReturnDateUtc { get; init; }

    public long InvoiceId { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public int Quantity { get; init; }

    /// <summary>What the goods were billed at — 600.</summary>
    public decimal BilledTotal { get; init; }

    /// <summary>The adjustment between billed and refunded — 25. Kept visible so the owner can
    /// see months later why 575 was handed back for a 600 item.</summary>
    public decimal DiscountTotal { get; init; }

    /// <summary>What was actually given back — 575.</summary>
    public decimal LineTotal { get; init; }

    /// <summary>Cash handed back, when the original sale was already settled (FR-028).</summary>
    public decimal RefundDue { get; init; }

    public string? Reason { get; init; }
}

/// <summary>One purchase return, as the Returns screen lists it — always one product, since a
/// purchase is recorded that way.</summary>
public sealed record PurchaseReturnRow
{
    public long ReturnId { get; init; }

    public string ReturnNumber { get; init; } = string.Empty;

    public DateTime ReturnDateUtc { get; init; }

    public long SupplierId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public int Quantity { get; init; }

    public decimal Total { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// The raw database row behind a "Return item" search — the invoice line plus the discount
/// figures needed to price it. It is NOT what the API sends: the controller maps it to
/// <see cref="ReturnableSaleLine"/>, which spells every amount out as a plain property.
/// </summary>
public sealed record ReturnableLineRow
{
    public long InvoiceId { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public long InvoiceItemId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int Quantity { get; init; }

    public int ReturnedQty { get; init; }

    /// <summary>What the line was billed at per unit, before any discount — shown struck through
    /// when a discount moved it, so the shopkeeper can see why the refund differs.</summary>
    public decimal UnitSalePrice { get; init; }

    /// <summary>The discount taken off this line itself.</summary>
    public decimal LineDiscount { get; init; }

    public decimal LineTotal { get; init; }

    /// <summary>The whole invoice, before and after its order discount — what spreads that
    /// discount proportionally across the lines.</summary>
    public decimal InvoiceSubtotal { get; init; }

    public decimal InvoiceTotal { get; init; }

    /// <summary>Still owed on the sale, which decides refund versus balance reduction.</summary>
    public decimal AmountRemaining { get; init; }

}

/// <summary>
/// What the counter is offered for one returnable sale line — every figure named in plain words
/// and spelled out as a real property.
///
/// <para><b>Every amount is a stored property, never a computed getter.</b> A getter is easy to
/// leave out of a response and impossible to see missing from the server side; when these three
/// were getters the Returns screen showed "Rs NaN" on every figure while every test on the
/// server still passed. <see cref="From"/> is the one place they are worked out.</para>
/// </summary>
public sealed record ReturnableSaleLine
{
    public long InvoiceId { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public long InvoiceItemId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    /// <summary>How many units were sold on this line.</summary>
    public int QuantitySold { get; init; }

    /// <summary>How many of them have already come back.</summary>
    public int QuantityReturned { get; init; }

    /// <summary>How many can still be returned — sold less already returned.</summary>
    public int QuantityAvailable { get; init; }

    /// <summary>What the receipt shows per unit, before any discount.</summary>
    public decimal UnitSalePrice { get; init; }

    /// <summary><b>What one unit is worth back.</b> The figure the return actually pays: the
    /// billed price less this line's share of the invoice's discount.</summary>
    public decimal RefundPerUnit { get; init; }

    /// <summary>Billed price less refund price. Zero on an undiscounted sale.</summary>
    public decimal DiscountPerUnit { get; init; }

    /// <summary>Returning everything still available on this line is worth this much.</summary>
    public decimal MaxRefund { get; init; }

    /// <summary>Still owed on the sale, which decides refund versus balance reduction.</summary>
    public decimal AmountRemaining { get; init; }

    /// <summary>The single place a search result's money is worked out, from the same
    /// <see cref="ReturnPricing"/> the write path charges at.</summary>
    public static ReturnableSaleLine From(ReturnableLineRow row)
    {
        var refundPerUnit = ReturnPricing.EffectiveUnitPrice(
            row.LineTotal, row.Quantity, row.InvoiceSubtotal, row.InvoiceTotal);

        var available = Math.Max(0, row.Quantity - row.ReturnedQty);

        return new ReturnableSaleLine
        {
            InvoiceId = row.InvoiceId,
            InvoiceNumber = row.InvoiceNumber,
            InvoiceItemId = row.InvoiceItemId,
            ProductName = row.ProductName,
            QuantitySold = row.Quantity,
            QuantityReturned = row.ReturnedQty,
            QuantityAvailable = available,
            UnitSalePrice = row.UnitSalePrice,
            RefundPerUnit = refundPerUnit,
            DiscountPerUnit = Math.Max(
                0m, Math.Round(row.UnitSalePrice - refundPerUnit, 2, MidpointRounding.AwayFromZero)),
            MaxRefund = ReturnPricing.RefundFor(
                row.LineTotal, row.Quantity, available, row.InvoiceSubtotal, row.InvoiceTotal),
            AmountRemaining = row.AmountRemaining,
        };
    }
}

public interface IReturnReadRepository
{
    /// <summary>
    /// Recent, still-returnable sale lines whose product name matches. Powers "Return item":
    /// search by name instead of hunting for the invoice number on a receipt.
    /// </summary>
    Task<IReadOnlyList<ReturnableLineRow>> FindReturnableLinesAsync(
        string search, int limit, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SaleReturnRow> Items, int TotalItems)> SearchSaleReturnsAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Scoped to one supplier when given — the everyday case, since a return is made to
    /// whichever supplier the goods came from.</summary>
    Task<(IReadOnlyList<PurchaseReturnRow> Items, int TotalItems)> SearchPurchaseReturnsAsync(
        long? supplierId, int page, int pageSize, CancellationToken cancellationToken = default);
}
