using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Taxonomy;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

public interface IBrandService
{
    Task<PagedResult<BrandDto>> SearchAsync(
        string? search, bool includeInactive, int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<BrandDto> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(BrandUpsertRequest request, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, BrandUpsertRequest request, CancellationToken cancellationToken = default);

    Task DeactivateAsync(long id, CancellationToken cancellationToken = default);

    Task ReactivateAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Brands module.
///
/// A brand is a label the whole catalogue depends on, so the two rules here are about not
/// breaking that dependency: names are unique, and a brand in use is retired rather than
/// removed.
/// </summary>
public sealed class BrandService : IBrandService
{
    private readonly IBrandRepository _brands;

    public BrandService(IBrandRepository brands) => _brands = brands;

    public async Task<PagedResult<BrandDto>> SearchAsync(
        string? search,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<BrandDto>.Normalize(page, pageSize);

        var (items, total) = await _brands.SearchAsync(
            search, includeInactive, normalizedPage, normalizedSize, cancellationToken);

        var dtos = new List<BrandDto>(items.Count);

        foreach (var brand in items)
        {
            dtos.Add(await ToDtoAsync(brand, cancellationToken));
        }

        return new PagedResult<BrandDto>(dtos, normalizedPage, normalizedSize, total);
    }

    public async Task<BrandDto> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var brand = await _brands.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Brand", id);

        return await ToDtoAsync(brand, cancellationToken);
    }

    public async Task<long> CreateAsync(
        BrandUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();

        await EnsureNameIsFreeAsync(name, null, cancellationToken);

        return await _brands.CreateAsync(
            new Brand
            {
                Name = name,
                Description = Trim(request.Description),
                IsLocal = request.IsLocal,
                IsActive = true,
            },
            cancellationToken);
    }

    public async Task UpdateAsync(
        long id,
        BrandUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = await _brands.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Brand", id);

        var name = request.Name.Trim();

        await EnsureNameIsFreeAsync(name, id, cancellationToken);

        await _brands.UpdateAsync(
            new Brand
            {
                Id = id,
                Name = name,
                Description = Trim(request.Description),
                IsLocal = request.IsLocal,
            },
            cancellationToken);
    }

    public async Task DeactivateAsync(long id, CancellationToken cancellationToken = default)
    {
        var brand = await _brands.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Brand", id);

        // Retired, never deleted. Products filed here stay filed here and keep displaying the
        // name; the brand simply stops being offered when adding new stock.
        await _brands.SetActiveAsync(id, isActive: false, cancellationToken);

        _ = brand;
    }

    public async Task ReactivateAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = await _brands.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Brand", id);

        await _brands.SetActiveAsync(id, isActive: true, cancellationToken);
    }

    private async Task<BrandDto> ToDtoAsync(
        Brand brand,
        CancellationToken cancellationToken) =>
        new()
        {
            Id = brand.Id,
            Name = brand.Name,
            Description = brand.Description,
            IsLocal = brand.IsLocal,
            IsActive = brand.IsActive,
            ProductCount = await _brands.ProductCountAsync(brand.Id, cancellationToken),
        };

    private async Task EnsureNameIsFreeAsync(
        string name,
        long? excludingId,
        CancellationToken cancellationToken)
    {
        if (await _brands.NameExistsAsync(name, excludingId, cancellationToken))
        {
            throw new BusinessRuleViolationException($"The brand '{name}' already exists.");
        }
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
