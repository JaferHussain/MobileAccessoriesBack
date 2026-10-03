using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class SalesmanStockRepository : ISalesmanStockRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SalesmanStockRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    private sealed record ProductQuantity
    {
        public long ProductId { get; init; }

        public int Quantity { get; init; }
    }

    public async Task<IReadOnlyDictionary<long, int>> HeldBySalesmenAsync(
        IUnitOfWork unitOfWork, IReadOnlyList<long> productIds, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        // A locking read, so it sees every issue committed before this transaction took the product lock.
        var rows = await unitOfWork.Connection.QueryAsync<ProductQuantity>(
            """
            SELECT product_id AS ProductId, quantity AS Quantity
            FROM salesman_stock
            WHERE product_id IN @productIds
            ORDER BY user_id, product_id
            FOR UPDATE;
            """,
            new { productIds },
            unitOfWork.Transaction);

        return rows.GroupBy(row => row.ProductId).ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity));
    }

    public async Task<IReadOnlyDictionary<long, int>> HoldingAsync(
        IUnitOfWork unitOfWork, long userId, IReadOnlyList<long> productIds, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        var rows = await unitOfWork.Connection.QueryAsync<ProductQuantity>(
            """
            SELECT product_id AS ProductId, quantity AS Quantity
            FROM salesman_stock
            WHERE user_id = @userId AND product_id IN @productIds
            ORDER BY product_id
            FOR UPDATE;
            """,
            new { userId, productIds },
            unitOfWork.Transaction);

        return rows.ToDictionary(row => row.ProductId, row => row.Quantity);
    }

    public async Task MoveAsync(
        IUnitOfWork unitOfWork, long userId, long productId, int change, int resulting, SalesmanStockReason reason,
        long? referenceId, string? note, long recordedByUserId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            INSERT INTO salesman_stock (user_id, product_id, quantity, updated_at_utc)
            VALUES (@userId, @productId, @resulting, @nowUtc)
            ON DUPLICATE KEY UPDATE quantity = VALUES(quantity), updated_at_utc = VALUES(updated_at_utc);

            INSERT INTO salesman_stock_movements
                (user_id, product_id, reason, change_qty, resulting_qty, reference_id, note, recorded_by_user_id, created_at_utc)
            VALUES (@userId, @productId, @reason, @change, @resulting, @referenceId, @note, @recordedByUserId, @nowUtc);
            """,
            // The ENUM as its name — Dapper would otherwise write the underlying int.
            new { userId, productId, resulting, change, reason = reason.ToString(), referenceId, note, recordedByUserId, nowUtc },
            unitOfWork.Transaction);
    }

    public async Task<IReadOnlyDictionary<long, int>> HeldBySalesmenForDisplayAsync(
        IReadOnlyList<long> productIds, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ProductQuantity>(
            """
            SELECT product_id AS ProductId, SUM(quantity) AS Quantity
            FROM salesman_stock
            WHERE product_id IN @productIds
            GROUP BY product_id;
            """,
            new { productIds });

        return rows.ToDictionary(row => row.ProductId, row => row.Quantity);
    }

    public async Task<IReadOnlyDictionary<long, int>> HoldingForDisplayAsync(
        long userId, IReadOnlyList<long> productIds, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ProductQuantity>(
            """
            SELECT product_id AS ProductId, quantity AS Quantity
            FROM salesman_stock
            WHERE user_id = @userId AND product_id IN @productIds;
            """,
            new { userId, productIds });

        return rows.ToDictionary(row => row.ProductId, row => row.Quantity);
    }

    public async Task<IReadOnlyList<SalesmanHoldingRow>> HoldingForAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<SalesmanHoldingRow>(
            """
            SELECT s.product_id AS ProductId, p.name AS ProductName, s.quantity AS Quantity
            FROM salesman_stock s
            JOIN products p ON p.id = s.product_id
            WHERE s.user_id = @userId AND s.quantity > 0
            ORDER BY p.name;
            """,
            new { userId });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<SalesmanStockMovementRow>> MovementsForAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // The invoice or return number, so a line reads as the paper it matches.
        var rows = await connection.QueryAsync<SalesmanStockMovementRow>(
            """
            SELECT m.id AS Id, m.product_id AS ProductId, p.name AS ProductName,
                   CAST(m.reason AS CHAR) AS Reason, m.change_qty AS ChangeQty, m.resulting_qty AS ResultingQty,
                   m.reference_id AS ReferenceId,
                   CASE m.reason
                       WHEN 'Sold' THEN (SELECT invoice_number FROM invoices WHERE id = m.reference_id)
                       WHEN 'CustomerReturn' THEN (SELECT return_number FROM sale_returns WHERE id = m.reference_id)
                   END AS Reference,
                   m.note AS Note, u.full_name AS RecordedBy, m.created_at_utc AS CreatedAtUtc
            FROM salesman_stock_movements m
            JOIN products p ON p.id = m.product_id
            JOIN users u ON u.id = m.recorded_by_user_id
            WHERE m.user_id = @userId
            ORDER BY m.created_at_utc DESC, m.id DESC
            LIMIT 500;
            """,
            new { userId });

        return rows.AsList();
    }
}
