using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>One of a salesman's sale lines, with everything its commission is worked out from.</summary>
public sealed record CommissionLineRow
{
    public long InvoiceItemId { get; init; }

    public long InvoiceId { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateTime InvoiceDateUtc { get; init; }

    public long? CustomerId { get; init; }

    public string? CustomerName { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int Quantity { get; init; }

    public int ReturnedQty { get; init; }

    public decimal LineTotal { get; init; }

    /// <summary>The owner's price at the moment of sale.</summary>
    public decimal BaseUnitPrice { get; init; }

    public decimal CommissionRate { get; init; }

    public decimal InvoiceSubtotal { get; init; }

    public decimal InvoiceTotal { get; init; }

    /// <summary>What the sale is worth now — the total less anything returned.</summary>
    public decimal InvoiceNet { get; init; }

    public decimal InvoiceAmountPaid { get; init; }
}

/// <summary>Something a customer owes: a sale, or an amount brought forward from the register.</summary>
public sealed record CustomerDebtRow
{
    public long CustomerId { get; init; }

    /// <summary>The invoice id for a sale; a negative ledger id for an amount brought forward.</summary>
    public long DebtId { get; init; }

    public DateTime OnDateUtc { get; init; }

    public decimal Amount { get; init; }

    public decimal PaidAtSale { get; init; }
}

/// <summary>Money a customer paid later.</summary>
public sealed record CustomerCreditRow
{
    public long CustomerId { get; init; }

    public DateTime OnDateUtc { get; init; }

    public decimal Amount { get; init; }
}

/// <summary>Commission the owner has paid a salesman.</summary>
public sealed record CommissionPayoutRow
{
    public long Id { get; init; }

    public decimal Amount { get; init; }

    public string PaymentMethod { get; init; } = string.Empty;

    public string? Note { get; init; }

    public DateTime PaidAtUtc { get; init; }

    public string RecordedBy { get; init; } = string.Empty;
}

public interface ICommissionRepository
{
    /// <summary>Every line this salesman sold that earns commission — those carrying an owner's price.</summary>
    Task<IReadOnlyList<CommissionLineRow>> LinesAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>Everything the given customers owe, from every seller — payments settle the oldest first.</summary>
    Task<IReadOnlyList<CustomerDebtRow>> DebtsAsync(
        IReadOnlyCollection<long> customerIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerCreditRow>> CreditsAsync(
        IReadOnlyCollection<long> customerIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommissionPayoutRow>> PayoutsAsync(long userId, CancellationToken cancellationToken = default);

    Task<long> InsertPayoutAsync(
        long userId, decimal amount, PaymentMethod method, string? note, long recordedByUserId, DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
