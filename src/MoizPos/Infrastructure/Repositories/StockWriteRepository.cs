using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Services;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class StockWriteRepository : IStockWriteRepository
{
    public async Task<ProductStockSnapshot?> LockProductAsync(
        IUnitOfWork unitOfWork,
        long productId,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<ProductStockSnapshot>(
            """
            SELECT id AS Id,
                   name AS Name,
                   quantity_on_hand AS QuantityOnHand,
                   cost_price AS CostPrice,
                   retail_price AS RetailPrice
            FROM products
            WHERE id = @productId
            FOR UPDATE;
            """,
            new { productId },
            unitOfWork.Transaction);
    }

    /// <summary>
    /// Sets the quantity only. Cost price is deliberately untouched: a recount changes how many
    /// units are on the shelf, never what they cost (FR-011a).
    /// </summary>
    public async Task UpdateQuantityAsync(
        IUnitOfWork unitOfWork,
        long productId,
        int newQuantity,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            UPDATE products
            SET quantity_on_hand = @newQuantity, updated_at_utc = @nowUtc
            WHERE id = @productId;
            """,
            new { productId, newQuantity, nowUtc },
            unitOfWork.Transaction);
    }
}
