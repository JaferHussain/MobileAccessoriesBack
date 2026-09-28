using MoizPos.Application.Services;
using MoizPos.Application.Time;

namespace MoizPos.Application.Abstractions;

/// <summary>
/// Reading what passed through the drawer, and recording what was counted.
/// </summary>
public interface IDayClosingRepository
{
    /// <summary>
    /// What the day's records say moved through the till, for one shop-local day.
    ///
    /// <para><b>Cash only.</b> A bank transfer or a wallet payment never entered the drawer, so
    /// including it would make the drawer look short by exactly the amount that arrived
    /// electronically — training the shopkeeper to ignore the difference, which is the one thing
    /// this feature must not do.</para>
    /// </summary>
    Task<DayCashMovement> CashMovementAsync(
        DateRangeUtc range, CancellationToken cancellationToken = default);

    Task InsertAsync(
        DateOnly closingDate,
        decimal openingFloat,
        DayCashMovement movement,
        decimal expectedCash,
        decimal countedCash,
        decimal difference,
        string? note,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<DayClosingDto?> FindByDateAsync(
        DateOnly closingDate, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DayClosingDto>> RecentAsync(
        int count, CancellationToken cancellationToken = default);
}
