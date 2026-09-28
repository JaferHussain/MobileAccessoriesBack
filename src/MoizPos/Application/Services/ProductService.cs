using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Products;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

public interface IProductService
{
    Task<PagedResult<ProductStaffDto>> SearchAsync(
        ProductQuery query, UserRole role, CancellationToken cancellationToken = default);

    /// <summary>
    /// One product, priced for the sale being made. The POS re-reads through here when the
    /// salesman switches between retail and wholesale with items already in the cart.
    /// </summary>
    Task<ProductStaffDto> GetAsync(
        long id, UserRole role, SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Barcode lookup for the counter's scanner. <paramref name="saleType"/> decides which price
    /// comes back, so scanning during a wholesale sale quotes the wholesale price.
    /// </summary>
    Task<ProductStaffDto?> FindByBarcodeAsync(
        string barcode, UserRole role, SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default);

    Task<long> CreateAsync(ProductUpsertRequest request, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, ProductUpsertRequest request, CancellationToken cancellationToken = default);

    Task DeactivateAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Catalogue reads and writes.
///
/// <para>Projection is role-aware: a Staff principal receives <see cref="ProductStaffDto"/>,
/// which has no cost property at all, and an Admin receives <see cref="ProductAdminDto"/>. The
/// decision is made once, here, so no controller can forget it (FR-040).</para>
///
/// <para>This service never changes stock or cost. Stock moves only through purchases, sales,
/// returns and audited adjustments; cost moves only through a purchase (FR-011a). Editing a
/// product must not be a back door into either.</para>
/// </summary>
public sealed class ProductService : IProductService
{
    private readonly IProductRepository _products;
    private readonly ICategoryRepository _categories;
    private readonly IBrandRepository _brands;

    public ProductService(
        IProductRepository products,
        ICategoryRepository categories,
        IBrandRepository brands)
    {
        _products = products;
        _categories = categories;
        _brands = brands;
    }

    public async Task<PagedResult<ProductStaffDto>> SearchAsync(
        ProductQuery query,
        UserRole role,
        CancellationToken cancellationToken = default)
    {
        var (page, pageSize) = PagedResult<ProductStaffDto>.Normalize(query.Page, query.PageSize);

        // Every rule about what the shopkeeper typed is decided here, once, for all three screens
        // that search — Products, the POS lookup and the Purchases picker.
        var terms = ProductSearchTerms.Parse(query.Search);

        if (terms.IsTooShort)
        {
            throw new SearchTooShortException();
        }

        var normalized = query with
        {
            Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            SearchWords = terms.Words,
            Page = page,
            PageSize = pageSize,
        };

        var (rows, total) = await _products.SearchAsync(normalized, cancellationToken);

        var items = rows.Select(row => Project(row, role)).ToList();

        return new PagedResult<ProductStaffDto>(items, page, pageSize, total);
    }

    public async Task<ProductStaffDto> GetAsync(
        long id,
        UserRole role,
        SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default)
    {
        var row = await _products.FindByIdAsync(id, saleType, cancellationToken)
            ?? throw new NotFoundException("Product", id);

        return Project(row, role);
    }

    public async Task<ProductStaffDto?> FindByBarcodeAsync(
        string barcode,
        UserRole role,
        SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        var row = await _products.FindByBarcodeAsync(barcode.Trim(), saleType, cancellationToken);

        return row is null ? null : Project(row, role);
    }

    public async Task<long> CreateAsync(
        ProductUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureBarcodeIsFreeAsync(request.Barcode, null, cancellationToken);
        await EnsureTaxonomyExistsAsync(request.CategoryId, request.BrandId, cancellationToken);

        var product = new Product
        {
            Name = request.Name.Trim(),
            CategoryId = request.CategoryId,
            BrandId = request.BrandId,
            Model = Trim(request.Model),
            Barcode = Trim(request.Barcode),
            MinStockThreshold = request.MinStockThreshold,
            SupplierId = request.SupplierId,

            // A new product is an empty shelf label: nothing on hand and nothing priced. The
            // first purchase supplies the cost, the two selling prices and the quantity, all in
            // one transaction — so a product is never half-priced or priced-but-absent.
            CostPrice = 0m,
            WholesalePrice = 0m,
            RetailPrice = 0m,
            QuantityOnHand = 0,
        };

        return await _products.CreateAsync(product, cancellationToken);
    }

    public async Task UpdateAsync(
        long id,
        ProductUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _products.FindByIdAsync(id, cancellationToken: cancellationToken)
            ?? throw new NotFoundException("Product", id);

        await EnsureBarcodeIsFreeAsync(request.Barcode, id, cancellationToken);
        await EnsureTaxonomyExistsAsync(request.CategoryId, request.BrandId, cancellationToken);

        var product = new Product
        {
            Id = id,
            Name = request.Name.Trim(),
            CategoryId = request.CategoryId,
            BrandId = request.BrandId,
            Model = Trim(request.Model),
            Barcode = Trim(request.Barcode),
            MinStockThreshold = request.MinStockThreshold,
            SupplierId = request.SupplierId,

            // Carried through unchanged. Editing a product changes what it IS, never what it is
            // worth or how many there are: prices move through a purchase, stock through a
            // purchase, sale, return or audited adjustment.
            CostPrice = existing.CostPrice,
            WholesalePrice = existing.WholesalePrice,
            RetailPrice = existing.RetailPrice,
            QuantityOnHand = existing.QuantityOnHand,
        };

        await _products.UpdateAsync(product, cancellationToken);
    }

    public async Task DeactivateAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = await _products.FindByIdAsync(id, cancellationToken: cancellationToken)
            ?? throw new NotFoundException("Product", id);

        // Deactivated, never deleted: historical invoices reference this row (FR-002).
        await _products.DeactivateAsync(id, cancellationToken);
    }

    /// <summary>Projects a row to the shape this role is permitted to see.</summary>
    public static ProductStaffDto Project(ProductRow row, UserRole role)
    {
        var isLowStock = StockRules.IsLowStock(row.QuantityOnHand, row.MinStockThreshold);

        if (role != UserRole.Admin)
        {
            return new ProductStaffDto
            {
                Id = row.Id,
                Name = row.Name,
                CategoryId = row.CategoryId,
                Category = row.Category,
                BrandId = row.BrandId,
                Brand = row.Brand,
                BrandIsLocal = row.BrandIsLocal,
                Model = row.Model,
                Barcode = row.Barcode,
                ImagePath = row.ImagePath,
                SalePrice = row.SalePrice,
                QuantityOnHand = row.QuantityOnHand,
                IsLowStock = isLowStock,
                IsActive = row.IsActive,
            };
        }

        return new ProductAdminDto
        {
            Id = row.Id,
            Name = row.Name,
            CategoryId = row.CategoryId,
            Category = row.Category,
            BrandId = row.BrandId,
            Brand = row.Brand,
            BrandIsLocal = row.BrandIsLocal,
            Model = row.Model,
            Barcode = row.Barcode,
            ImagePath = row.ImagePath,
            SalePrice = row.SalePrice,
            QuantityOnHand = row.QuantityOnHand,
            IsLowStock = isLowStock,
            IsActive = row.IsActive,
            CostPrice = row.CostPrice,
            WholesalePrice = row.WholesalePrice,
            RetailPrice = row.RetailPrice,
            MinStockThreshold = row.MinStockThreshold,
            SupplierId = row.SupplierId,
            SupplierName = row.SupplierName,
        };
    }

    /// <summary>
    /// Refuses a product pointed at a category or brand that does not exist, or one the owner has
    /// retired. The foreign key would catch the first case as a 500; this makes it a clear 404.
    /// </summary>
    /// <summary>
    /// Checks a product's filing: the category exists and is in use, and so does the brand.
    ///
    /// <para><b>The brand is required</b> — the owner's rule is that goods with no well-known
    /// maker are filed under a brand created for them rather than left blank, so "no brand" is
    /// not a state a product can be in.</para>
    /// </summary>
    private async Task EnsureTaxonomyExistsAsync(
        long categoryId,
        long brandId,
        CancellationToken cancellationToken)
    {
        var category = await _categories.FindByIdAsync(categoryId, cancellationToken)
            ?? throw new NotFoundException("Category", categoryId);

        if (!category.IsActive)
        {
            throw new BusinessRuleViolationException(
                $"The category '{category.Name}' is no longer in use. Choose an active category.");
        }

        var brand = await _brands.FindByIdAsync(brandId, cancellationToken)
            ?? throw new NotFoundException("Brand", brandId);

        if (!brand.IsActive)
        {
            throw new BusinessRuleViolationException(
                $"The brand '{brand.Name}' is no longer in use. Choose an active brand.");
        }

    }

    private async Task EnsureBarcodeIsFreeAsync(
        string? barcode,
        long? excludingProductId,
        CancellationToken cancellationToken)
    {
        var trimmed = Trim(barcode);

        if (trimmed is null)
        {
            return;
        }

        if (await _products.BarcodeExistsAsync(trimmed, excludingProductId, cancellationToken))
        {
            throw new BusinessRuleViolationException(
                $"Barcode '{trimmed}' is already used by another product.");
        }
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
