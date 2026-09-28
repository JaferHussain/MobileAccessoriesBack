using MoizPos.Application.Abstractions;
using MoizPos.Application.Time;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>Raised when credentials or a refresh token are rejected. Always 401.</summary>
public sealed class AuthenticationFailedException : DomainException
{
    public AuthenticationFailedException(string message)
        : base(ErrorCodes.Unauthenticated, message)
    {
    }
}

public interface IAuthService
{
    Task<AuthResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default);

    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Changes the signed-in user's own password, verifying the current one first.</summary>
    Task ChangePasswordAsync(
        long userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets another user's password without knowing the old one — for when a salesman forgets
    /// theirs. Admin-only at the controller.
    /// </summary>
    Task ResetPasswordAsync(
        long userId, string newPassword, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sign-in, token refresh and logout.
///
/// Refresh tokens rotate on every use: presenting one revokes it and issues a replacement. If a
/// token that was already used is presented again, that means it leaked — every live token for
/// that user is revoked rather than just the one (research.md R9).
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IClock _clock;
    private readonly int _refreshTokenDays;

    public AuthService(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IClock clock,
        int refreshTokenDays)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _clock = clock;
        _refreshTokenDays = refreshTokenDays;
    }

    public async Task<AuthResult> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByUsernameAsync(username, cancellationToken);

        // One message for every failure mode. Saying "no such user" would let anyone enumerate
        // who works at the shop.
        var credentialsValid = user is not null
                               && user.IsActive
                               && _passwordHasher.Verify(password, user.PasswordHash);

        if (!credentialsValid)
        {
            throw new AuthenticationFailedException("Incorrect username or password.");
        }

        return await IssueAsync(user!, cancellationToken);
    }

    public async Task<AuthResult> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new AuthenticationFailedException("A refresh token is required.");
        }

        var nowUtc = _clock.UtcNow;
        var hash = _tokenService.HashRefreshToken(refreshToken);
        var stored = await _refreshTokens.FindAsync(hash, cancellationToken);

        if (stored is null)
        {
            throw new AuthenticationFailedException("Invalid refresh token.");
        }

        if (stored.RevokedAtUtc is not null)
        {
            // Replay of an already-rotated token: assume it leaked and burn the whole chain.
            await _refreshTokens.RevokeAllForUserAsync(stored.UserId, nowUtc, cancellationToken);

            throw new AuthenticationFailedException(
                "This refresh token has already been used. Please sign in again.");
        }

        if (!stored.IsActive(nowUtc))
        {
            throw new AuthenticationFailedException("This session has expired. Please sign in again.");
        }

        var user = await _users.FindByIdAsync(stored.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            await _refreshTokens.RevokeAllForUserAsync(stored.UserId, nowUtc, cancellationToken);

            throw new AuthenticationFailedException("This account is no longer active.");
        }

        await _refreshTokens.RevokeAsync(stored.Id, nowUtc, cancellationToken);

        return await IssueAsync(user, cancellationToken);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var stored = await _refreshTokens.FindAsync(
            _tokenService.HashRefreshToken(refreshToken), cancellationToken);

        if (stored is not null)
        {
            await _refreshTokens.RevokeAsync(stored.Id, _clock.UtcNow, cancellationToken);
        }
    }

    public async Task ChangePasswordAsync(
        long userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(userId, cancellationToken)
            ?? throw new AuthenticationFailedException("This account no longer exists.");

        // Verifying the current password is what stops an unattended counter terminal being
        // used to lock the owner out of their own shop.
        if (!_passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            throw new AuthenticationFailedException("The current password is incorrect.");
        }

        ValidateNewPassword(newPassword, currentPassword);

        await ApplyNewPasswordAsync(userId, newPassword, cancellationToken);
    }

    public async Task ResetPasswordAsync(
        long userId,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        _ = await _users.FindByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User", userId);

        ValidateNewPassword(newPassword, currentPassword: null);

        await ApplyNewPasswordAsync(userId, newPassword, cancellationToken);
    }

    private static void ValidateNewPassword(string newPassword, string? currentPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            throw new BusinessRuleViolationException(
                "The new password must be at least 8 characters.");
        }

        if (currentPassword is not null
            && string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            throw new BusinessRuleViolationException(
                "The new password must be different from the current one.");
        }
    }

    private async Task ApplyNewPasswordAsync(
        long userId, string newPassword, CancellationToken cancellationToken)
    {
        await _users.UpdatePasswordAsync(
            userId, _passwordHasher.Hash(newPassword), cancellationToken);

        // Every existing session for this account is now stale. If the password was changed
        // because it leaked, leaving old refresh tokens alive would defeat the point.
        await _refreshTokens.RevokeAllForUserAsync(userId, _clock.UtcNow, cancellationToken);
    }

    private async Task<AuthResult> IssueAsync(User user, CancellationToken cancellationToken)
    {
        var nowUtc = _clock.UtcNow;

        var accessToken = _tokenService.CreateAccessToken(user, nowUtc);
        var refreshToken = _tokenService.CreateRefreshToken();
        var refreshExpiry = nowUtc.AddDays(_refreshTokenDays);

        await _refreshTokens.StoreAsync(
            user.Id,
            _tokenService.HashRefreshToken(refreshToken),
            refreshExpiry,
            nowUtc,
            cancellationToken);

        return new AuthResult(
            new TokenPair(accessToken, refreshToken, nowUtc.AddMinutes(60)),
            new AuthenticatedUser(user.Id, user.Username, user.FullName, user.Role));
    }
}
