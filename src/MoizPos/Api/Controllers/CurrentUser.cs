using System.Globalization;
using System.Security.Claims;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Controllers;

/// <summary>
/// Reads the signed-in user from the token.
///
/// Every stock and ledger mutation records who made it (FR-041), so a controller that cannot
/// identify the caller must fail rather than guess.
/// </summary>
public static class CurrentUser
{
    public static long Id(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            throw new AuthenticationFailedRequestException();
        }

        return id;
    }

    public static UserRole Role(ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Admin)) ? UserRole.Admin : UserRole.Staff;

    public static bool IsAdmin(ClaimsPrincipal principal) => Role(principal) == UserRole.Admin;
}

/// <summary>The token was accepted but carries no usable user id.</summary>
public sealed class AuthenticationFailedRequestException : DomainException
{
    public AuthenticationFailedRequestException()
        : base(ErrorCodes.Unauthenticated, "The signed-in user could not be identified.")
    {
    }
}
