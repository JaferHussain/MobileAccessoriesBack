using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <summary>
/// Reads the audit trail (FR-041). Read-only by design: the trail is append-only, written from
/// inside the transactions it records, and nothing may edit it after the fact.
/// </summary>
public sealed class AuditRepository : IAuditRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AuditRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<AuditEntryRow> Items, int TotalItems)> SearchAsync(
        string? entityType,
        long? entityId,
        long? userId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = "WHERE 1 = 1";

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            filter += " AND a.entity_type = @entityType";
        }

        if (entityId is not null)
        {
            filter += " AND a.entity_id = @entityId";
        }

        if (userId is not null)
        {
            filter += " AND a.user_id = @userId";
        }

        if (fromUtc is not null)
        {
            filter += " AND a.occurred_at_utc >= @fromUtc";
        }

        if (toUtc is not null)
        {
            filter += " AND a.occurred_at_utc < @toUtc";
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT a.id              AS Id,
                    a.entity_type     AS EntityType,
                    a.entity_id       AS EntityId,
                    a.field_name      AS FieldName,
                    a.old_value       AS OldValue,
                    a.new_value       AS NewValue,
                    a.action          AS Action,
                    u.full_name       AS UserName,
                    a.occurred_at_utc AS OccurredAtUtc
             FROM audit_entries a
             JOIN users u ON u.id = a.user_id
             {filter}
             ORDER BY a.occurred_at_utc DESC, a.id DESC
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM audit_entries a {filter};
             """,
            new
            {
                entityType,
                entityId,
                userId,
                fromUtc,
                toUtc,
                limit = pageSize,
                offset = (page - 1) * pageSize,
            });

        var items = (await reader.ReadAsync<AuditEntryRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }
}
