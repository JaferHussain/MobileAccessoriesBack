using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Taxonomy;
using MoizPos.Application.Services;

namespace MoizPos.Api.Controllers;

/// <summary>
/// The Categories module (FR-001).
///
/// Reading is open to any signed-in user because the salesman's product list shows the category
/// and the POS filters by it. Changing the list is the owner's business, so every write is
/// AdminOnly.
/// </summary>
[ApiController]
[Route("api/categories")]
[Authorize]
public sealed class CategoriesController : ControllerBase
{
    private readonly ICategoryService _categories;

    public CategoriesController(ICategoryService categories) => _categories = categories;

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var result = await _categories.SearchAsync(
            search,
            // Only an Admin manages the list, so only an Admin sees retired categories.
            includeInactive && CurrentUser.IsAdmin(User),
            page,
            pageSize,
            cancellationToken);

        return Ok(ApiResponse<PagedResult<CategoryDto>>.Ok(result));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var category = await _categories.GetAsync(id, cancellationToken);

        return Ok(ApiResponse<CategoryDto>.Ok(category));
    }

    [HttpPost]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Create(
        [FromBody] CategoryUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _categories.CreateAsync(request, cancellationToken);
        var created = await _categories.GetAsync(id, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<CategoryDto>.Ok(created));
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] CategoryUpsertRequest request,
        CancellationToken cancellationToken)
    {
        await _categories.UpdateAsync(id, request, cancellationToken);

        return Ok(ApiResponse<CategoryDto>.Ok(await _categories.GetAsync(id, cancellationToken)));
    }

    /// <summary>
    /// Retires a category. Products already filed under it are untouched — removing it outright
    /// would orphan stock the shop is holding.
    /// </summary>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Deactivate(long id, CancellationToken cancellationToken)
    {
        await _categories.DeactivateAsync(id, cancellationToken);

        return NoContent();
    }

    [HttpPost("{id:long}/reactivate")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Reactivate(long id, CancellationToken cancellationToken)
    {
        await _categories.ReactivateAsync(id, cancellationToken);

        return NoContent();
    }
}
