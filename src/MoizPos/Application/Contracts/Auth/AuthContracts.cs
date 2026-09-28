using FluentValidation;

namespace MoizPos.Application.Contracts.Auth;

public sealed record LoginRequest(string Username, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record AuthUserDto(long Id, string Username, string FullName, string Role);

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    AuthUserDto User);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required.")
            .MaximumLength(50).WithMessage("Username cannot exceed 50 characters.");

        // Deliberately only checks presence, not strength: a strength rule here would reject a
        // legitimate existing password and turn sign-in into a guessing game.
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty().WithMessage("Your current password is required.");

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("A new password is required.")
            .MinimumLength(8).WithMessage("The new password must be at least 8 characters.");
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator() =>
        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithMessage("A refresh token is required.");
}
