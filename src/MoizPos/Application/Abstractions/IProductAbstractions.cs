using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>Paging and filtering for a product listing.</summary>
public sealed record ProductQuery
{
    /// <summary>
    /// The text as typed. Used only for the exact barcode comparison (FR-080); word matching reads
    /// <see cref="SearchWords"/>.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// The normalised words from <c>ProductSearchTerms</c>. Every one must match (FR-074). Set by
    /// the service, never by a controller, so the rules in one place decide what is searched.
    /// </summary>
    public IReadOnlyList<string> SearchWords { get; init; } = [];

    public long? CategoryId { get; init; }

    public long? BrandId { get; init; }

    /// <summary>
    /// Which price the caller is selling at. A wholesale sale is quoted the product's wholesale
    /// price; anything else is quoted the counter price.
    ///
    /// <para>Resolved here rather than by handing the client both prices, because "wholesale" is
    /// one of the property names Staff DTOs may not carry (FR-040) — the salesman is told the
    /// price to charge for the sale they are making, and cost stays out of reach either way.</para>
    /// </summary>
    public SaleType SaleType { get; init; } = SaleType.Retail;

    /// <summary>
    /// Only products whose brand the owner has marked local (FR-087). An unbranded product is never
    /// local — a brand is what carries the flag.
    /// </summary>
    public bool LocalOnly { get; init; }

    public bool LowStockOnly { get; init; }

    public bool IncludeInactive { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

/// <summary>A product row joined with its supplier name, before role-based projection.</summary>
public sealed record ProductRow
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public long CategoryId { get; init; }

    /// <summary>Joined from the categories table, so a rename shows everywhere at once.</summary>
    public string Category { get; init; } = string.Empty;

    public long? BrandId { get; init; }

    public string? Brand { get; init; }

    /// <summary>False for an imported brand and for a product with no brand at all.</summary>
    public bool BrandIsLocal { get; init; }

    public string? Model { get; init; }

    public string? Barcode { get; init; }

    public string? ImagePath { get; init; }

    public decimal CostPrice { get; init; }

    public decimal WholesalePrice { get; init; }

    public decimal RetailPrice { get; init; }

    public decimal SalePrice { get; init; }

    public int QuantityOnHand { get; init; }

    public int MinStockThreshold { get; init; }

    public long? SupplierId { get; init; }

    public string? SupplierName { get; init; }

    public bool IsActive { get; init; }
}

public interface IProductRepository
{
    Task<(IReadOnlyList<ProductRow> Items, int TotalItems)> SearchAsync(
        ProductQuery query,
        CancellationToken cancellationToken = default);

    Task<ProductRow?> FindByIdAsync(
        long id,
        SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default);

    Task<ProductRow?> FindByBarcodeAsync(
        string barcode,
        SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default);

    Task<long> CreateAsync(Product product, CancellationToken cancellationToken = default);

    Task UpdateAsync(Product product, CancellationToken cancellationToken = default);

    Task DeactivateAsync(long id, CancellationToken cancellationToken = default);

    Task SetImagePathAsync(long id, string imagePath, CancellationToken cancellationToken = default);

    Task<bool> BarcodeExistsAsync(
        string barcode,
        long? excludingProductId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads and appends the append-only stock movement history.</summary>
public interface IStockMovementRepository
{
    Task<(IReadOnlyList<StockMovement> Items, int TotalItems)> ListForProductAsync(
        long productId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<StockMovement?> LatestForProductAsync(
        long productId,
        CancellationToken cancellationToken = default);
}

/// <summary>Supplier reads and writes.</summary>
public interface ISupplierRepository
{
    Task<(IReadOnlyList<Supplier> Items, int TotalItems)> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Supplier?> FindByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(Supplier supplier, CancellationToken cancellationToken = default);

    Task UpdateAsync(Supplier supplier, CancellationToken cancellationToken = default);
}

/// <summary>One row of a supplier's purchase/payment history with its running payable.</summary>
public sealed record SupplierLedgerRow
{
    public DateTime EntryDateUtc { get; init; }

    public string EntryType { get; init; } = string.Empty;

    public long ReferenceId { get; init; }

    public string? Description { get; init; }

    public decimal PurchaseAmount { get; init; }

    public decimal PaymentAmount { get; init; }
}

public interface IPurchaseRepository
{
    Task<(IReadOnlyList<Purchase> Items, int TotalItems)> SearchAsync(
        long? supplierId,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? productSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Purchase?> FindByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierLedgerRow>> LedgerForSupplierAsync(
        long supplierId,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>Writes the audit trail. Always called inside the mutating transaction (FR-041).</summary>
public interface IAuditWriter
{
    Task RecordAsync(
        IUnitOfWork unitOfWork,
        string entityType,
        long entityId,
        string fieldName,
        string? oldValue,
        string? newValue,
        string action,
        long userId,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>Appends a stock movement inside an existing transaction.</summary>
public interface IStockMovementWriter
{
    Task<long> AppendAsync(
        IUnitOfWork unitOfWork,
        long productId,
        int changeQty,
        int resultingQty,
        StockMovementReason reason,
        long? referenceId,
        long userId,
        string? note,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default);
}
