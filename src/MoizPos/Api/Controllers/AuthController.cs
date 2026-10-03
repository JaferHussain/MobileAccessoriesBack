using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Auth;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Application.Time;

namespace MoizPos.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IUserRepository _users;
    private readonly IClock _clock;

    public AuthController(IAuthService authService, IUserRepository users, IClock clock)
    {
        _authService = authService;
        _users = users;
        _clock = clock;
    }

    /// <summary>Signs in and returns an access/refresh token pair (FR-038).</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request.Username, request.Password, cancellationToken);

        // For the owner's Team screen: who signed in, when, and from what. Only a successful
        // sign-in, and never a token refresh — a working day would otherwise read as hundreds.
        await _users.RecordLoginAsync(
            result.User.Id,
            _clock.UtcNow,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null,
            cancellationToken);

        return Ok(ApiResponse<AuthResponse>.Ok(ToResponse(result)));
    }

    /// <summary>Rotates a refresh token for a new pair.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.RefreshAsync(request.RefreshToken, cancellationToken);

        return Ok(ApiResponse<AuthResponse>.Ok(ToResponse(result)));
    }

    /// <summary>Revokes the supplied refresh token. Always 204, even for an unknown token.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken)
    {
        await _authService.LogoutAsync(request.RefreshToken, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Changes your own password. Every other session for this account is signed out, so a
    /// password changed because it leaked actually ends the leak.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        await _authService.ChangePasswordAsync(
            CurrentUser.Id(User), request.CurrentPassword, request.NewPassword, cancellationToken);

        return NoContent();
    }

    private static AuthResponse ToResponse(AuthResult result) =>
        new(
            result.Tokens.AccessToken,
            result.Tokens.RefreshToken,
            result.Tokens.AccessTokenExpiresAtUtc,
            new AuthUserDto(
                result.User.Id,
                result.User.Username,
                result.User.FullName,
                result.User.Role.ToString(),
                result.User.Job?.ToString()));
}
