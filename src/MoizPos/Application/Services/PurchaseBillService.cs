using System.Globalization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>One product on a supplier's bill.</summary>
public sealed record PurchaseBillLine
{
    public long ProductId { get; init; }

    public int Quantity { get; init; }

    public decimal UnitCost { get; init; }

    /// <summary>Required the first time a product is stocked; omitted means "leave the price standing".</summary>
    public decimal? NewRetailPrice { get; init; }

    public decimal? NewWholesalePrice { get; init; }
}

/// <summary>Money paid to the supplier with a bill — or against it later.</summary>
public sealed record PurchaseBillPayment
{
    public decimal Amount { get; init; }

    public PaymentMethod PaymentMethod { get; init; }

    /// <summary>The shop's day it was really paid. The bill's date when left out.</summary>
    public DateOnly? PaidOn { get; init; }

    /// <summary>Which shop account paid — checked by the caller against the method.</summary>
    public long? ShopAccountId { get; init; }

    /// <summary>The cheque or transaction number.</summary>
    public string? Reference { get; init; }
}

public sealed record RecordPurchaseBillRequest
{
    public long SupplierId { get; init; }

    /// <summary>The number printed on the supplier's own bill.</summary>
    public string? BillNumber { get; init; }

    /// <summary>The shop's day the goods were bought. Today when left out.</summary>
    public DateOnly? BillDate { get; init; }

    public string? Note { get; init; }

    public IReadOnlyList<PurchaseBillLine> Lines { get; init; } = [];

    /// <summary>Paid with the bill, there and then. Null: pay later.</summary>
    public PurchaseBillPayment? Payment { get; init; }
}

/// <param name="PaymentId">What the payment's screenshot is attached to; null when nothing was paid.</param>
public sealed record RecordPurchaseBillResult(
    long BillId,
    decimal Total,
    decimal Paid,
    long? PaymentId,
    decimal NewSupplierPayable,
    IReadOnlyList<StockedLine> Lines);

public sealed record PayPurchaseBillResult(long PaymentId, decimal Due, decimal NewSupplierPayable);

public interface IPurchaseBillService
{
    Task<RecordPurchaseBillResult> RecordAsync(
        RecordPurchaseBillRequest request, long userId, CancellationToken cancellationToken = default);

    Task<PayPurchaseBillResult> PayAsync(
        long billId, PurchaseBillPayment payment, long userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A supplier's bill: several products bought together, and — when the owner pays there and then —
/// the payment, in ONE transaction. All of it, or none of it.
///
/// <para><b>Stock first, then the payment.</b> Every line is stocked (the latest-cost rule, prices,
/// movements — <see cref="PurchaseLineWriter"/>, the same code a single purchase runs), the
/// supplier's payable rises by the bill's total, and only then is the payment written against it.
/// A payment is never dated before its bill, so in the supplier's ledger the goods always come
/// first.</para>
///
/// <para><b>A payment states the day it was really made</b>, and it cannot be one that has not
/// happened, one before the goods were bought, or — for cash — a day whose drawer is already
/// counted: a closing is a snapshot, and cash slipped in behind it would never be seen.</para>
/// </summary>
public sealed class PurchaseBillService : IPurchaseBillService
{
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IPurchaseWriteRepository _purchases;
    private readonly IStockMovementWriter _stockMovements;
    private readonly IAuditWriter _audit;
    private readonly IDayClosingRepository _closings;
    private readonly PeriodResolver _periods;
    private readonly IClock _clock;

    public PurchaseBillService(
        IUnitOfWorkFactory unitOfWorkFactory,
        IPurchaseWriteRepository purchases,
        IStockMovementWriter stockMovements,
        IAuditWriter audit,
        IDayClosingRepository closings,
        PeriodResolver periods,
        IClock clock)
    {
        _unitOfWorkFactory = unitOfWorkFactory;
        _purchases = purchases;
        _stockMovements = stockMovements;
        _audit = audit;
        _closings = closings;
        _periods = periods;
        _clock = clock;
    }

    public async Task<RecordPurchaseBillResult> RecordAsync(
        RecordPurchaseBillRequest request, long userId, CancellationToken cancellationToken = default)
    {
        ValidateLines(request.Lines);

        var nowUtc = _clock.UtcNow;
        var today = ShopDay(nowUtc);
        var billDate = request.BillDate ?? today;

        if (billDate > today)
        {
            throw new BusinessRuleViolationException("A bill cannot be dated in the future.");
        }

        var total = PurchaseLineWriter.Round(request.Lines.Sum(line => PurchaseLineWriter.Round(line.UnitCost * line.Quantity)));

        if (request.Payment is { } payment)
        {
            if (payment.Amount > total)
            {
                throw new BusinessRuleViolationException(
                    $"The bill is Rs {total:N2}, so no more than that can be paid with it. " +
                    "Pay an older balance from the supplier's account.");
            }

            await ValidatePaymentAsync(payment, billDate, today, cancellationToken);
        }

        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        // Products first, ordered by id, then the supplier — the order a single purchase takes its
        // locks in, so a bill and a purchase racing over one product cannot deadlock.
        var products = new Dictionary<long, ProductStockSnapshot>();

        foreach (var productId in request.Lines.Select(line => line.ProductId).OrderBy(id => id))
        {
            products[productId] = await _purchases.LockProductAsync(uow, productId, cancellationToken)
                ?? throw new NotFoundException("Product", productId);
        }

        var supplier = await _purchases.LockSupplierAsync(uow, request.SupplierId, cancellationToken)
            ?? throw new NotFoundException("Supplier", request.SupplierId);

        var billId = await _purchases.InsertPurchaseBillAsync(
            uow, request.SupplierId, Trimmed(request.BillNumber), billDate, total, Trimmed(request.Note), userId, nowUtc,
            cancellationToken);

        // 1. The stock — every line, through the same writer a single purchase uses.
        var purchaseDateUtc = InstantOn(billDate, today, nowUtc);
        var stocked = new List<StockedLine>();

        foreach (var line in request.Lines)
        {
            stocked.Add(await PurchaseLineWriter.StockAsync(
                uow, _purchases, _stockMovements, _audit, products[line.ProductId], request.SupplierId, billId,
                line.Quantity, line.UnitCost, line.NewRetailPrice, line.NewWholesalePrice,
                purchaseDateUtc, userId, nowUtc, cancellationToken));
        }

        // 2. What the shop owes for it.
        var payable = PurchaseLineWriter.Round(supplier.PayableBalance + total);
        await _purchases.UpdateSupplierPayableAsync(uow, request.SupplierId, payable, nowUtc, cancellationToken);
        await AuditPayableAsync(uow, request.SupplierId, supplier.PayableBalance, payable, "PurchaseBill", userId, nowUtc, cancellationToken);

        // 3. Then — and only then — the payment made with it.
        long? paymentId = null;

        if (request.Payment is { } paid)
        {
            var before = payable;
            payable = PurchaseLineWriter.Round(payable - paid.Amount);

            paymentId = await _purchases.InsertSupplierPaymentAsync(
                uow, request.SupplierId, paid.Amount, paid.PaymentMethod, isOverpayment: payable < 0m,
                Trimmed(paid.Reference), paid.ShopAccountId, userId, nowUtc, cancellationToken,
                InstantOn(paid.PaidOn ?? billDate, today, nowUtc), billId);

            await _purchases.UpdateSupplierPayableAsync(uow, request.SupplierId, payable, nowUtc, cancellationToken);
            await AuditPayableAsync(uow, request.SupplierId, before, payable, "SupplierPayment", userId, nowUtc, cancellationToken);
        }

        await uow.CommitAsync(cancellationToken);

        return new RecordPurchaseBillResult(billId, total, request.Payment?.Amount ?? 0m, paymentId, payable, stocked);
    }

    public async Task<PayPurchaseBillResult> PayAsync(
        long billId, PurchaseBillPayment payment, long userId, CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNow;
        var today = ShopDay(nowUtc);

        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        var bill = await _purchases.LockPurchaseBillAsync(uow, billId, cancellationToken)
            ?? throw new NotFoundException("Purchase bill", billId);

        var supplier = await _purchases.LockSupplierAsync(uow, bill.SupplierId, cancellationToken)
            ?? throw new NotFoundException("Supplier", bill.SupplierId);

        var due = PurchaseLineWriter.Round(bill.Total - bill.Returned - bill.Paid);

        if (payment.Amount > due)
        {
            throw new BusinessRuleViolationException(
                due <= 0m
                    ? "This bill is already paid."
                    : $"Rs {due:N2} is still owed on this bill — no more than that can be paid against it.");
        }

        var paidOn = payment.PaidOn ?? today;
        await ValidatePaymentAsync(payment with { PaidOn = paidOn }, bill.BillDate, today, cancellationToken);

        var payable = PurchaseLineWriter.Round(supplier.PayableBalance - payment.Amount);

        var paymentId = await _purchases.InsertSupplierPaymentAsync(
            uow, bill.SupplierId, payment.Amount, payment.PaymentMethod, isOverpayment: payable < 0m,
            Trimmed(payment.Reference), payment.ShopAccountId, userId, nowUtc, cancellationToken,
            InstantOn(paidOn, today, nowUtc), billId);

        await _purchases.UpdateSupplierPayableAsync(uow, bill.SupplierId, payable, nowUtc, cancellationToken);
        await AuditPayableAsync(uow, bill.SupplierId, supplier.PayableBalance, payable, "SupplierPayment", userId, nowUtc, cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return new PayPurchaseBillResult(paymentId, PurchaseLineWriter.Round(due - payment.Amount), payable);
    }

    private static void ValidateLines(IReadOnlyList<PurchaseBillLine> lines)
    {
        if (lines.Count == 0)
        {
            throw new BusinessRuleViolationException("Add at least one product to the bill.");
        }

        if (lines.Select(line => line.ProductId).Distinct().Count() != lines.Count)
        {
            throw new BusinessRuleViolationException("The same product appears more than once. Combine it into a single line.");
        }

        if (lines.Any(line => line.Quantity <= 0))
        {
            throw new BusinessRuleViolationException("Each quantity must be greater than zero.");
        }

        if (lines.Any(line => line.UnitCost <= 0m))
        {
            throw new BusinessRuleViolationException("Each cost per unit must be greater than zero.");
        }

        if (lines.Any(line => line.NewRetailPrice is < 0m || line.NewWholesalePrice is < 0m))
        {
            throw new BusinessRuleViolationException("A selling price cannot be negative.");
        }
    }

    /// <summary>The rules every payment to a supplier on a bill obeys, wherever it is recorded.</summary>
    private async Task ValidatePaymentAsync(
        PurchaseBillPayment payment, DateOnly billDate, DateOnly today, CancellationToken cancellationToken)
    {
        if (payment.Amount <= 0m)
        {
            throw new BusinessRuleViolationException("The amount paid must be more than zero.");
        }

        if (payment.PaymentMethod is PaymentMethod.Credit or PaymentMethod.Partial)
        {
            throw new BusinessRuleViolationException("A supplier is paid in cash or by transfer.");
        }

        var paidOn = payment.PaidOn ?? billDate;

        if (paidOn > today)
        {
            throw new BusinessRuleViolationException("A payment cannot be dated in the future.");
        }

        if (paidOn < billDate)
        {
            throw new BusinessRuleViolationException(
                $"The bill is dated {billDate:d MMM yyyy}, so it cannot have been paid before that.");
        }

        // Cash leaves the drawer on the day it is paid. A day already closed is a snapshot of what
        // the drawer held — cash slipped in behind it would never be counted anywhere.
        if (payment.PaymentMethod == PaymentMethod.Cash
            && await _closings.FindByDateAsync(paidOn, cancellationToken) is not null)
        {
            throw new BusinessRuleViolationException(
                $"The drawer for {paidOn:d MMM yyyy} is already counted and closed, so a cash payment cannot be " +
                "dated then. Date it today, or record how it was really paid.");
        }
    }

    private DateOnly ShopDay(DateTime instantUtc) => DateOnly.FromDateTime(_periods.ToShopLocal(instantUtc));

    /// <summary>
    /// The instant to store for a shop day: now when it is today, otherwise midday that day — well
    /// inside it, so the day-close and every report place it on exactly that day.
    /// </summary>
    private DateTime InstantOn(DateOnly day, DateOnly today, DateTime nowUtc) =>
        day == today ? nowUtc : _periods.ResolveLocalDateRange(day, day).StartUtc.AddHours(12);

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task AuditPayableAsync(
        IUnitOfWork uow, long supplierId, decimal oldValue, decimal newValue, string action, long userId, DateTime nowUtc,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            uow, "Supplier", supplierId, "payable_balance",
            oldValue.ToString(CultureInfo.InvariantCulture), newValue.ToString(CultureInfo.InvariantCulture),
            action, userId, nowUtc, cancellationToken);
}
