using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Products;
using MoizPos.Application.Services;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Storage;

namespace MoizPos.Api.Controllers;

[ApiController]
[Route("api/products")]
[Authorize]
public sealed class ProductsController : ControllerBase
{
    private readonly IProductService _products;
    private readonly IStockService _stock;

    public ProductsController(IProductService products, IStockService stock)
    {
        _products = products;
        _stock = stock;
    }

    /// <summary>Stock movement history for one product (FR-005).</summary>
    [HttpGet("{id:long}/stock-movements")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> StockMovements(
        long id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var history = await _stock.HistoryAsync(id, page, pageSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<StockMovementDto>>.Ok(history));
    }

    /// <summary>
    /// A manual stock correction — a recount, breakage, theft. Always audited, and a note is
    /// required so no change is left unexplained (FR-041).
    /// </summary>
    [HttpPost("{id:long}/adjust-stock")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> AdjustStock(
        long id,
        [FromBody] AdjustStockRequest request,
        CancellationToken cancellationToken)
    {
        var newQuantity = await _stock.AdjustAsync(
            id, request.NewQuantity, request.Note, CurrentUser.Id(User), cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { productId = id, quantityOnHand = newQuantity }));
    }

    /// <summary>
    /// Search and list. A Staff caller receives ProductStaffDto, which carries no cost price at
    /// all; an Admin receives ProductAdminDto (FR-003, FR-040).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] long? categoryId,
        [FromQuery] long? brandId,
        [FromQuery] SaleType saleType = SaleType.Retail,
        [FromQuery] bool localOnly = false,
        [FromQuery] bool lowStockOnly = false,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var query = new ProductQuery
        {
            Search = search,
            CategoryId = categoryId,
            BrandId = brandId,
            SaleType = saleType,
            LocalOnly = localOnly,
            LowStockOnly = lowStockOnly,
            // Only an Admin manages the catalogue, so only an Admin sees retired products.
            IncludeInactive = includeInactive && CurrentUser.IsAdmin(User),
            Page = page,
            PageSize = pageSize,
        };

        var result = await _products.SearchAsync(query, CurrentUser.Role(User), cancellationToken);

        return Ok(ApiResponse<PagedResult<ProductStaffDto>>.Ok(result));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(
        long id,
        [FromQuery] SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default)
    {
        // The POS re-reads through here when the salesman switches sale type mid-cart, so the
        // price returned has to follow that choice.
        var product = await _products.GetAsync(
            id, CurrentUser.Role(User), saleType, cancellationToken);

        return Ok(ApiResponse<ProductStaffDto>.Ok(product));
    }

    /// <summary>Barcode lookup for the counter's scanner (FR-018).</summary>
    [HttpGet("by-barcode/{barcode}")]
    public async Task<IActionResult> GetByBarcode(
        string barcode,
        [FromQuery] SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default)
    {
        // Scanning during a wholesale sale must quote the wholesale price, not the counter one.
        var product = await _products.FindByBarcodeAsync(
            barcode, CurrentUser.Role(User), saleType, cancellationToken);

        return product is null
            ? NotFound(ApiResponse<object>.Fail(
                "NOT_FOUND",
                $"No product found for barcode '{barcode}'.",
                traceId: HttpContext.TraceIdentifier))
            : Ok(ApiResponse<ProductStaffDto>.Ok(product));
    }

    [HttpPost]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Create(
        [FromBody] ProductUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _products.CreateAsync(request, cancellationToken);
        var created = await _products.GetAsync(id, CurrentUser.Role(User), cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(Get), new { id }, ApiResponse<ProductStaffDto>.Ok(created));
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] ProductUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await _products.UpdateAsync(id, request, cancellationToken);
        var updated = await _products.GetAsync(id, CurrentUser.Role(User), cancellationToken: cancellationToken);

        return Ok(ApiResponse<ProductStaffDto>.Ok(updated));
    }

    /// <summary>Uploads a product image (FR-001). Max 2 MB, JPEG/PNG/WebP only.</summary>
    [HttpPost("{id:long}/image")]
    [Authorize(Policy = Policies.AdminOnly)]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(
        long id,
        IFormFile file,
        [FromServices] IImageStorageService images,
        [FromServices] IProductRepository repository,
        CancellationToken cancellationToken)
    {
        // Confirms the product exists before writing a file we would then have to clean up.
        var existing = await _products.GetAsync(id, CurrentUser.Role(User), cancellationToken: cancellationToken);

        if (file is null || file.Length == 0)
        {
            throw new BusinessRuleViolationException("No image was uploaded.");
        }

        await using var stream = file.OpenReadStream();

        var relativePath = await images.SaveProductImageAsync(
            stream, file.ContentType, file.Length, cancellationToken);

        await repository.SetImagePathAsync(id, relativePath, cancellationToken);

        // Replacing an image should not leave the old file behind forever.
        if (!string.IsNullOrWhiteSpace(existing.ImagePath))
        {
            images.DeleteProductImage(existing.ImagePath);
        }

        return Ok(ApiResponse<object>.Ok(new { productId = id, imagePath = relativePath }));
    }

    /// <summary>Retires a product. Never a hard delete — old invoices reference it (FR-002).</summary>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Deactivate(long id, CancellationToken cancellationToken)
    {
        await _products.DeactivateAsync(id, cancellationToken);

        return NoContent();
    }
}
