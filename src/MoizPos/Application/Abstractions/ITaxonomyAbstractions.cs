using MoizPos.Domain.Entities;

namespace MoizPos.Application.Abstractions;

/// <summary>
/// Reads and writes product categories.
///
/// Deliberately mirrors <see cref="IBrandRepository"/>: the two modules are separate because the
/// owner maintains them separately, not because they behave differently.
/// </summary>
public interface ICategoryRepository
{
    Task<(IReadOnlyList<Category> Items, int TotalItems)> SearchAsync(
        string? search,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Category?> FindByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(Category category, CancellationToken cancellationToken = default);

    Task UpdateAsync(Category category, CancellationToken cancellationToken = default);

    Task SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default);

    Task<bool> NameExistsAsync(
        string name,
        long? excludingId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many products are filed under this category. A category still in use cannot be
    /// removed — deleting it would orphan stock the shop actually holds.
    /// </summary>
    Task<int> ProductCountAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes product brands. See <see cref="ICategoryRepository"/>.</summary>
public interface IBrandRepository
{
    Task<(IReadOnlyList<Brand> Items, int TotalItems)> SearchAsync(
        string? search,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Brand?> FindByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(Brand brand, CancellationToken cancellationToken = default);

    Task UpdateAsync(Brand brand, CancellationToken cancellationToken = default);

    Task SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default);

    Task<bool> NameExistsAsync(
        string name,
        long? excludingId = null,
        CancellationToken cancellationToken = default);

    Task<int> ProductCountAsync(long id, CancellationToken cancellationToken = default);
}
