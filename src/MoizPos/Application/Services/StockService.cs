using System.Globalization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Products;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>Write side of manual stock corrections.</summary>
public interface IStockWriteRepository
{
    Task<ProductStockSnapshot?> LockProductAsync(
        IUnitOfWork unitOfWork, long productId, CancellationToken cancellationToken = default);

    Task UpdateQuantityAsync(
        IUnitOfWork unitOfWork,
        long productId,
        int newQuantity,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public interface IStockService
{
    Task<PagedResult<StockMovementDto>> HistoryAsync(
        long productId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<int> AdjustAsync(
        long productId, int newQuantity, string note, long userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stock history and manual corrections.
///
/// An adjustment is the only way to change stock outside a purchase, sale or return — a physical
/// recount, breakage, theft. It always requires a note and always writes both a movement and an
/// audit entry, so an unexplained change is impossible (FR-005, FR-041).
/// </summary>
public sealed class StockService : IStockService
{
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IStockWriteRepository _stockWrites;
    private readonly IStockMovementRepository _movements;
    private readonly IStockMovementWriter _movementWriter;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public StockService(
        IUnitOfWorkFactory unitOfWorkFactory,
        IStockWriteRepository stockWrites,
        IStockMovementRepository movements,
        IStockMovementWriter movementWriter,
        IAuditWriter audit,
        IClock clock)
    {
        _unitOfWorkFactory = unitOfWorkFactory;
        _stockWrites = stockWrites;
        _movements = movements;
        _movementWriter = movementWriter;
        _audit = audit;
        _clock = clock;
    }

    public async Task<PagedResult<StockMovementDto>> HistoryAsync(
        long productId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) =
            PagedResult<StockMovementDto>.Normalize(page, pageSize);

        var (items, total) = await _movements.ListForProductAsync(
            productId, normalizedPage, normalizedSize, cancellationToken);

        var dtos = items.Select(m => new StockMovementDto
        {
            Id = m.Id,
            ProductId = m.ProductId,
            ChangeQty = m.ChangeQty,
            ResultingQty = m.ResultingQty,
            Reason = m.Reason.ToString(),
            ReferenceId = m.ReferenceId,
            Note = m.Note,
            CreatedAtUtc = m.CreatedAtUtc,
        }).ToList();

        return new PagedResult<StockMovementDto>(dtos, normalizedPage, normalizedSize, total);
    }

    public async Task<int> AdjustAsync(
        long productId,
        int newQuantity,
        string note,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (newQuantity < 0)
        {
            throw new BusinessRuleViolationException("Stock quantity cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            throw new BusinessRuleViolationException(
                "A note explaining the adjustment is required.");
        }

        var nowUtc = _clock.UtcNow;

        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        var product = await _stockWrites.LockProductAsync(uow, productId, cancellationToken)
            ?? throw new NotFoundException("Product", productId);

        var change = newQuantity - product.QuantityOnHand;

        if (change == 0)
        {
            // Nothing moved; recording a zero movement would only add noise to the history.
            return product.QuantityOnHand;
        }

        await _stockWrites.UpdateQuantityAsync(uow, productId, newQuantity, nowUtc, cancellationToken);

        await _movementWriter.AppendAsync(
            uow, productId, change, newQuantity, StockMovementReason.Adjustment,
            referenceId: null, userId, note.Trim(), nowUtc, cancellationToken);

        await _audit.RecordAsync(
            uow, "Product", productId, "quantity_on_hand",
            product.QuantityOnHand.ToString(CultureInfo.InvariantCulture),
            newQuantity.ToString(CultureInfo.InvariantCulture),
            "Adjustment", userId, nowUtc, cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return newQuantity;
    }
}
