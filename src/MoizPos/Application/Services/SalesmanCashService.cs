using MoizPos.Application.Abstractions;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

public sealed record SalesmanCashMovement(
    string Kind, long ReferenceId, string? Reference, DateTime EntryDateUtc, decimal Amount, string? Method, string? Detail,
    /// <summary>The effect on his cash in hand: + for money taken, − for money given back or handed over.</summary>
    decimal Effect,
    /// <summary>A handover by transfer has its screenshot attached.</summary>
    bool HasProof = false);

/// <summary>The cash a field salesman carries: collected − refunded − handed over = in hand.</summary>
public sealed record SalesmanCashStatement(
    long UserId,
    string FullName,
    string? Job,
    decimal Collected,
    decimal Refunded,
    decimal HandedOver,
    decimal InHand,
    IReadOnlyList<SalesmanCashMovement> Movements,
    /// <summary>Set only in answer to a handover just recorded — so its proof can be attached straight after.</summary>
    long? HandoverId = null);

public interface ISalesmanCashService
{
    Task<SalesmanCashStatement> StatementAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>Records money a salesman handed over. Never more than he is holding.</summary>
    Task<SalesmanCashStatement> ReceiveAsync(
        long userId, decimal amount, PaymentMethod method, string? note, long receivedByUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The cash a field salesman carries between the market and the shop.
///
/// <para>Worked out from what he recorded — never a figure he types: cash taken at his sales and
/// udhaar recoveries in the field, less cash refunds he gave there, less what he has handed over.
/// These are exactly the amounts day close leaves out of the counter drawer, so the two agree:
/// money not in the drawer is in his hand until he hands it over, and then it is in the drawer
/// (cash) or in a shop account (transfer).</para>
/// </summary>
public sealed class SalesmanCashService : ISalesmanCashService
{
    private readonly ISalesmanCashRepository _cash;
    private readonly IUserRepository _users;
    private readonly IClock _clock;

    public SalesmanCashService(ISalesmanCashRepository cash, IUserRepository users, IClock clock)
    {
        _cash = cash;
        _users = users;
        _clock = clock;
    }

    public async Task<SalesmanCashStatement> StatementAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User", userId);

        var movements = (await _cash.MovementsAsync(userId, cancellationToken))
            .Select(row => new SalesmanCashMovement(
                row.Kind, row.ReferenceId, row.Reference, row.EntryDateUtc, row.Amount, row.Method, row.Detail,
                row.Kind is "Sale" or "Recovery" ? row.Amount : -row.Amount,
                row.HasProof))
            .ToList();

        var collected = movements.Where(m => m.Kind is "Sale" or "Recovery").Sum(m => m.Amount);
        var refunded = movements.Where(m => m.Kind == "Refund").Sum(m => m.Amount);
        var handedOver = movements.Where(m => m.Kind == "Handover").Sum(m => m.Amount);

        return new SalesmanCashStatement(
            user.Id, user.FullName, user.Job?.ToString(),
            collected, refunded, handedOver, collected - refunded - handedOver, movements);
    }

    public async Task<SalesmanCashStatement> ReceiveAsync(
        long userId, decimal amount, PaymentMethod method, string? note, long receivedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (method is PaymentMethod.Credit or PaymentMethod.Partial)
        {
            throw new BusinessRuleViolationException("Money is handed over in cash or by transfer.");
        }

        var statement = await StatementAsync(userId, cancellationToken);

        if (amount > statement.InHand)
        {
            throw new BusinessRuleViolationException(
                $"{statement.FullName} is holding Rs {statement.InHand:N2}. Record only what he actually handed over.");
        }

        var handoverId = await _cash.InsertHandoverAsync(
            userId, amount, method, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), receivedByUserId, _clock.UtcNow,
            cancellationToken);

        return await StatementAsync(userId, cancellationToken) with { HandoverId = handoverId };
    }
}
