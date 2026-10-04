namespace MoizPos.Application.Abstractions;

/// <summary>A supplier's bill as the Purchases screen lists it. Init-only: Dapper materialises it.</summary>
public sealed record PurchaseBillRow
{
    public long Id { get; init; }

    public long SupplierId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public string? BillNumber { get; init; }

    public DateOnly BillDate { get; init; }

    public decimal Total { get; init; }

    /// <summary>Paid against this bill, with it or later.</summary>
    public decimal Paid { get; init; }

    /// <summary>The value of its goods sent back to the supplier.</summary>
    public decimal Returned { get; init; }

    public int ItemCount { get; init; }

    /// <summary>A photo of the supplier's bill is attached. Never where it is stored.</summary>
    public bool HasBillImage { get; init; }

    public string? Note { get; init; }

    public string RecordedBy { get; init; } = string.Empty;
}

/// <summary>One product on a bill.</summary>
public sealed record PurchaseBillLineRow
{
    public long PurchaseId { get; init; }

    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int Quantity { get; init; }

    public decimal UnitCost { get; init; }

    public decimal Total { get; init; }

    public int ReturnedQty { get; init; }
}

/// <summary>One payment made against a bill.</summary>
public sealed record PurchaseBillPaymentRow
{
    public long Id { get; init; }

    public decimal Amount { get; init; }

    public string PaymentMethod { get; init; } = string.Empty;

    public DateTime PaymentDateUtc { get; init; }

    public string? Note { get; init; }

    public bool HasProof { get; init; }
}

public interface IPurchaseBillRepository
{
    /// <summary>Bills newest first; one supplier's when <paramref name="supplierId"/> is given.</summary>
    Task<IReadOnlyList<PurchaseBillRow>> ListAsync(long? supplierId, int limit, CancellationToken cancellationToken = default);

    Task<PurchaseBillRow?> FindAsync(long billId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseBillLineRow>> LinesAsync(long billId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseBillPaymentRow>> PaymentsAsync(long billId, CancellationToken cancellationToken = default);
}
