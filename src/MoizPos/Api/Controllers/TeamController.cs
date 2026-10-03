using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;

namespace MoizPos.Api.Controllers;

/// <summary>
/// The owner's view of the team: a card per person, each person's activity, and the watch list.
/// Admin only — this is the owner looking at the shop, and staff never see one another's figures.
/// <c>from</c> and <c>to</c> are shop-local days, both inclusive; today when left out.
/// </summary>
[ApiController]
[Route("api/team")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class TeamController : ControllerBase
{
    private readonly ITeamService _team;

    public TeamController(ITeamService team) => _team = team;

    [HttpGet]
    public async Task<IActionResult> Members([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<TeamMemberRow>>.Ok(await _team.MembersAsync(from, to, cancellationToken)));

    [HttpGet("{userId:long}/activity")]
    public async Task<IActionResult> Activity(
        long userId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<TeamActivityRow>>.Ok(await _team.ActivityAsync(userId, from, to, cancellationToken)));

    /// <summary>Things worth a look — big discounts, same-day returns, transfers without proof, transfer refunds.</summary>
    [HttpGet("watchlist")]
    public async Task<IActionResult> WatchList([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<WatchItemRow>>.Ok(await _team.WatchListAsync(from, to, cancellationToken)));
}
