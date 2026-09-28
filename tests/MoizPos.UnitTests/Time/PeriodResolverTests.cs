using FluentAssertions;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;

namespace MoizPos.UnitTests.Time;

/// <summary>
/// T021 — reporting period boundaries. Every transaction must land in exactly one period under
/// shop-local time (FR-034). Pakistan observes no daylight saving, so the offset is a constant
/// +05:00 (research.md R6), but the boundary arithmetic still has to be right.
/// </summary>
public sealed class PeriodResolverTests
{
    private static readonly PeriodResolver Resolver = new();

    /// <summary>13:30 on 9 September 2026, Karachi time, expressed as UTC.</summary>
    private static readonly DateTime NowUtc = new(2026, 9, 9, 8, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Today_starts_at_local_midnight_expressed_in_utc()
    {
        var range = Resolver.Resolve(DashboardPeriod.Today, NowUtc);

        // 2026-09-09 00:00 Karachi == 2026-09-08 19:00 UTC
        range.StartUtc.Should().Be(new DateTime(2026, 9, 8, 19, 0, 0, DateTimeKind.Utc));
        range.EndUtc.Should().Be(new DateTime(2026, 9, 9, 19, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Today_is_exactly_twenty_four_hours()
    {
        var range = Resolver.Resolve(DashboardPeriod.Today, NowUtc);

        (range.EndUtc - range.StartUtc).Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void This_month_spans_the_local_calendar_month()
    {
        var range = Resolver.Resolve(DashboardPeriod.ThisMonth, NowUtc);

        // 1 Sep 2026 00:00 Karachi -> 31 Aug 2026 19:00 UTC; 1 Oct 2026 00:00 Karachi -> 30 Sep 19:00 UTC
        range.StartUtc.Should().Be(new DateTime(2026, 8, 31, 19, 0, 0, DateTimeKind.Utc));
        range.EndUtc.Should().Be(new DateTime(2026, 9, 30, 19, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void This_year_spans_the_local_calendar_year()
    {
        var range = Resolver.Resolve(DashboardPeriod.ThisYear, NowUtc);

        range.StartUtc.Should().Be(new DateTime(2025, 12, 31, 19, 0, 0, DateTimeKind.Utc));
        range.EndUtc.Should().Be(new DateTime(2026, 12, 31, 19, 0, 0, DateTimeKind.Utc));
    }

    // --- the edge case the spec calls out: a sale near local midnight ---

    [Fact]
    public void A_sale_at_one_minute_to_local_midnight_falls_in_that_day()
    {
        // 2026-09-09 23:59 Karachi == 2026-09-09 18:59 UTC
        var saleUtc = new DateTime(2026, 9, 9, 18, 59, 0, DateTimeKind.Utc);
        var range = Resolver.Resolve(DashboardPeriod.Today, NowUtc);

        range.Contains(saleUtc).Should().BeTrue();
    }

    [Fact]
    public void A_sale_at_local_midnight_falls_in_the_next_day_not_this_one()
    {
        // 2026-09-10 00:00 Karachi == 2026-09-09 19:00 UTC — the exclusive end of "today".
        var saleUtc = new DateTime(2026, 9, 9, 19, 0, 0, DateTimeKind.Utc);
        var today = Resolver.Resolve(DashboardPeriod.Today, NowUtc);

        today.Contains(saleUtc).Should().BeFalse("the range end is exclusive");
    }

    [Fact]
    public void Consecutive_days_do_not_overlap_and_leave_no_gap()
    {
        var day1 = Resolver.Resolve(DashboardPeriod.Today, NowUtc);
        var day2 = Resolver.Resolve(DashboardPeriod.Today, NowUtc.AddDays(1));

        day1.EndUtc.Should().Be(day2.StartUtc);
    }

    [Fact]
    public void A_sale_on_the_last_day_of_the_year_falls_in_that_year()
    {
        // 2026-12-31 23:30 Karachi == 2026-12-31 18:30 UTC
        var saleUtc = new DateTime(2026, 12, 31, 18, 30, 0, DateTimeKind.Utc);
        var year = Resolver.Resolve(DashboardPeriod.ThisYear, NowUtc);

        year.Contains(saleUtc).Should().BeTrue();
    }

    [Fact]
    public void A_sale_just_after_local_new_year_falls_in_the_following_year()
    {
        // 2027-01-01 00:30 Karachi == 2026-12-31 19:30 UTC — still 2026 in UTC, but 2027 locally.
        var saleUtc = new DateTime(2026, 12, 31, 19, 30, 0, DateTimeKind.Utc);
        var year2026 = Resolver.Resolve(DashboardPeriod.ThisYear, NowUtc);

        year2026.Contains(saleUtc).Should().BeFalse(
            "local time had already rolled into 2027 even though UTC had not");
    }

    // --- explicit date ranges used by the report screens ---

    [Fact]
    public void An_explicit_local_date_range_is_inclusive_of_the_final_day()
    {
        var range = Resolver.ResolveLocalDateRange(
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 30));

        range.StartUtc.Should().Be(new DateTime(2026, 8, 31, 19, 0, 0, DateTimeKind.Utc));
        // Exclusive end = midnight after 30 Sep local.
        range.EndUtc.Should().Be(new DateTime(2026, 9, 30, 19, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_single_day_range_covers_that_whole_day()
    {
        var range = Resolver.ResolveLocalDateRange(new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 9));

        (range.EndUtc - range.StartUtc).Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Rejects_a_range_whose_end_precedes_its_start()
    {
        var act = () => Resolver.ResolveLocalDateRange(
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 1));

        act.Should().Throw<ArgumentException>();
    }
}
