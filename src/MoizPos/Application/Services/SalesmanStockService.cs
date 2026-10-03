using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>A product and how many of it, as the owner issues or takes back.</summary>
public sealed record SalesmanStockLine(long ProductId, int Quantity);

/// <summary>What a salesman carries, and every unit in and out of his bag.</summary>
public sealed record SalesmanStockStatement(
    long UserId,
    string FullName,
    string? Job,
    int TotalUnits,
    IReadOnlyList<SalesmanHoldingRow> Items,
    IReadOnlyList<SalesmanStockMovementRow> Movements);

public interface ISalesmanStockService
{
    Task<SalesmanStockStatement> StatementAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>The owner hands goods to a field salesman. Only what is on the shelf.</summary>
    Task<SalesmanStockStatement> IssueAsync(
        long userId, IReadOnlyList<SalesmanStockLine> lines, string? note, long recordedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Goods he brings back to the shop. Only what he is carrying.</summary>
    Task<SalesmanStockStatement> ReturnAsync(
        long userId, IReadOnlyList<SalesmanStockLine> lines, string? note, long recordedByUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The stock a field salesman carries out of the shop and brings back — every unit tracked.
///
/// <para>Issuing and returning never touch <c>quantity_on_hand</c>: that is what the shop OWNS, and
/// goods in his bag are still owned. They only move the units between the shelf and his bag. His
/// sales take units out of both (<see cref="InvoiceService"/>); a customer handing goods back to him
/// puts them back into both (<see cref="ReturnService"/>).</para>
/// </summary>
public sealed class SalesmanStockService : ISalesmanStockService
{
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IStockWriteRepository _stock;
    private readonly ISalesmanStockRepository _salesmanStock;
    private readonly IUserRepository _users;
    private readonly IClock _clock;

    public SalesmanStockService(
        IUnitOfWorkFactory unitOfWorkFactory,
        IStockWriteRepository stock,
        ISalesmanStockRepository salesmanStock,
        IUserRepository users,
        IClock clock)
    {
        _unitOfWorkFactory = unitOfWorkFactory;
        _stock = stock;
        _salesmanStock = salesmanStock;
        _users = users;
        _clock = clock;
    }

    public async Task<SalesmanStockStatement> StatementAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User", userId);

        var items = await _salesmanStock.HoldingForAsync(userId, cancellationToken);
        var movements = await _salesmanStock.MovementsForAsync(userId, cancellationToken);

        return new SalesmanStockStatement(
            user.Id, user.FullName, user.Job?.ToString(), items.Sum(item => item.Quantity), items, movements);
    }

    public async Task<SalesmanStockStatement> IssueAsync(
        long userId, IReadOnlyList<SalesmanStockLine> lines, string? note, long recordedByUserId,
        CancellationToken cancellationToken = default)
    {
        var user = await FieldSalesmanAsync(userId, cancellationToken);
        Validate(lines);

        var nowUtc = _clock.UtcNow;
        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        var (productIds, names, onHand) = await LockProductsAsync(uow, lines, cancellationToken);
        var held = await _salesmanStock.HeldBySalesmenAsync(uow, productIds, cancellationToken);
        var his = await _salesmanStock.HoldingAsync(uow, userId, productIds, cancellationToken);

        // Every line checked before anything moves: an issue is all or nothing.
        foreach (var line in lines)
        {
            var heldNow = held.GetValueOrDefault(line.ProductId);

            if (!SalesmanStockRules.CanIssue(onHand[line.ProductId], heldNow, line.Quantity))
            {
                throw new BusinessRuleViolationException(
                    $"Only {SalesmanStockRules.AtShop(onHand[line.ProductId], heldNow)} of '{names[line.ProductId]}' " +
                    $"are in the shop to issue — {line.Quantity} were asked for.");
            }
        }

        foreach (var line in lines)
        {
            var resulting = his.GetValueOrDefault(line.ProductId) + line.Quantity;

            await _salesmanStock.MoveAsync(
                uow, userId, line.ProductId, line.Quantity, resulting, SalesmanStockReason.Issued,
                referenceId: null, Trimmed(note), recordedByUserId, nowUtc, cancellationToken);
        }

        await uow.CommitAsync(cancellationToken);

        return await StatementAsync(user.Id, cancellationToken);
    }

    public async Task<SalesmanStockStatement> ReturnAsync(
        long userId, IReadOnlyList<SalesmanStockLine> lines, string? note, long recordedByUserId,
        CancellationToken cancellationToken = default)
    {
        _ = await _users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User", userId);
        Validate(lines);

        var nowUtc = _clock.UtcNow;
        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        var (productIds, names, _) = await LockProductsAsync(uow, lines, cancellationToken);
        var his = await _salesmanStock.HoldingAsync(uow, userId, productIds, cancellationToken);

        foreach (var line in lines)
        {
            var carrying = his.GetValueOrDefault(line.ProductId);

            if (line.Quantity > carrying)
            {
                throw new BusinessRuleViolationException(
                    $"He is carrying {carrying} of '{names[line.ProductId]}' — {line.Quantity} cannot come back.");
            }
        }

        foreach (var line in lines)
        {
            await _salesmanStock.MoveAsync(
                uow, userId, line.ProductId, -line.Quantity, his[line.ProductId] - line.Quantity, SalesmanStockReason.Returned,
                referenceId: null, Trimmed(note), recordedByUserId, nowUtc, cancellationToken);
        }

        await uow.CommitAsync(cancellationToken);

        return await StatementAsync(userId, cancellationToken);
    }

    private async Task<Domain.Entities.User> FieldSalesmanAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User", userId);

        if (user.Job != StaffJob.FieldSales || !user.IsActive)
        {
            throw new BusinessRuleViolationException(
                $"{user.FullName} is not a field salesman. Stock is issued to the salesman who sells in the market.");
        }

        return user;
    }

    private static void Validate(IReadOnlyList<SalesmanStockLine> lines)
    {
        if (lines.Count == 0)
        {
            throw new BusinessRuleViolationException("Choose at least one product.");
        }

        if (lines.Any(line => line.Quantity <= 0))
        {
            throw new BusinessRuleViolationException("Each quantity must be more than zero.");
        }

        if (lines.Select(line => line.ProductId).Distinct().Count() != lines.Count)
        {
            throw new BusinessRuleViolationException("The same product appears more than once. Combine it into a single line.");
        }
    }

    /// <summary>Locks the product rows ordered by id — the lock every stock change takes, so nothing races.</summary>
    private async Task<(List<long> Ids, Dictionary<long, string> Names, Dictionary<long, int> OnHand)> LockProductsAsync(
        IUnitOfWork uow, IReadOnlyList<SalesmanStockLine> lines, CancellationToken cancellationToken)
    {
        var ids = lines.Select(line => line.ProductId).OrderBy(id => id).ToList();
        var names = new Dictionary<long, string>();
        var onHand = new Dictionary<long, int>();

        foreach (var id in ids)
        {
            var product = await _stock.LockProductAsync(uow, id, cancellationToken) ?? throw new NotFoundException("Product", id);
            names[id] = product.Name;
            onHand[id] = product.QuantityOnHand;
        }

        return (ids, names, onHand);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
