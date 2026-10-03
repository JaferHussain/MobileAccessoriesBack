using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>One row of a customer's ledger, as the screen shows it.</summary>
public sealed record LedgerEntryRow
{
    public long Id { get; init; }

    public DateTime EntryDateUtc { get; init; }

    public LedgerEntryType EntryType { get; init; }

    public long? ReferenceId { get; init; }

    /// <summary>The invoice or receipt number this entry refers to.</summary>
    public string? ReferenceNumber { get; init; }

    public decimal BillAmount { get; init; }

    public decimal PaidAmount { get; init; }

    public decimal BalanceAfter { get; init; }

    /// <summary>
    /// Why this entry exists. Carried by a correction to a carried-forward amount (FR-071),
    /// which has to be visible to whoever reads the ledger — not only in the audit trail.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>How the sale or payment was paid. Null for every other kind of entry.</summary>
    public string? PaymentMethod { get; init; }

    /// <summary>Whether a proof screenshot is attached to that sale or payment.</summary>
    public bool HasProof { get; init; }
}

/// <summary>Totals shown on a customer's profile (FR-023).</summary>
public sealed record CustomerSummaryRow
{
    public decimal TotalPurchased { get; init; }

    public decimal TotalPaid { get; init; }

    public decimal TotalOutstanding { get; init; }

    public int InvoiceCount { get; init; }
}

public interface ILedgerRepository
{
    Task<(IReadOnlyList<LedgerEntryRow> Items, int TotalItems)> ListForCustomerAsync(
        long customerId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CustomerSummaryRow> SummaryForCustomerAsync(
        long customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every movement in a customer's account, in the order it was written — what the due-date
    /// rule needs to find the oldest purchase still unpaid.
    /// </summary>
    /// <param name="throughPaymentId">
    /// Stop at the entry this payment wrote, so a receipt states the due date as it stood when the
    /// money was taken — the same reason it states that entry's balance and not today's. Null for
    /// the whole ledger.
    /// </param>
    Task<IReadOnlyList<LedgerMovementRow>> MovementsAsync(
        long customerId, long? throughPaymentId, CancellationToken cancellationToken = default);
}

/// <summary>One ledger movement, with the instant it was written.</summary>
public sealed record LedgerMovementRow
{
    public DateTime EntryDateUtc { get; init; }

    public decimal BillAmount { get; init; }

    public decimal PaidAmount { get; init; }
}

/// <summary>Write side of receiving money from a customer.</summary>
public interface ICustomerPaymentWriteRepository
{
    Task<CustomerBalanceSnapshot?> LockCustomerAsync(
        IUnitOfWork unitOfWork, long customerId, CancellationToken cancellationToken = default);

    Task<long> InsertPaymentAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        string receiptNumber,
        decimal amount,
        PaymentMethod paymentMethod,
        bool isOverpayment,
        string? note,
        long userId,
        DateTime nowUtc,
        bool inField,
        CancellationToken cancellationToken = default);

    Task UpdateBalanceAsync(
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
        string? note = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a customer's carried-forward figure and balance together under a row lock.
    ///
    /// Separate from <see cref="LockCustomerAsync"/> rather than widening the shared snapshot:
    /// the other caller does not select this column, and a property that is silently null on one
    /// code path is the kind of trap this codebase documents rather than creates.
    /// </summary>
    Task<(bool Exists, decimal? OpeningBalance, decimal OutstandingBalance)>
        LockForOpeningBalanceAsync(
            IUnitOfWork unitOfWork, long customerId, CancellationToken cancellationToken = default);

    /// <summary>Writes the carried-forward figure. The balance moves separately, by the difference.</summary>
    Task SetOpeningBalanceAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        decimal openingBalance,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<string> NextReceiptNumberAsync(
        IUnitOfWork unitOfWork, int year, CancellationToken cancellationToken = default);
}
