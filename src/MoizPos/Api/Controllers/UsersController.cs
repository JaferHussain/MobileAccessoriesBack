using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Controllers;

public sealed record CreateUserRequest
{
    public string Username { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public UserRole Role { get; init; } = UserRole.Staff;
}

public sealed record UserDto(long Id, string Username, string FullName, string Role, bool IsActive);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordValidator() =>
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("A new password is required.")
            .MinimumLength(8).WithMessage("The new password must be at least 8 characters.");
}

public sealed class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required.")
            .MinimumLength(3).WithMessage("Username must be at least 3 characters.")
            .MaximumLength(50).WithMessage("Username cannot exceed 50 characters.")
            .Matches("^[A-Za-z0-9._-]+$")
            .WithMessage("Username may contain only letters, numbers, dots, dashes and underscores.");

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(100).WithMessage("Full name cannot exceed 100 characters.");

        // Enforced at creation, unlike sign-in, where a strength rule would lock out an existing
        // account (data-model.md §1).
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.");
    }
}

/// <summary>
/// User accounts (FR-039).
///
/// Admin-only: creating logins is how someone would grant themselves access to cost and profit,
/// so a salesman must never be able to do it.
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        IUserRepository users,
        IPasswordHasher passwordHasher,
        ILogger<UsersController> logger)
    {
        _users = users;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var users = await _users.ListAsync(cancellationToken);

        var dtos = users
            .Select(u => new UserDto(u.Id, u.Username, u.FullName, u.Role.ToString(), u.IsActive))
            .ToList();

        return Ok(ApiResponse<IReadOnlyList<UserDto>>.Ok(dtos));
    }

    /// <summary>Creates a login, e.g. when the owner hires a salesman (spec US6).</summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();

        if (await _users.FindByUsernameAsync(username, cancellationToken) is not null)
        {
            throw new BusinessRuleViolationException($"The username '{username}' is already taken.");
        }

        var id = await _users.CreateAsync(
            new User
            {
                Username = username,
                FullName = request.FullName.Trim(),
                PasswordHash = _passwordHasher.Hash(request.Password),
                Role = request.Role,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
            },
            cancellationToken);

        _logger.LogInformation(
            "User {Username} created with role {Role} by user {CreatedBy}",
            username, request.Role, CurrentUser.Id(User));

        return StatusCode(
            StatusCodes.Status201Created,
            ApiResponse<UserDto>.Ok(
                new UserDto(id, username, request.FullName.Trim(), request.Role.ToString(), true)));
    }

    /// <summary>
    /// Sets someone else's password — for when a salesman forgets theirs. Their existing
    /// sessions end immediately.
    /// </summary>
    [HttpPost("{id:long}/reset-password")]
    public async Task<IActionResult> ResetPassword(
        long id,
        [FromBody] ResetPasswordRequest request,
        [FromServices] IAuthService authService,
        CancellationToken cancellationToken)
    {
        await authService.ResetPasswordAsync(id, request.NewPassword, cancellationToken);

        _logger.LogWarning(
            "Password for user {UserId} reset by user {ResetBy}", id, CurrentUser.Id(User));

        return NoContent();
    }

    /// <summary>
    /// Deactivates a login — for a departed employee. Never deleted, so the audit trail keeps
    /// naming who did what.
    /// </summary>
    [HttpPost("{id:long}/deactivate")]
    public async Task<IActionResult> Deactivate(long id, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("User", id);

        if (id == CurrentUser.Id(User))
        {
            throw new BusinessRuleViolationException("You cannot deactivate your own account.");
        }

        // A shop locked out of its own system has no way back in without database access.
        if (user.Role == UserRole.Admin && await _users.CountActiveAdminsAsync(cancellationToken) <= 1)
        {
            throw new BusinessRuleViolationException(
                "This is the only active administrator. Create another before deactivating this one.");
        }

        await _users.SetActiveAsync(id, isActive: false, cancellationToken);

        _logger.LogWarning(
            "User {UserId} deactivated by user {DeactivatedBy}", id, CurrentUser.Id(User));

        return NoContent();
    }
}
