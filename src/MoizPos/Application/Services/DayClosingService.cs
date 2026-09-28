using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Time;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>What the day's records say passed through the drawer, before anyone counts it.</summary>
/// <param name="CashPaidOut">
/// Expenses recorded as paid from the till. Expenses with no source recorded are NOT counted —
/// they predate the question and guessing at them would put an invented figure into a
/// reconciliation whose whole value is that it is not invented.
/// </param>
/// <param name="CashToSuppliers">
/// Supplier bills settled in cash. Kept apart from <paramref name="CashPaidOut"/> because buying
/// stock and paying the electricity bill are different questions, and a shopkeeper reading a
/// short wants to know which of the two moved.
/// </param>
public sealed record DayCashMovement(
    decimal CashSales,
    decimal CashRecovery,
    decimal CashRefunds,
    decimal CashPaidOut,
    decimal CashToSuppliers);

/// <summary>A day's close, whether already saved or previewed.</summary>
public sealed record DayClosingDto
{
    public DateOnly ClosingDate { get; init; }

    public decimal OpeningFloat { get; init; }

    public decimal CashSales { get; init; }

    public decimal CashRecovery { get; init; }

    public decimal CashRefunds { get; init; }

    public decimal CashPaidOut { get; init; }

    public decimal CashToSuppliers { get; init; }

    public decimal ExpectedCash { get; init; }

    public decimal CountedCash { get; init; }

    public decimal Difference { get; init; }

    public string? Note { get; init; }

    public string? ClosedByUserName { get; init; }

    public DateTime? ClosedAtUtc { get; init; }

    /// <summary>False while this is only a preview — nothing has been counted or saved yet.</summary>
    public bool IsClosed { get; init; }
}

public sealed record CloseDayRequest
{
    public DateOnly ClosingDate { get; init; }

    public decimal OpeningFloat { get; init; }

    public decimal CountedCash { get; init; }

    public string? Note { get; init; }
}

public interface IDayClosingService
{
    /// <summary>What the day took, before it is counted. Saves nothing.</summary>
    Task<DayClosingDto> PreviewAsync(
        DateOnly closingDate, decimal openingFloat, CancellationToken cancellationToken = default);

    Task<DayClosingDto> CloseAsync(
        CloseDayRequest request, long userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DayClosingDto>> RecentAsync(
        int count, CancellationToken cancellationToken = default);
}

/// <summary>
/// Counting the drawer at the end of a trading day.
///
/// <para><b>The only control the shop has over physical cash.</b> Every other figure reconciles
/// against itself — a cash sale recorded perfectly and pocketed leaves no trace in any report,
/// because the sale really was recorded. Counting the notes against what the day took is what
/// makes that visible.</para>
///
/// <para><b>Figures are snapshotted at closing, never recomputed.</b> A return taken tomorrow
/// against a sale made today must not rewrite what was counted last night. A closing is evidence
/// of one evening, and evidence that changes afterwards is not evidence.</para>
/// </summary>
public sealed class DayClosingService : IDayClosingService
{
    private readonly IDayClosingRepository _closings;
    private readonly PeriodResolver _periods;
    private readonly IClock _clock;

    public DayClosingService(
        IDayClosingRepository closings, PeriodResolver periods, IClock clock)
    {
        _closings = closings;
        _periods = periods;
        _clock = clock;
    }

    public async Task<DayClosingDto> PreviewAsync(
        DateOnly closingDate,
        decimal openingFloat,
        CancellationToken cancellationToken = default)
    {
        // Already closed? Show what was recorded, not a fresh calculation — the saved figures
        // are the evidence, and recomputing them would quietly contradict it.
        var existing = await _closings.FindByDateAsync(closingDate, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var movement = await MovementAsync(closingDate, cancellationToken);

        var count = CashDrawer.Reconcile(
            openingFloat, movement.CashSales, movement.CashRecovery,
            movement.CashRefunds, movement.CashPaidOut, movement.CashToSuppliers, counted: 0m);

        return new DayClosingDto
        {
            ClosingDate = closingDate,
            OpeningFloat = openingFloat,
            CashSales = movement.CashSales,
            CashRecovery = movement.CashRecovery,
            CashRefunds = movement.CashRefunds,
            CashPaidOut = movement.CashPaidOut,
            CashToSuppliers = movement.CashToSuppliers,
            ExpectedCash = count.Expected,
            // Nothing counted yet. Zero here is "not answered", and the screen asks for it.
            CountedCash = 0m,
            Difference = 0m,
            IsClosed = false,
        };
    }

    public async Task<DayClosingDto> CloseAsync(
        CloseDayRequest request,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_periods.ToShopLocal(_clock.UtcNow));

        if (request.ClosingDate > today)
        {
            throw new BusinessRuleViolationException("A day cannot be closed before it has happened.");
        }

        // One closing per day. A day that could be closed twice would let a short be closed away
        // and reopened at a more comfortable figure.
        if (await _closings.FindByDateAsync(request.ClosingDate, cancellationToken) is not null)
        {
            throw new BusinessRuleViolationException(
                $"{request.ClosingDate:dd MMM yyyy} has already been closed.");
        }

        var movement = await MovementAsync(request.ClosingDate, cancellationToken);

        var count = CashDrawer.Reconcile(
            request.OpeningFloat, movement.CashSales, movement.CashRecovery,
            movement.CashRefunds, movement.CashPaidOut, movement.CashToSuppliers,
            request.CountedCash);

        var nowUtc = _clock.UtcNow;

        await _closings.InsertAsync(
            request.ClosingDate, request.OpeningFloat, movement,
            count.Expected, count.Counted, count.Difference,
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            userId, nowUtc, cancellationToken);

        return await _closings.FindByDateAsync(request.ClosingDate, cancellationToken)
               ?? throw new NotFoundException("Day closing", request.ClosingDate.DayNumber);
    }

    public Task<IReadOnlyList<DayClosingDto>> RecentAsync(
        int count, CancellationToken cancellationToken = default) =>
        _closings.RecentAsync(count, cancellationToken);

    /// <summary>The day's cash movement, resolved against the shop's own day, not a UTC one.</summary>
    private Task<DayCashMovement> MovementAsync(
        DateOnly closingDate, CancellationToken cancellationToken)
    {
        var range = _periods.ResolveLocalDateRange(closingDate, closingDate);

        return _closings.CashMovementAsync(range, cancellationToken);
    }
}
