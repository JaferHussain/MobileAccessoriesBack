namespace MoizPos.Application.Abstractions;

/// <summary>
/// A customer as the owner's Udhaar customers page shows them. <b>Carries whether each side of the
/// ID card is on file, never where</b> — the paths stay in the database, and the photos are only
/// ever streamed through the owner's own route. Init-only: Dapper materialises it.
/// </summary>
public sealed record UdhaarCustomerRow
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? MobileNumber { get; init; }

    public decimal OutstandingBalance { get; init; }

    /// <summary>Registered as an udhaar customer — may be sold to on full udhaar.</summary>
    public bool IsUdhaarCustomer { get; init; }

    public bool HasIdCardFront { get; init; }

    public bool HasIdCardBack { get; init; }

    /// <summary>An udhaar customer still without both sides of the ID card — marked before cards were asked for.</summary>
    public bool IdCardMissing { get; init; }
}

public interface IUdhaarCustomerRepository
{
    /// <summary>Every active udhaar customer, by name. <paramref name="search"/> matches name or mobile.</summary>
    Task<IReadOnlyList<UdhaarCustomerRow>> ListAsync(string? search, CancellationToken cancellationToken = default);

    /// <summary>Any customer's udhaar standing — null when there is no such customer.</summary>
    Task<UdhaarCustomerRow?> FindAsync(long customerId, CancellationToken cancellationToken = default);

    /// <summary>Where the two photos are stored. Never returned through the API.</summary>
    Task<(string? Front, string? Back)> IdCardPathsAsync(long customerId, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(
        string name, string mobileNumber, string frontPath, string backPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an existing customer an udhaar customer. A null mobile or photo leaves the stored one
    /// as it is — so completing a missing ID card never asks for the side already on file.
    /// </summary>
    Task RegisterAsync(
        long customerId, string? mobileNumber, string? frontPath, string? backPath, CancellationToken cancellationToken = default);

    /// <summary>Takes the mark away. The ID card photos are kept — they are evidence of who was given credit.</summary>
    Task RemoveAsync(long customerId, CancellationToken cancellationToken = default);
}
