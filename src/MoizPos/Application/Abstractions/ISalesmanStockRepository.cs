namespace MoizPos.Application.Abstractions;

/// <summary>Why units went in or out of a salesman's bag.</summary>
public enum SalesmanStockReason
{
    Issued,
    Returned,
    Sold,
    CustomerReturn,
}

/// <summary>One product in a salesman's bag. Init-only: Dapper materialises it.</summary>
public sealed record SalesmanHoldingRow
{
    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int Quantity { get; init; }
}

/// <summary>One movement in or out of a salesman's bag.</summary>
public sealed record SalesmanStockMovementRow
{
    public long Id { get; init; }

    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public int ChangeQty { get; init; }

    public int ResultingQty { get; init; }

    /// <summary>The invoice for a sale, the sale return for a customer return.</summary>
    public long? ReferenceId { get; init; }

    public string? Reference { get; init; }

    public string? Note { get; init; }

    public string RecordedBy { get; init; } = string.Empty;

    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>
/// The stock salesmen carry. <b>Every write happens under the product rows' lock</b>
/// (<c>SELECT … FOR UPDATE</c> on <c>products</c>, ordered by id), and every read inside a write is
/// itself a locking read — a plain read would see the transaction's snapshot, which may predate an
/// issue committed while this one waited for the product lock.
/// </summary>
public interface ISalesmanStockRepository
{
    /// <summary>How many of each product ALL salesmen carry between them. Missing ids carry none.</summary>
    Task<IReadOnlyDictionary<long, int>> HeldBySalesmenAsync(
        IUnitOfWork unitOfWork, IReadOnlyList<long> productIds, CancellationToken cancellationToken = default);

    /// <summary>How many of each product this one salesman carries. Missing ids: none.</summary>
    Task<IReadOnlyDictionary<long, int>> HoldingAsync(
        IUnitOfWork unitOfWork, long userId, IReadOnlyList<long> productIds, CancellationToken cancellationToken = default);

    /// <summary>Sets what he carries of one product and records the movement that got it there.</summary>
    Task MoveAsync(
        IUnitOfWork unitOfWork, long userId, long productId, int change, int resulting, SalesmanStockReason reason,
        long? referenceId, string? note, long recordedByUserId, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// For DISPLAY only — how many of each product all salesmen carry, read outside any write.
    /// Never decide a sale from it; the write paths use the locking overload above.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> HeldBySalesmenForDisplayAsync(
        IReadOnlyList<long> productIds, CancellationToken cancellationToken = default);

    /// <summary>For DISPLAY only — how many of each product this salesman carries.</summary>
    Task<IReadOnlyDictionary<long, int>> HoldingForDisplayAsync(
        long userId, IReadOnlyList<long> productIds, CancellationToken cancellationToken = default);

    /// <summary>What he carries now — only products he holds at least one of.</summary>
    Task<IReadOnlyList<SalesmanHoldingRow>> HoldingForAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>His movements, newest first.</summary>
    Task<IReadOnlyList<SalesmanStockMovementRow>> MovementsForAsync(long userId, CancellationToken cancellationToken = default);
}
