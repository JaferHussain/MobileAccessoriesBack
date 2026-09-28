using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;

namespace MoizPos.Api.Controllers;

public sealed record RestoreBackupRequest
{
    public string BackupFileName { get; init; } = string.Empty;

    /// <summary>Must be true. Restoring overwrites everything currently in the database.</summary>
    public bool Confirm { get; init; }
}

public sealed class RestoreBackupValidator : AbstractValidator<RestoreBackupRequest>
{
    public RestoreBackupValidator()
    {
        RuleFor(x => x.BackupFileName)
            .NotEmpty().WithMessage("Choose a backup to restore.");

        RuleFor(x => x.Confirm)
            .Equal(true)
            .WithMessage("Restoring replaces all current data. Confirm to continue.");
    }
}

/// <summary>
/// The audit trail (FR-041). Admin-only: it records every stock and balance change, which is
/// exactly the history a salesman should not be able to review or reason about.
/// </summary>
[ApiController]
[Route("api/admin/audit")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditRepository _audit;

    public AuditController(IAuditRepository audit) => _audit = audit;

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? entityType,
        [FromQuery] long? entityId,
        [FromQuery] long? userId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<AuditEntryRow>.Normalize(page, pageSize);

        var (items, total) = await _audit.SearchAsync(
            entityType, entityId, userId, from, to, normalizedPage, normalizedSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<AuditEntryRow>>.Ok(
            new PagedResult<AuditEntryRow>(items, normalizedPage, normalizedSize, total)));
    }
}

/// <summary>
/// Backup and restore (FR-046). Admin-only — a salesman must not be able to take a copy of the
/// shop's entire database, nor overwrite it.
/// </summary>
[ApiController]
[Route("api/admin/backups")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class BackupsController : ControllerBase
{
    private readonly IBackupService _backups;
    private readonly ILogger<BackupsController> _logger;

    public BackupsController(IBackupService backups, ILogger<BackupsController> logger)
    {
        _backups = backups;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var files = await _backups.ListAsync(cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<BackupFile>>.Ok(files));
    }

    /// <summary>Takes a backup now (FR-046).</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var result = await _backups.CreateAsync(cancellationToken);

        _logger.LogInformation(
            "Manual backup taken by user {UserId}: {FileName}",
            CurrentUser.Id(User), result.FileName);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<BackupResult>.Ok(result));
    }

    /// <summary>
    /// Restores a backup, replacing everything currently in the database (FR-047).
    ///
    /// The file is validated before anything is overwritten, so a truncated or wrong file leaves
    /// the shop's current data untouched rather than destroying it (spec edge case).
    /// </summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore(
        [FromBody] RestoreBackupRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Restore requested by user {UserId} from {FileName}",
            CurrentUser.Id(User), request.BackupFileName);

        await _backups.RestoreAsync(request.BackupFileName, cancellationToken);

        _logger.LogWarning("Restore completed from {FileName}", request.BackupFileName);

        return Ok(ApiResponse<object>.Ok(new { restored = request.BackupFileName }));
    }
}
