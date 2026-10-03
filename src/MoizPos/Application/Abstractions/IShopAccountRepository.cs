using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>
/// One of the shop's own bank or wallet accounts. Init-only properties: Dapper materialises it.
/// </summary>
public sealed record ShopAccount
{
    public long Id { get; init; }

    /// <summary>What the owner calls it: "HBL Current", "JazzCash 0300".</summary>
    public string Name { get; init; } = string.Empty;

    public ShopAccountType AccountType { get; init; }

    public string? AccountNumber { get; init; }

    public string? AccountTitle { get; init; }

    /// <summary>False once retired: kept for the payments that name it, offered for no new ones.</summary>
    public bool IsActive { get; init; }
}

public sealed record ShopAccountInput(
    string Name, ShopAccountType AccountType, string? AccountNumber, string? AccountTitle);

public interface IShopAccountRepository
{
    Task<IReadOnlyList<ShopAccount>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default);

    Task<ShopAccount?> FindAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Whether another account already has this name. Case- and accent-insensitive, as the column is.</summary>
    Task<bool> NameTakenAsync(string name, long? exceptId, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(ShopAccountInput input, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, ShopAccountInput input, CancellationToken cancellationToken = default);

    Task SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default);
}
