using System.Globalization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

public sealed record RecordPurchaseRequest
{
    public long SupplierId { get; init; }

    public long ProductId { get; init; }

    public decimal UnitCost { get; init; }

    public int Quantity { get; init; }

    public DateTime? PurchaseDateUtc { get; init; }

    /// <summary>
    /// What a walk-in pays. Writes <c>retail_price</c> — the column a retail sale is quoted from.
    ///
    /// <para><b>Required the first time a product is stocked</b> — that is the moment a
    /// catalogue entry becomes something sellable, and a product on the shelf with no price is
    /// the one state the counter cannot handle. On a repeat purchase it is optional: omit it and
    /// the current price stands.</para>
    ///
    /// <para>When supplied it applies to ALL remaining stock, including units bought earlier at
    /// a different cost (FR-011d) — old stock sells at today's price.</para>
    /// </summary>
    public decimal? NewRetailPrice { get; init; }

    /// <summary>
    /// What a bulk buyer pays. Optional even on a first purchase: a shop that does not sell
    /// wholesale should not be made to invent a second price, and a wholesale sale falls back to
    /// the retail price when none is set.
    /// </summary>
    public decimal? NewWholesalePrice { get; init; }
}

public sealed record RecordPurchaseResult(
    long PurchaseId,
    int NewQuantityOnHand,
    decimal NewCostPrice,
    decimal NewSupplierPayable,
    decimal NewRetailPrice,
    decimal NewWholesalePrice);

/// <param name="PaymentId">What a proof of the transfer is attached to.</param>
public sealed record SupplierPaymentResult(long PaymentId, decimal NewPayable);

public sealed record RecordSupplierPaymentRequest
{
    public long SupplierId { get; init; }

    public decimal Amount { get; init; }

    public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.Cash;

    public string? Note { get; init; }

    /// <summary>Which shop account paid — checked by the caller against the method.</summary>
    public long? ShopAccountId { get; init; }

    public bool ConfirmOverpayment { get; init; }
}

public interface IPurchaseService
{
    Task<RecordPurchaseResult> RecordPurchaseAsync(
        RecordPurchaseRequest request,
        long userId,
        CancellationToken cancellationToken = default);

    Task<SupplierPaymentResult> RecordSupplierPaymentAsync(
        RecordSupplierPaymentRequest request,
        long userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Purchasing.
///
/// <para><b>The costing rule.</b> The shop owner chose <i>latest purchase cost</i>: when stock is
/// bought, that purchase's unit cost replaces the product's cost for every unit on hand — not a
/// weighted average. Buying 10 at 800, selling 5, then buying 10 at 850 leaves all 15 units
/// costed at 850, not 825 (FR-011a). Sales already recorded keep the cost they snapshotted, so
/// history is never rewritten (FR-011c).</para>
///
/// <para>Recording a purchase is one transaction doing six things (data-model.md §11). All of
/// it, or none of it.</para>
/// </summary>
public sealed class PurchaseService : IPurchaseService
{
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IPurchaseWriteRepository _purchases;
    private readonly IStockMovementWriter _stockMovements;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public PurchaseService(
        IUnitOfWorkFactory unitOfWorkFactory,
        IPurchaseWriteRepository purchases,
        IStockMovementWriter stockMovements,
        IAuditWriter audit,
        IClock clock)
    {
        _unitOfWorkFactory = unitOfWorkFactory;
        _purchases = purchases;
        _stockMovements = stockMovements;
        _audit = audit;
        _clock = clock;
    }

    public async Task<RecordPurchaseResult> RecordPurchaseAsync(
        RecordPurchaseRequest request,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (request.Quantity <= 0)
        {
            throw new BusinessRuleViolationException("Purchase quantity must be greater than zero.");
        }

        if (request.UnitCost <= 0m)
        {
            throw new BusinessRuleViolationException("Purchase unit cost must be greater than zero.");
        }

        if (request.NewRetailPrice is < 0m)
        {
            throw new BusinessRuleViolationException("Retail price cannot be negative.");
        }

        if (request.NewWholesalePrice is < 0m)
        {
            throw new BusinessRuleViolationException("Wholesale price cannot be negative.");
        }

        var nowUtc = _clock.UtcNow;
        var purchaseDate = request.PurchaseDateUtc ?? nowUtc;

        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        // Locked in a fixed order (product then supplier) so concurrent purchases cannot deadlock.
        var product = await _purchases.LockProductAsync(uow, request.ProductId, cancellationToken)
            ?? throw new NotFoundException("Product", request.ProductId);

        var supplier = await _purchases.LockSupplierAsync(uow, request.SupplierId, cancellationToken)
            ?? throw new NotFoundException("Supplier", request.SupplierId);

        // 1–3, 5–6: the purchase, stock, cost, prices, movement and product audits — the same
        // writer a purchase bill uses for each of its lines.
        var line = await PurchaseLineWriter.StockAsync(
            uow, _purchases, _stockMovements, _audit, product, request.SupplierId, purchaseBillId: null,
            request.Quantity, request.UnitCost, request.NewRetailPrice, request.NewWholesalePrice,
            purchaseDate, userId, nowUtc, cancellationToken);

        // 4. What the shop now owes.
        var newPayable = Round(supplier.PayableBalance + line.Total);

        await _purchases.UpdateSupplierPayableAsync(
            uow, request.SupplierId, newPayable, nowUtc, cancellationToken);

        await AuditAsync(uow, "Supplier", request.SupplierId, "payable_balance",
            supplier.PayableBalance, newPayable, "Purchase", userId, nowUtc, cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return new RecordPurchaseResult(
            line.PurchaseId, line.NewQuantityOnHand, line.NewCostPrice, newPayable, line.NewRetailPrice, line.NewWholesalePrice);
    }

    public async Task<SupplierPaymentResult> RecordSupplierPaymentAsync(
        RecordSupplierPaymentRequest request,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (request.Amount <= 0m)
        {
            throw new BusinessRuleViolationException("Payment amount must be greater than zero.");
        }

        var nowUtc = _clock.UtcNow;

        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        var supplier = await _purchases.LockSupplierAsync(uow, request.SupplierId, cancellationToken)
            ?? throw new NotFoundException("Supplier", request.SupplierId);

        // Paying more than is owed must be deliberate, never an accident (FR-009).
        if (request.Amount > supplier.PayableBalance && !request.ConfirmOverpayment)
        {
            throw new OverpaymentNotConfirmedException(request.Amount, supplier.PayableBalance);
        }

        var newPayable = Round(supplier.PayableBalance - request.Amount);

        var paymentId = await _purchases.InsertSupplierPaymentAsync(
            uow, request.SupplierId, request.Amount, request.PaymentMethod,
            isOverpayment: newPayable < 0m, request.Note, request.ShopAccountId, userId, nowUtc, cancellationToken);

        await _purchases.UpdateSupplierPayableAsync(
            uow, request.SupplierId, newPayable, nowUtc, cancellationToken);

        await AuditAsync(uow, "Supplier", request.SupplierId, "payable_balance",
            supplier.PayableBalance, newPayable, "SupplierPayment", userId, nowUtc, cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return new SupplierPaymentResult(paymentId, newPayable);
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private Task AuditAsync<T>(
        IUnitOfWork uow, string entityType, long entityId, string field,
        T oldValue, T newValue, string action, long userId, DateTime nowUtc,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            uow, entityType, entityId, field,
            Convert.ToString(oldValue, CultureInfo.InvariantCulture),
            Convert.ToString(newValue, CultureInfo.InvariantCulture),
            action, userId, nowUtc, cancellationToken);
}
