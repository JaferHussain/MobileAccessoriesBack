using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Taxonomy;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

public interface ICategoryService
{
    Task<PagedResult<CategoryDto>> SearchAsync(
        string? search, bool includeInactive, int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<CategoryDto> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(CategoryUpsertRequest request, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, CategoryUpsertRequest request, CancellationToken cancellationToken = default);

    Task DeactivateAsync(long id, CancellationToken cancellationToken = default);

    Task ReactivateAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Categories module.
///
/// A category is a label the whole catalogue depends on, so the two rules here are about not
/// breaking that dependency: names are unique, and a category in use is retired rather than
/// removed.
/// </summary>
public sealed class CategoryService : ICategoryService
{
    private readonly ICategoryRepository _categories;

    public CategoryService(ICategoryRepository categories) => _categories = categories;

    public async Task<PagedResult<CategoryDto>> SearchAsync(
        string? search,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<CategoryDto>.Normalize(page, pageSize);

        var (items, total) = await _categories.SearchAsync(
            search, includeInactive, normalizedPage, normalizedSize, cancellationToken);

        var dtos = new List<CategoryDto>(items.Count);

        foreach (var category in items)
        {
            dtos.Add(await ToDtoAsync(category, cancellationToken));
        }

        return new PagedResult<CategoryDto>(dtos, normalizedPage, normalizedSize, total);
    }

    public async Task<CategoryDto> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var category = await _categories.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Category", id);

        return await ToDtoAsync(category, cancellationToken);
    }

    public async Task<long> CreateAsync(
        CategoryUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();

        await EnsureNameIsFreeAsync(name, null, cancellationToken);

        return await _categories.CreateAsync(
            new Category { Name = name, Description = Trim(request.Description), IsActive = true },
            cancellationToken);
    }

    public async Task UpdateAsync(
        long id,
        CategoryUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = await _categories.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Category", id);

        var name = request.Name.Trim();

        await EnsureNameIsFreeAsync(name, id, cancellationToken);

        await _categories.UpdateAsync(
            new Category { Id = id, Name = name, Description = Trim(request.Description) },
            cancellationToken);
    }

    public async Task DeactivateAsync(long id, CancellationToken cancellationToken = default)
    {
        var category = await _categories.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Category", id);

        // Retired, never deleted. Products filed here stay filed here and keep displaying the
        // name; the category simply stops being offered when adding new stock.
        await _categories.SetActiveAsync(id, isActive: false, cancellationToken);

        _ = category;
    }

    public async Task ReactivateAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = await _categories.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Category", id);

        await _categories.SetActiveAsync(id, isActive: true, cancellationToken);
    }

    private async Task<CategoryDto> ToDtoAsync(
        Category category,
        CancellationToken cancellationToken) =>
        new()
        {
            Id = category.Id,
            Name = category.Name,
            Description = category.Description,
            IsActive = category.IsActive,
            ProductCount = await _categories.ProductCountAsync(category.Id, cancellationToken),
        };

    private async Task EnsureNameIsFreeAsync(
        string name,
        long? excludingId,
        CancellationToken cancellationToken)
    {
        if (await _categories.NameExistsAsync(name, excludingId, cancellationToken))
        {
            throw new BusinessRuleViolationException($"The category '{name}' already exists.");
        }
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
