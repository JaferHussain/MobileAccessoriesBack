namespace MoizPos.Application.Abstractions;

public sealed record BackupFile
{
    public string FileName { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}

public sealed record BackupResult(string FileName, long SizeBytes, DateTime CreatedAtUtc);

/// <summary>
/// Backup and restore of the shop's database (FR-045..047).
///
/// <para>This is a logical dump, not point-in-time recovery: losing the machine loses up to a
/// day of sales. SC-012 only requires a restore to reproduce what was committed before the
/// backup was taken, so that is acceptable — but the owner should know it, and the backup
/// directory belongs on a different device (research.md R8).</para>
/// </summary>
public interface IBackupService
{
    Task<BackupResult> CreateAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupFile>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores a backup, overwriting everything currently in the database.</summary>
    Task RestoreAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>Deletes dumps older than the retention window.</summary>
    Task<int> PruneAsync(CancellationToken cancellationToken = default);
}

/// <summary>An audit entry as the owner reads it (FR-041).</summary>
public sealed record AuditEntryRow
{
    public long Id { get; init; }

    public string EntityType { get; init; } = string.Empty;

    public long EntityId { get; init; }

    public string FieldName { get; init; } = string.Empty;

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public string Action { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public DateTime OccurredAtUtc { get; init; }
}

public interface IAuditRepository
{
    Task<(IReadOnlyList<AuditEntryRow> Items, int TotalItems)> SearchAsync(
        string? entityType,
        long? entityId,
        long? userId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
