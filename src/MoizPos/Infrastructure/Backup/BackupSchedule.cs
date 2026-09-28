using MoizPos.Application.Abstractions;
using MoizPos.Application.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MoizPos.Infrastructure.Backup;

/// <summary>
/// Decides whether a daily backup is due.
///
/// Pure and clock-driven so the schedule can be tested without waiting a day (T166). The rule is
/// deliberately "at most one per shop-local day, at or after the configured hour" rather than a
/// timer: a shop machine that is switched off overnight still gets its backup when it is next
/// turned on, instead of silently skipping the day.
/// </summary>
public sealed class BackupSchedule
{
    private readonly int _runAtLocalHour;
    private readonly PeriodResolver _periods;

    public BackupSchedule(int runAtLocalHour, PeriodResolver periods)
    {
        _runAtLocalHour = Math.Clamp(runAtLocalHour, 0, 23);
        _periods = periods;
    }

    /// <param name="lastBackupUtc">When the last backup ran, or null if there has never been one.</param>
    public bool IsDue(DateTime nowUtc, DateTime? lastBackupUtc)
    {
        var localNow = _periods.ToShopLocal(nowUtc);

        if (localNow.Hour < _runAtLocalHour)
        {
            return false;
        }

        if (lastBackupUtc is null)
        {
            // A brand-new installation gets its first backup as soon as the hour arrives.
            return true;
        }

        var localLast = _periods.ToShopLocal(lastBackupUtc.Value);

        return localLast.Date < localNow.Date;
    }
}

/// <summary>
/// Runs the daily backup (FR-045) without anyone remembering to do it.
///
/// Wakes periodically and asks <see cref="BackupSchedule"/> whether one is due, rather than
/// sleeping until a fixed time — a laptop that was closed overnight still gets its backup.
/// </summary>
public sealed class BackupHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly IBackupService _backups;
    private readonly BackupSchedule _schedule;
    private readonly IClock _clock;
    private readonly ILogger<BackupHostedService> _logger;

    public BackupHostedService(
        IBackupService backups,
        BackupSchedule schedule,
        IClock clock,
        ILogger<BackupHostedService> logger)
    {
        _backups = backups;
        _schedule = schedule;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed backup must never take the shop's till offline.
                _logger.LogError(ex, "The scheduled backup failed. The shop is still running.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        var existing = await _backups.ListAsync(cancellationToken);
        var lastBackupUtc = existing.Count == 0 ? null : (DateTime?)existing[0].CreatedAtUtc;

        if (!_schedule.IsDue(_clock.UtcNow, lastBackupUtc))
        {
            return;
        }

        var result = await _backups.CreateAsync(cancellationToken);

        _logger.LogInformation(
            "Daily backup written: {FileName} ({SizeBytes} bytes)", result.FileName, result.SizeBytes);

        var pruned = await _backups.PruneAsync(cancellationToken);

        if (pruned > 0)
        {
            _logger.LogInformation("Removed {Count} backup(s) past the retention window.", pruned);
        }
    }
}
