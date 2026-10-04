namespace MoizPos.Application.Abstractions;

/// <summary>A customer who owes the shop money. Init-only: Dapper materialises it.</summary>
public sealed record RecoveryCustomerRow
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? MobileNumber { get; init; }

    public bool IsUdhaarCustomer { get; init; }

    public decimal OutstandingBalance { get; init; }
}

/// <summary>One ledger movement of an owing customer, with the document it refers to.</summary>
public sealed record RecoveryMovementRow
{
    public long CustomerId { get; init; }

    public DateTime EntryDateUtc { get; init; }

    public string EntryType { get; init; } = string.Empty;

    public long? ReferenceId { get; init; }

    public string? ReferenceNumber { get; init; }

    public decimal BillAmount { get; init; }

    public decimal PaidAmount { get; init; }
}

public interface IRecoveryRepository
{
    /// <summary>Every active customer whose balance is above zero.</summary>
    Task<IReadOnlyList<RecoveryCustomerRow>> OwingCustomersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole ledger of these customers in one read, each customer's in the order it was
    /// written — what the oldest-first settlement needs.
    /// </summary>
    Task<IReadOnlyList<RecoveryMovementRow>> MovementsAsync(
        IReadOnlyList<long> customerIds, CancellationToken cancellationToken = default);
}
