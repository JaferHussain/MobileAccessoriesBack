using MoizPos.Domain.Enums;

namespace MoizPos.Application.Time;

/// <summary>
/// A half-open UTC range <c>[StartUtc, EndUtc)</c>. Half-open so consecutive periods neither
/// overlap nor leave a gap — every transaction lands in exactly one period (FR-034).
/// </summary>
public readonly record struct DateRangeUtc(DateTime StartUtc, DateTime EndUtc)
{
    public bool Contains(DateTime instantUtc) => instantUtc >= StartUtc && instantUtc < EndUtc;
}

/// <summary>The current instant, abstracted so period logic is unit-testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <inheritdoc />
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Turns a reporting period into the UTC range to query.
///
/// Timestamps are stored in UTC but the shop thinks in local time: "today's sales" means the
/// Karachi day, not the UTC day. Pakistan observes no daylight saving, so the offset is a
/// constant +05:00 — but resolving through <see cref="TimeZoneInfo"/> anyway keeps this correct
/// if that ever changes (research.md R6).
/// </summary>
public sealed class PeriodResolver
{
    private readonly TimeZoneInfo _shopTimeZone;

    public PeriodResolver()
        : this(ResolveShopTimeZone())
    {
    }

    public PeriodResolver(TimeZoneInfo shopTimeZone) => _shopTimeZone = shopTimeZone;

    public DateRangeUtc Resolve(DashboardPeriod period, DateTime nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _shopTimeZone);

        var (localStart, localEnd) = period switch
        {
            DashboardPeriod.Today => (
                localNow.Date,
                localNow.Date.AddDays(1)),

            DashboardPeriod.ThisMonth => (
                new DateTime(localNow.Year, localNow.Month, 1),
                new DateTime(localNow.Year, localNow.Month, 1).AddMonths(1)),

            DashboardPeriod.ThisYear => (
                new DateTime(localNow.Year, 1, 1),
                new DateTime(localNow.Year, 1, 1).AddYears(1)),

            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown period."),
        };

        return new DateRangeUtc(ToUtc(localStart), ToUtc(localEnd));
    }

    /// <summary>
    /// An explicit local date range from a report filter. Both dates are inclusive to the user —
    /// 1 Sep to 30 Sep covers all of September — so the exclusive end is midnight after
    /// <paramref name="toLocal"/>.
    /// </summary>
    public DateRangeUtc ResolveLocalDateRange(DateOnly fromLocal, DateOnly toLocal)
    {
        if (toLocal < fromLocal)
        {
            throw new ArgumentException(
                $"Range end ({toLocal:yyyy-MM-dd}) cannot precede its start ({fromLocal:yyyy-MM-dd}).",
                nameof(toLocal));
        }

        var localStart = fromLocal.ToDateTime(TimeOnly.MinValue);
        var localEnd = toLocal.AddDays(1).ToDateTime(TimeOnly.MinValue);

        return new DateRangeUtc(ToUtc(localStart), ToUtc(localEnd));
    }

    /// <summary>
    /// Converts a stored UTC instant to shop-local time. Documents show the time the customer
    /// was actually standing at the counter, not a UTC one they would not recognise.
    /// </summary>
    public DateTime ToShopLocal(DateTime instantUtc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc), _shopTimeZone);

    private DateTime ToUtc(DateTime localUnspecified)
    {
        var unspecified = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, _shopTimeZone);
    }

    /// <summary>
    /// .NET 6+ accepts IANA ids on Windows, but fall back to the Windows id if the ICU data is
    /// unavailable so the API still starts on a bare server.
    /// </summary>
    private static TimeZoneInfo ResolveShopTimeZone()
    {
        foreach (var id in new[] { "Asia/Karachi", "Pakistan Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next identifier.
            }
            catch (InvalidTimeZoneException)
            {
                // Try the next identifier.
            }
        }

        // Pakistan Standard Time is a fixed UTC+05:00 with no daylight saving.
        return TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "Pakistan Time", "PKT");
    }
}
