using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;

namespace MoizPos.Api.Controllers;

/// <summary>
/// Everyone who owes the shop money, most overdue first. Open to any signed-in user, like the
/// customer list and receiving a payment: collecting a debt is everyone's job, and balances are the
/// shop's receivables, not its margins — nothing here reveals cost or profit.
/// </summary>
[ApiController]
[Route("api/recovery")]
[Authorize]
public sealed class RecoveryController : ControllerBase
{
    private readonly IRecoveryService _recovery;

    public RecoveryController(IRecoveryService recovery) => _recovery = recovery;

    [HttpGet]
    public async Task<IActionResult> Report(CancellationToken cancellationToken) =>
        Ok(ApiResponse<RecoveryReport>.Ok(await _recovery.ReportAsync(cancellationToken)));
}
