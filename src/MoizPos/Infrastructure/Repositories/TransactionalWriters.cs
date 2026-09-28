using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <summary>
/// Appends stock movements inside a caller-supplied transaction.
///
/// Takes the <see cref="IUnitOfWork"/> rather than opening its own connection precisely so the
/// movement row and the quantity update commit or roll back together (invariant 1).
/// </summary>
public sealed class StockMovementWriter : IStockMovementWriter
{
    public async Task<long> AppendAsync(
        IUnitOfWork unitOfWork,
        long productId,
        int changeQty,
        int resultingQty,
        StockMovementReason reason,
        long? referenceId,
        long userId,
        string? note,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO stock_movements
                (product_id, change_qty, resulting_qty, reason, reference_id, user_id, note, created_at_utc)
            VALUES
                (@productId, @changeQty, @resultingQty, @reason, @referenceId, @userId, @note, @occurredAtUtc);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                productId,
                changeQty,
                resultingQty,
                reason = reason.ToString(),
                referenceId,
                userId,
                note,
                occurredAtUtc,
            },
            unitOfWork.Transaction);
    }
}

/// <summary>
/// Writes audit entries inside the mutating transaction, so a rolled-back change can never leave
/// an audit row claiming it happened (FR-041, invariant 5).
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    public async Task RecordAsync(
        IUnitOfWork unitOfWork,
        string entityType,
        long entityId,
        string fieldName,
        string? oldValue,
        string? newValue,
        string action,
        long userId,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            INSERT INTO audit_entries
                (entity_type, entity_id, field_name, old_value, new_value, action, user_id, occurred_at_utc)
            VALUES
                (@entityType, @entityId, @fieldName, @oldValue, @newValue, @action, @userId, @occurredAtUtc);
            """,
            new
            {
                entityType,
                entityId,
                fieldName,
                oldValue,
                newValue,
                action,
                userId,
                occurredAtUtc,
            },
            unitOfWork.Transaction);
    }
}
