using MoizPos.Domain.Enums;

namespace MoizPos.Application.Abstractions;

/// <summary>A share token as stored. Only the hash is ever persisted.</summary>
public sealed record StoredDocumentToken
{
    public long Id { get; init; }

    public DocumentType DocumentType { get; init; }

    public long ReferenceId { get; init; }

    public DateTime ExpiresAtUtc { get; init; }

    public DateTime? RevokedAtUtc { get; init; }

    public bool IsUsable(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}

/// <summary>
/// One share link, as the owner sees it when deciding what to withdraw.
///
/// <para><b>No token, ever.</b> Only the hash is stored, and re-exposing a live link through an
/// authenticated screen would turn whoever is reading over the owner's shoulder into a link
/// holder. The owner revokes by id.</para>
/// </summary>
public sealed record ShareLinkSummary
{
    public long Id { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Who released it. A customer-facing release has someone accountable for it.</summary>
    public string CreatedByUserName { get; init; } = string.Empty;

    public DateTime ExpiresAtUtc { get; init; }

    public DateTime? RevokedAtUtc { get; init; }

    /// <summary>Null when never opened — which is itself the answer to "did this get out?".</summary>
    public DateTime? LastAccessedAtUtc { get; init; }

    public int AccessCount { get; init; }

    public bool IsUsable { get; init; }
}

public interface IDocumentTokenRepository
{
    /// <summary>Every link issued for one document, newest first.</summary>
    Task<IReadOnlyList<ShareLinkSummary>> ListForDocumentAsync(
        DocumentType documentType,
        long referenceId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>The link itself, for a revoke that must know whether it exists.</summary>
    Task<ShareLinkSummary?> FindByIdAsync(
        long id, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(
        string tokenHash,
        DocumentType documentType,
        long referenceId,
        DateTime expiresAtUtc,
        long createdByUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<StoredDocumentToken?> FindAsync(string tokenHash, CancellationToken cancellationToken = default);

    Task RecordAccessAsync(long id, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task RevokeAsync(long id, DateTime nowUtc, CancellationToken cancellationToken = default);
}
