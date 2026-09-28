using System.Globalization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

public sealed record CreateInvoiceLine
{
    public long ProductId { get; init; }

    public int Quantity { get; init; }

    /// <summary>As charged. May differ from the catalogue price at the shopkeeper's discretion.</summary>
    public decimal UnitSalePrice { get; init; }

    public decimal LineDiscount { get; init; }
}

public sealed record CreateInvoiceRequest
{
    public long? CustomerId { get; init; }

    /// <summary>Inline quick-create during a sale (FR-017). Used when CustomerId is absent.</summary>
    public NewCustomer? NewCustomer { get; init; }

    public decimal OrderDiscount { get; init; }

    public decimal AmountPaid { get; init; }

    public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.Cash;

    /// <summary>The customer's account, where a non-cash payment came from. Optional.</summary>
    public string? PaymentAccountNumber { get; init; }

    /// <summary>Their reference for that transfer. Optional.</summary>
    public string? PaymentTransactionId { get; init; }

    /// <summary>
    /// Counter sale or bulk sale. Retail unless the salesman says otherwise, because the counter
    /// is the normal case and an unset field must not silently reclassify the day's takings.
    /// </summary>
    public SaleType SaleType { get; init; } = SaleType.Retail;

    public IReadOnlyList<CreateInvoiceLine> Items { get; init; } = [];

    public string? IdempotencyKey { get; init; }
}

public sealed record NewCustomer(string Name, string? MobileNumber);

public sealed record CreateInvoiceResult(
    long InvoiceId,
    string InvoiceNumber,
    decimal Subtotal,
    decimal TotalDiscount,
    decimal Total,
    decimal AmountPaid,
    decimal AmountRemaining,
    long? CustomerId,
    decimal? CustomerBalance);

public interface IInvoiceService
{
    /// <summary>
    /// Records a sale. <paramref name="role"/> is passed in explicitly rather than read from any
    /// ambient principal, so the credit rule (FR-051) stays unit-testable without an HTTP
    /// context — and so it cannot be quietly bypassed by a caller that forgets to set one.
    /// </summary>
    Task<CreateInvoiceResult> CreateAsync(
        CreateInvoiceRequest request, long userId, UserRole role,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Selling.
///
/// <para>One transaction, in a deliberate order: lock every affected product row by id, verify
/// stock against the locked values, price the sale from scratch, then write. Locking first is
/// what makes "two sales for the last unit" resolve to exactly one success rather than
/// overselling (FR-006, research.md R4).</para>
///
/// <para>Client-supplied totals are never trusted — the server recomputes everything from the
/// line items it just read (FR-013, Principle IV).</para>
/// </summary>
public sealed class InvoiceService : IInvoiceService
{
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IInvoiceWriteRepository _invoices;
    private readonly ICustomerRepository _customers;
    private readonly IStockMovementWriter _stockMovements;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public InvoiceService(
        IUnitOfWorkFactory unitOfWorkFactory,
        IInvoiceWriteRepository invoices,
        ICustomerRepository customers,
        IStockMovementWriter stockMovements,
        IAuditWriter audit,
        IClock clock)
    {
        _unitOfWorkFactory = unitOfWorkFactory;
        _invoices = invoices;
        _customers = customers;
        _stockMovements = stockMovements;
        _audit = audit;
        _clock = clock;
    }

    public async Task<CreateInvoiceResult> CreateAsync(
        CreateInvoiceRequest request,
        long userId,
        UserRole role,
        CancellationToken cancellationToken = default)
    {
        if (request.Items.Count == 0)
        {
            throw new BusinessRuleViolationException("A sale must contain at least one line item.");
        }

        if (request.Items.Select(i => i.ProductId).Distinct().Count() != request.Items.Count)
        {
            throw new BusinessRuleViolationException(
                "The same product appears more than once. Combine it into a single line.");
        }

        var nowUtc = _clock.UtcNow;

        // A quick-created customer is committed before the sale: if the sale then fails, the shop
        // keeps a harmless new contact rather than losing the details the shopkeeper just typed.
        var customerId = request.CustomerId ?? await QuickCreateCustomerAsync(request, cancellationToken);

        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        if (request.IdempotencyKey is { Length: > 0 } key)
        {
            var existing = await _invoices.FindByIdempotencyKeyAsync(uow, key, cancellationToken);

            if (existing is not null)
            {
                // A replayed request — a double-tap, or a retry after a timeout. Return what was
                // already recorded instead of selling the same goods twice.
                throw new DuplicateInvoiceException(existing.Value);
            }
        }

        // Ordered by id so concurrent multi-line sales acquire locks in the same sequence.
        var productIds = request.Items.Select(i => i.ProductId).OrderBy(id => id).ToList();
        var products = await _invoices.LockProductsAsync(uow, productIds, cancellationToken);
        var byId = products.ToDictionary(p => p.Id);

        foreach (var productId in productIds)
        {
            if (!byId.ContainsKey(productId))
            {
                throw new NotFoundException("Product", productId);
            }
        }

        // Price the sale from the locked rows. Anything the client computed is discarded.
        var totals = InvoiceCalculator.Calculate(
            request.Items
                .Select(i => new InvoiceLineInput(i.Quantity, i.UnitSalePrice, i.LineDiscount))
                .ToList(),
            request.OrderDiscount,
            request.AmountPaid);

        // Stock and price checked across every line before writing anything: a sale is all or
        // nothing, so a problem on line three must not leave lines one and two deducted.
        foreach (var item in request.Items)
        {
            var product = byId[item.ProductId];

            // A product that has never been stocked has no selling price, and the counter would
            // quote zero for it. Refused here rather than trusted from the request, because the
            // client supplies the unit price and would happily sell an unpriced product at
            // whatever it last had in memory. Read from the LOCKED row, so a purchase committing
            // alongside this sale cannot make the answer stale.
            if (product.RetailPrice <= 0m)
            {
                throw new BusinessRuleViolationException(
                    $"'{product.Name}' has no selling price yet. Record a purchase for it first — " +
                    "that is what sets its price and puts it in stock.");
            }

            if (product.QuantityOnHand < item.Quantity)
            {
                throw new InsufficientStockException(
                    product.Name, product.QuantityOnHand, item.Quantity);
            }
        }

        if (totals.AmountRemaining > 0m && customerId is null)
        {
            throw new CustomerRequiredException(totals.AmountRemaining);
        }

        // Only the owner may let goods leave against a debt (FR-051, FR-052).
        //
        // Decided from the SERVER'S recomputed total, not from request.PaymentMethod and not from
        // request.AmountPaid: a sale labelled "Cash" whose payment falls short is still credit,
        // and the label is the one part of this a caller controls.
        //
        // Placed here deliberately — after pricing and the stock check, before the first write —
        // so a refusal leaves no invoice, no stock movement and no balance change (FR-055).
        if (totals.AmountRemaining > 0m && role != UserRole.Admin)
        {
            throw new CreditRequiresAdminException(totals.AmountRemaining);
        }

        // A cash sale carries no payment reference. Money counted into the drawer came from no
        // account, so a "reference" on it would be evidence of nothing — and allowing one would
        // quietly invite references on sales that never had a transfer. Same rule, and the same
        // reasoning, as the proof screenshot (feature 008).
        //
        // Checked here, before the first write, so a refusal leaves no invoice and no stock
        // movement rather than a sale with a nonsense reference attached.
        if (request.PaymentMethod == PaymentMethod.Cash &&
            (!string.IsNullOrWhiteSpace(request.PaymentAccountNumber) ||
             !string.IsNullOrWhiteSpace(request.PaymentTransactionId)))
        {
            throw new BusinessRuleViolationException(
                "A cash sale cannot carry a payment account or transaction reference.");
        }

        var invoiceNumber = await _invoices.NextInvoiceNumberAsync(uow, nowUtc.Year, cancellationToken);

        var invoiceId = await _invoices.InsertInvoiceAsync(
            uow, invoiceNumber, customerId, nowUtc, request.SaleType,
            totals.Subtotal, request.OrderDiscount, totals.Total,
            totals.AmountPaid, totals.AmountRemaining,
            request.PaymentMethod,
            Trimmed(request.PaymentAccountNumber),
            Trimmed(request.PaymentTransactionId),
            request.IdempotencyKey, userId, nowUtc, cancellationToken);

        // Each line snapshots the product's CURRENT cost. Under the shop's latest-cost rule a
        // later purchase overwrites products.cost_price; without this snapshot every historical
        // profit figure would silently change (FR-011c).
        var itemsToWrite = request.Items.Select((item, index) =>
        {
            var product = byId[item.ProductId];
            var line = totals.Lines[index];

            return new InvoiceItemToWrite(
                product.Id,
                product.Name,
                item.Quantity,
                item.UnitSalePrice,
                line.LineDiscount,
                product.CostPrice,
                line.LineTotal);
        }).ToList();

        await _invoices.InsertInvoiceItemsAsync(uow, invoiceId, itemsToWrite, cancellationToken);

        foreach (var item in request.Items)
        {
            var product = byId[item.ProductId];
            var newQuantity = StockRules.NextQuantity(
                product.QuantityOnHand, -item.Quantity, product.Name);

            await _invoices.UpdateProductQuantityAsync(
                uow, product.Id, newQuantity, nowUtc, cancellationToken);

            await _stockMovements.AppendAsync(
                uow, product.Id, -item.Quantity, newQuantity, StockMovementReason.Sale,
                invoiceId, userId, note: null, nowUtc, cancellationToken);

            await _audit.RecordAsync(
                uow, "Product", product.Id, "quantity_on_hand",
                product.QuantityOnHand.ToString(CultureInfo.InvariantCulture),
                newQuantity.ToString(CultureInfo.InvariantCulture),
                "Sale", userId, nowUtc, cancellationToken);
        }

        decimal? customerBalance = null;

        if (customerId is not null)
        {
            var customer = await _invoices.LockCustomerAsync(uow, customerId.Value, cancellationToken)
                ?? throw new NotFoundException("Customer", customerId.Value);

            // The whole bill lands in the ledger, along with whatever was paid at the counter,
            // so the register reads the way the shopkeeper's own book does (FR-020).
            var newBalance = LedgerBalanceCalculator.NextBalance(
                customer.OutstandingBalance, totals.Total, totals.AmountPaid);

            await _invoices.UpdateCustomerBalanceAsync(
                uow, customerId.Value, newBalance, nowUtc, cancellationToken);

            await _invoices.InsertLedgerEntryAsync(
                uow, customerId.Value, nowUtc, LedgerEntryType.Invoice, invoiceId,
                totals.Total, totals.AmountPaid, newBalance, userId, nowUtc, cancellationToken);

            await _audit.RecordAsync(
                uow, "Customer", customerId.Value, "outstanding_balance",
                customer.OutstandingBalance.ToString(CultureInfo.InvariantCulture),
                newBalance.ToString(CultureInfo.InvariantCulture),
                "Sale", userId, nowUtc, cancellationToken);

            customerBalance = newBalance;
        }

        await uow.CommitAsync(cancellationToken);

        return new CreateInvoiceResult(
            invoiceId, invoiceNumber, totals.Subtotal, totals.TotalDiscount, totals.Total,
            totals.AmountPaid, totals.AmountRemaining, customerId, customerBalance);
    }

    /// <summary>
    /// Blank and whitespace both mean "not given". Stored as NULL rather than an empty string so
    /// "no reference" is one fact in the column, not two that every later query must handle.
    /// </summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<long?> QuickCreateCustomerAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        if (request.NewCustomer is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.NewCustomer.Name))
        {
            throw new BusinessRuleViolationException("A customer name is required.");
        }

        return await _customers.CreateAsync(
            new Domain.Entities.Customer
            {
                Name = request.NewCustomer.Name.Trim(),
                MobileNumber = string.IsNullOrWhiteSpace(request.NewCustomer.MobileNumber)
                    ? null
                    : request.NewCustomer.MobileNumber.Trim(),
            },
            cancellationToken);
    }
}

/// <summary>
/// A replayed idempotency key. Carries the original invoice id so the caller can return the
/// sale that was already recorded rather than creating a second one.
/// </summary>
public sealed class DuplicateInvoiceException : DomainException
{
    public DuplicateInvoiceException(long existingInvoiceId)
        : base(ErrorCodes.BusinessRuleViolation, "This sale has already been recorded.") =>
        ExistingInvoiceId = existingInvoiceId;

    public long ExistingInvoiceId { get; }
}
