using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>
/// One movement of the cash a field salesman carries: taken at a sale, taken back as udhaar,
/// handed back as a refund, or handed over to the shop. Init-only properties: Dapper materialises it.
/// </summary>
public sealed record SalesmanCashMovementRow
{
    /// <summary>Sale, Recovery, Refund or Handover.</summary>
    public string Kind { get; init; } = string.Empty;

    public long ReferenceId { get; init; }

    public string? Reference { get; init; }

    public DateTime EntryDateUtc { get; init; }

    /// <summary>Always positive; <see cref="Kind"/> says which way it moved his cash.</summary>
    public decimal Amount { get; init; }

    /// <summary>A handover's method — cash into the drawer, or into a shop account.</summary>
    public string? Method { get; init; }

    /// <summary>The customer, or who received a handover and any note.</summary>
    public string? Detail { get; init; }
}

public interface ISalesmanCashRepository
{
    /// <summary>
    /// Every movement of this salesman's cash: his CASH sales and recoveries in the field, his cash
    /// refunds in the field, and what he has handed over. Transfers never passed through his hands.
    /// </summary>
    Task<IReadOnlyList<SalesmanCashMovementRow>> MovementsAsync(long userId, CancellationToken cancellationToken = default);

    Task<long> InsertHandoverAsync(
        long userId, decimal amount, PaymentMethod method, string? note, long receivedByUserId, DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
