using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;

namespace MoizPos.Infrastructure.Auth;

/// <summary>Signing and lifetime settings for issued tokens.</summary>
public sealed class JwtOptions
{
    public const int MinimumKeyLength = 32;

    public string Issuer { get; init; } = "MoizPos";

    public string Audience { get; init; } = "MoizPosCounter";

    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// 60 minutes: a salesman works a full shift, and forcing a re-login mid-sale would invite
    /// password sharing, which destroys the audit trail's meaning (research.md R9).
    /// </summary>
    public int AccessTokenMinutes { get; init; } = 60;

    public int RefreshTokenDays { get; init; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length < MinimumKeyLength)
        {
            throw new InvalidOperationException(
                $"Jwt:Key must be at least {MinimumKeyLength} characters. " +
                "Set it with: dotnet user-secrets set \"Jwt:Key\" \"<random value>\"");
        }

        if (AccessTokenMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException("Jwt:AccessTokenMinutes must be between 1 and 1440.");
        }

        if (RefreshTokenDays is < 1 or > 365)
        {
            throw new InvalidOperationException("Jwt:RefreshTokenDays must be between 1 and 365.");
        }
    }
}

/// <inheritdoc />
public sealed class JwtTokenService : ITokenService
{
    /// <summary>256 bits of entropy — a refresh token is a bearer credential in its own right.</summary>
    private const int RefreshTokenBytes = 32;

    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;

    public JwtTokenService(JwtOptions options)
    {
        options.Validate();

        _options = options;
        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
            SecurityAlgorithms.HmacSha256);
    }

    public string CreateAccessToken(User user, DateTime nowUtc)
    {
        // Invariant culture: a claim value must parse identically wherever it is read.
        var userId = user.Id.ToString(CultureInfo.InvariantCulture);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, userId),

            // The role claim is what the AdminOnly policy reads. Cost and profit confidentiality
            // ultimately rests on this one claim being correct (FR-040).
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: nowUtc,
            expires: nowUtc.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: _credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(RefreshTokenBytes));

    public string HashRefreshToken(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
