using System.Text.Json.Serialization;
using FluentValidation;

namespace MoizPos.Application.Contracts.Products;

/// <summary>
/// The product as a Staff principal sees it.
///
/// This type has NO cost, profit or margin property AT ALL — not nulled, absent. FR-040 requires
/// that Staff cannot obtain cost data by any route, and a shared DTO with a conditional null is
/// one forgotten `if` away from leaking. An architecture test asserts this stays true.
///
/// <para><b>Serialization note.</b> System.Text.Json writes the DECLARED type's properties, so
/// without <see cref="JsonDerivedTypeAttribute"/> an Admin response typed as ProductStaffDto
/// silently drops CostPrice and the Admin sees the Staff shape. This attribute makes the
/// serializer use the runtime type. Removing it does not break the build — it quietly hides
/// cost data from the owner.</para>
/// </summary>
[JsonDerivedType(typeof(ProductAdminDto))]
public record ProductStaffDto
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public long CategoryId { get; init; }

    /// <summary>The category's name, resolved for display. Staff read it; only Admin edits it.</summary>
    public string Category { get; init; } = string.Empty;

    public long? BrandId { get; init; }

    public string? Brand { get; init; }

    /// <summary>
    /// Whether this product's brand is marked local. Not cost, margin or profit, so it is safe on the
    /// Staff shape; false for an unbranded product.
    /// </summary>
    public bool BrandIsLocal { get; init; }

    public string? Model { get; init; }

    public string? Barcode { get; init; }

    public string? ImagePath { get; init; }

    public decimal SalePrice { get; init; }

    public int QuantityOnHand { get; init; }

    public bool IsLowStock { get; init; }

    public bool IsActive { get; init; }
}

/// <summary>The product as an Admin sees it: everything, including what it cost.</summary>
public sealed record ProductAdminDto : ProductStaffDto
{
    public decimal CostPrice { get; init; }

    public decimal WholesalePrice { get; init; }

    public decimal RetailPrice { get; init; }

    public int MinStockThreshold { get; init; }

    public long? SupplierId { get; init; }

    public string? SupplierName { get; init; }
}

public sealed record ProductUpsertRequest
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Chosen from the Categories module. Free text is no longer accepted.</summary>
    public long CategoryId { get; init; }

    /// <summary>
    /// Chosen from the Brands module. <b>Required</b> since 0027 — goods with no well-known maker
    /// are filed under the shop's own general "Local" brand rather than left unbranded, which is
    /// what lets the form ask for the brand first and offer only that brand's categories.
    /// </summary>
    public long BrandId { get; init; }

    public string? Model { get; init; }

    public string? Barcode { get; init; }

    public int MinStockThreshold { get; init; }

    public long? SupplierId { get; init; }

    // NO PRICES AND NO QUANTITY, deliberately.
    //
    // A product is a catalogue entry — what the thing IS. What it costs, what it sells for and
    // how many are on the shelf all arrive with the first delivery, through a purchase, and are
    // the purchase's to set. Accepting them here would give the shop two ways to price an item
    // and two ways to set its stock, and the two would disagree the first time anyone used the
    // wrong one. A product therefore starts at zero stock and no price, and is unsellable until
    // it has been stocked.
}

public sealed record AdjustStockRequest
{
    public int NewQuantity { get; init; }

    public string Note { get; init; } = string.Empty;
}

public sealed record StockMovementDto
{
    public long Id { get; init; }

    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int ChangeQty { get; init; }

    public int ResultingQty { get; init; }

    public string Reason { get; init; } = string.Empty;

    public long? ReferenceId { get; init; }

    public string? Note { get; init; }

    public string UserName { get; init; } = string.Empty;

    public DateTime CreatedAtUtc { get; init; }
}

public sealed class ProductUpsertValidator : AbstractValidator<ProductUpsertRequest>
{
    public ProductUpsertValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Product name is required.")
            .MaximumLength(150).WithMessage("Product name cannot exceed 150 characters.");

        // The category must exist; that it exists is checked by the service, which can look it up.
        RuleFor(x => x.CategoryId)
            .GreaterThan(0).WithMessage("A category must be selected.");

        // That the brand exists, is in use, and carries the chosen category is the service's
        // call — it can look all three up. This only catches an absent or nonsense id.
        RuleFor(x => x.BrandId)
            .GreaterThan(0).WithMessage("A brand must be selected.");

        RuleFor(x => x.Model)
            .MaximumLength(80).WithMessage("Model cannot exceed 80 characters.");

        RuleFor(x => x.Barcode)
            .MaximumLength(64).WithMessage("Barcode cannot exceed 64 characters.");

        RuleFor(x => x.MinStockThreshold)
            .GreaterThanOrEqualTo(0).WithMessage("Minimum stock threshold cannot be negative.");
    }
}

public sealed class AdjustStockValidator : AbstractValidator<AdjustStockRequest>
{
    public AdjustStockValidator()
    {
        RuleFor(x => x.NewQuantity)
            .GreaterThanOrEqualTo(0).WithMessage("Quantity cannot be negative.");

        // A manual stock change without a reason is unauditable, and every stock change must be
        // explicable after the fact (FR-041).
        RuleFor(x => x.Note)
            .NotEmpty().WithMessage("A note explaining the adjustment is required.")
            .MaximumLength(255).WithMessage("Note cannot exceed 255 characters.");
    }
}
