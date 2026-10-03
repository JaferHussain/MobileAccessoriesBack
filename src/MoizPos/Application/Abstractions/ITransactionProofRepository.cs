using MoizPos.Application.Calculations;

namespace MoizPos.Application.Abstractions;

/// <summary>A transaction that may carry a proof: how its money moved, and what is attached.</summary>
public sealed record ProofTarget
{
    /// <summary>
    /// As stored: a payment method (<c>Cash</c>, <c>BankTransfer</c>…), an expense's source
    /// (<c>Till</c>/<c>Bank</c>), or null for a return that refunded nothing.
    /// </summary>
    public string? Method { get; init; }

    public string? ProofPath { get; init; }
}

/// <summary>
/// One non-cash transaction with no proof attached yet — a line on the owner's "Proof missing"
/// list. Init-only properties: Dapper materialises it.
/// </summary>
public sealed record MissingProofRow
{
    public ProofKind Kind { get; init; }

    /// <summary>The id the proof is attached to — invoice, payment, return or expense.</summary>
    public long ReferenceId { get; init; }

    /// <summary>The number the shop knows it by: invoice, receipt or return number, or a category.</summary>
    public string? Reference { get; init; }

    public DateTime EntryDateUtc { get; init; }

    /// <summary>Who the money came from or went to. Null for a walk-in sale.</summary>
    public string? Party { get; init; }

    public string? Method { get; init; }

    public decimal Amount { get; init; }
}

/// <summary>Reads and records the proof on every kind of transaction that can carry one.</summary>
public interface ITransactionProofRepository
{
    /// <summary>Null when no such transaction exists.</summary>
    Task<ProofTarget?> FindAsync(ProofKind kind, long id, CancellationToken cancellationToken = default);

    Task SetProofPathAsync(ProofKind kind, long id, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every non-cash transaction still without a proof, newest first. <paramref name="fromUtc"/>
    /// and <paramref name="toUtc"/> are optional bounds, half-open.
    /// </summary>
    Task<IReadOnlyList<MissingProofRow>> MissingAsync(
        DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken = default);
}
