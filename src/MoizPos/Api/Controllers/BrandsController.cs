using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Taxonomy;
using MoizPos.Application.Services;

namespace MoizPos.Api.Controllers;

/// <summary>
/// The Brands module (FR-001).
///
/// Reading is open to any signed-in user because the salesman's product list shows the brand
/// and the POS filters by it. Changing the list is the owner's business, so every write is
/// AdminOnly.
/// </summary>
[ApiController]
[Route("api/brands")]
[Authorize]
public sealed class BrandsController : ControllerBase
{
    private readonly IBrandService _brands;

    public BrandsController(IBrandService brands) => _brands = brands;

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var result = await _brands.SearchAsync(
            search,
            // Only an Admin manages the list, so only an Admin sees retired brands.
            includeInactive && CurrentUser.IsAdmin(User),
            page,
            pageSize,
            cancellationToken);

        return Ok(ApiResponse<PagedResult<BrandDto>>.Ok(result));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var brand = await _brands.GetAsync(id, cancellationToken);

        return Ok(ApiResponse<BrandDto>.Ok(brand));
    }

    [HttpPost]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Create(
        [FromBody] BrandUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _brands.CreateAsync(request, cancellationToken);
        var created = await _brands.GetAsync(id, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<BrandDto>.Ok(created));
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] BrandUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await _brands.UpdateAsync(id, request, cancellationToken);

        return Ok(ApiResponse<BrandDto>.Ok(await _brands.GetAsync(id, cancellationToken)));
    }

    /// <summary>
    /// Retires a brand. Products already filed under it are untouched — removing it outright
    /// would orphan stock the shop is holding.
    /// </summary>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Deactivate(long id, CancellationToken cancellationToken)
    {
        await _brands.DeactivateAsync(id, cancellationToken);

        return NoContent();
    }

    [HttpPost("{id:long}/reactivate")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Reactivate(long id, CancellationToken cancellationToken)
    {
        await _brands.ReactivateAsync(id, cancellationToken);

        return NoContent();
    }
}
