using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>A customer's balance, read under a row lock.</summary>
public sealed record CustomerBalanceSnapshot
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public decimal OutstandingBalance { get; init; }
}

/// <summary>One line as it will be written, after the server has priced it.</summary>
public sealed record InvoiceItemToWrite(
    long ProductId,
    string ProductName,
    int Quantity,
    decimal UnitSalePrice,
    decimal LineDiscount,
    decimal UnitCostPrice,
    decimal LineTotal);

/// <summary>
/// The write side of selling. Every method takes the caller's transaction so the invoice, the
/// stock decrements and the ledger entry commit together or not at all (Principle IV).
/// </summary>
public interface IInvoiceWriteRepository
{
    /// <summary>
    /// Locks several products at once, ordered by id. A fixed lock order is what stops two
    /// multi-line sales that share products from deadlocking (research.md R4).
    /// </summary>
    Task<IReadOnlyList<ProductStockSnapshot>> LockProductsAsync(
        IUnitOfWork unitOfWork,
        IReadOnlyList<long> productIds,
        CancellationToken cancellationToken = default);

    Task<CustomerBalanceSnapshot?> LockCustomerAsync(
        IUnitOfWork unitOfWork, long customerId, CancellationToken cancellationToken = default);

    Task<long> InsertInvoiceAsync(
        IUnitOfWork unitOfWork,
        string invoiceNumber,
        long? customerId,
        DateTime invoiceDateUtc,
        SaleType saleType,
        decimal subtotal,
        decimal orderDiscount,
        decimal total,
        decimal amountPaid,
        decimal amountRemaining,
        PaymentMethod paymentMethod,
        string? paymentAccountNumber,
        string? paymentTransactionId,
        string? idempotencyKey,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task InsertInvoiceItemsAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        IReadOnlyList<InvoiceItemToWrite> items,
        CancellationToken cancellationToken = default);

    Task UpdateProductQuantityAsync(
        IUnitOfWork unitOfWork,
        long productId,
        int newQuantity,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task UpdateCustomerBalanceAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        decimal newBalance,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task InsertLedgerEntryAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        DateTime entryDateUtc,
        LedgerEntryType entryType,
        long? referenceId,
        decimal billAmount,
        decimal paidAmount,
        decimal balanceAfter,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the existing invoice id for a replayed idempotency key, if any.</summary>
    Task<long?> FindByIdempotencyKeyAsync(
        IUnitOfWork unitOfWork, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Reserves the next invoice number for the year, inside the transaction.</summary>
    Task<string> NextInvoiceNumberAsync(
        IUnitOfWork unitOfWork, int year, CancellationToken cancellationToken = default);
}

/// <summary>An invoice with its lines, for display.</summary>
public sealed record InvoiceWithItems(Invoice Invoice, IReadOnlyList<InvoiceItem> Items, string? CustomerName);

/// <summary>
/// One sale as the Invoices list shows it — enough to recognise a bill and hand it over, and no
/// more. No cost, no profit: this list is reachable by Staff, because handing a customer their
/// own receipt is counter work.
/// </summary>
public sealed record InvoiceListRow
{
    public long Id { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateTime InvoiceDateUtc { get; init; }

    /// <summary>Null for a walk-in — a sale that belongs to nobody.</summary>
    public long? CustomerId { get; init; }

    /// <summary>
    /// Null for a walk-in, deliberately, rather than an invented "Walk-in customer" label.
    /// "Nobody" is a fact; how to word it is the screen's choice, not the database's.
    /// </summary>
    public string? CustomerName { get; init; }

    public SaleType SaleType { get; init; }

    public decimal Total { get; init; }

    public decimal AmountPaid { get; init; }

    public decimal AmountRemaining { get; init; }

    /// <summary>Total less the value of any sale returns — what the sale is worth today.</summary>
    public decimal NetAmount { get; init; }

    public PaymentMethod PaymentMethod { get; init; }
}

public interface IInvoiceReadRepository
{
    Task<InvoiceWithItems?> FindByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches (or replaces) the screenshot backing a non-cash payment. A single-row update
    /// touching no money and no stock, so it needs none of the locking the sale itself does.
    /// </summary>
    Task SetPaymentProofPathAsync(
        long id, string paymentProofPath, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<InvoiceListRow> Items, int TotalItems)> SearchAsync(
        long? customerId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>Customer reads and writes.</summary>
public interface ICustomerRepository
{
    Task<(IReadOnlyList<Customer> Items, int TotalItems)> SearchAsync(
        string? search,
        bool withBalanceOnly,
        SaleType? saleType,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Customer?> FindByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(Customer customer, CancellationToken cancellationToken = default);

    Task UpdateAsync(Customer customer, CancellationToken cancellationToken = default);
}
