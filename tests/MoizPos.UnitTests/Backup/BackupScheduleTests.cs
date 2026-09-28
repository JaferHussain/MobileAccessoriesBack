using FluentAssertions;
using MoizPos.Application.Time;
using MoizPos.Infrastructure.Backup;

namespace MoizPos.UnitTests.Backup;

/// <summary>
/// T166 — the daily backup schedule, driven by a mockable clock so a day need not pass.
///
/// The rule is "at most one per shop-local day, at or after the configured hour" rather than a
/// timer, so a shop machine switched off overnight still gets its backup when next turned on
/// instead of silently skipping the day (FR-045).
/// </summary>
public sealed class BackupScheduleTests
{
    private static readonly PeriodResolver Periods = new();

    /// <summary>Runs at 02:00 shop-local.</summary>
    private static BackupSchedule Schedule() => new(runAtLocalHour: 2, Periods);

    /// <summary>Builds a UTC instant from a shop-local wall-clock time. Karachi is UTC+05:00.</summary>
    private static DateTime LocalToUtc(int year, int month, int day, int hour, int minute = 0) =>
        DateTime.SpecifyKind(
            new DateTime(year, month, day, hour, minute, 0).AddHours(-5), DateTimeKind.Utc);

    [Fact]
    public void A_brand_new_installation_backs_up_once_the_hour_arrives()
    {
        Schedule().IsDue(LocalToUtc(2026, 9, 9, 2, 5), lastBackupUtc: null)
            .Should().BeTrue();
    }

    [Fact]
    public void Nothing_runs_before_the_configured_hour()
    {
        Schedule().IsDue(LocalToUtc(2026, 9, 9, 1, 59), lastBackupUtc: null)
            .Should().BeFalse();
    }

    [Fact]
    public void A_backup_taken_today_is_not_repeated_today()
    {
        var schedule = Schedule();
        var lastBackup = LocalToUtc(2026, 9, 9, 2, 1);

        schedule.IsDue(LocalToUtc(2026, 9, 9, 14, 0), lastBackup).Should().BeFalse();
        schedule.IsDue(LocalToUtc(2026, 9, 9, 23, 59), lastBackup).Should().BeFalse();
    }

    [Fact]
    public void The_next_day_is_due_again()
    {
        Schedule()
            .IsDue(LocalToUtc(2026, 9, 10, 2, 30), LocalToUtc(2026, 9, 9, 2, 1))
            .Should().BeTrue();
    }

    [Fact]
    public void A_machine_switched_off_overnight_still_backs_up_when_it_returns()
    {
        // The shop opens at 09:00 having missed 02:00 entirely — the day must not be skipped.
        Schedule()
            .IsDue(LocalToUtc(2026, 9, 10, 9, 0), LocalToUtc(2026, 9, 9, 2, 1))
            .Should().BeTrue();
    }

    [Fact]
    public void A_gap_of_several_days_still_produces_only_one_backup_today()
    {
        var schedule = Schedule();
        var lastBackup = LocalToUtc(2026, 9, 1, 2, 1);

        schedule.IsDue(LocalToUtc(2026, 9, 10, 9, 0), lastBackup).Should().BeTrue();

        // Once today's has run, no more today.
        schedule.IsDue(LocalToUtc(2026, 9, 10, 18, 0), LocalToUtc(2026, 9, 10, 9, 1))
            .Should().BeFalse();
    }

    [Fact]
    public void The_day_boundary_follows_shop_local_time_not_utc()
    {
        // 23:30 local on the 9th is 18:30 UTC; 00:30 local on the 10th is 19:30 UTC the same
        // UTC day. The second must count as a new day.
        var lastBackup = LocalToUtc(2026, 9, 9, 23, 30);

        Schedule().IsDue(LocalToUtc(2026, 9, 10, 2, 30), lastBackup).Should().BeTrue();
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(99)]
    public void An_out_of_range_hour_is_clamped_rather_than_disabling_backups(int configuredHour)
    {
        var schedule = new BackupSchedule(configuredHour, Periods);

        // Whatever was configured, the shop still gets a backup at some point in the day.
        schedule.IsDue(LocalToUtc(2026, 9, 9, 23, 59), lastBackupUtc: null).Should().BeTrue();
    }
}
