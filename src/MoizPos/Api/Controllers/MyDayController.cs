using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;

namespace MoizPos.Api.Controllers;

/// <summary>
/// The signed-in person's own day — the salesman's screen on his phone. Always the caller's own
/// figures: there is no user id to ask for, so nobody can read anyone else's through it.
/// <c>from</c> and <c>to</c> are shop-local days, both inclusive; today when left out.
/// </summary>
[ApiController]
[Route("api/my-day")]
[Authorize]
public sealed class MyDayController : ControllerBase
{
    private readonly ITeamService _team;

    public MyDayController(ITeamService team) => _team = team;

    [HttpGet]
    public async Task<IActionResult> Mine([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        Ok(ApiResponse<MyDay>.Ok(await _team.MineAsync(CurrentUser.Id(User), from, to, cancellationToken)));
}
