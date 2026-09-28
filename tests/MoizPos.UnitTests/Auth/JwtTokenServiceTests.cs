using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;
using MoizPos.Infrastructure.Auth;

namespace MoizPos.UnitTests.Auth;

/// <summary>T030 — access token contents and refresh token properties (research.md R9).</summary>
public sealed class JwtTokenServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 9, 8, 30, 0, DateTimeKind.Utc);

    private static JwtOptions Options() => new()
    {
        Issuer = "MoizPos",
        Audience = "MoizPosCounter",
        Key = "a-test-signing-key-of-at-least-32-characters",
        AccessTokenMinutes = 60,
        RefreshTokenDays = 30,
    };

    private static JwtTokenService Service() => new(Options());

    private static User Staff() => new()
    {
        Id = 7,
        Username = "salesman",
        FullName = "Test Salesman",
        Role = UserRole.Staff,
    };

    private static JwtSecurityToken Read(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    [Fact]
    public void Access_token_carries_the_user_id_and_role()
    {
        var jwt = Read(Service().CreateAccessToken(Staff(), NowUtc));

        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "7");
        jwt.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Staff");
    }

    [Fact]
    public void Access_token_carries_the_admin_role_for_an_admin()
    {
        var admin = new User { Id = 1, Username = "admin", FullName = "Owner", Role = UserRole.Admin };

        var jwt = Read(Service().CreateAccessToken(admin, NowUtc));

        jwt.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Admin");
    }

    [Fact]
    public void Access_token_expires_after_sixty_minutes()
    {
        var jwt = Read(Service().CreateAccessToken(Staff(), NowUtc));

        jwt.ValidTo.Should().BeCloseTo(NowUtc.AddMinutes(60), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Access_token_carries_the_configured_issuer_and_audience()
    {
        var jwt = Read(Service().CreateAccessToken(Staff(), NowUtc));

        jwt.Issuer.Should().Be("MoizPos");
        jwt.Audiences.Should().Contain("MoizPosCounter");
    }

    [Fact]
    public void Access_token_never_contains_the_password_hash()
    {
        var user = Staff();
        user.PasswordHash = "210000.abc.def";

        var token = Service().CreateAccessToken(user, NowUtc);

        token.Should().NotContain("210000.abc.def");
    }

    [Fact]
    public void Each_access_token_has_a_unique_id()
    {
        var service = Service();

        var first = Read(service.CreateAccessToken(Staff(), NowUtc)).Id;
        var second = Read(service.CreateAccessToken(Staff(), NowUtc)).Id;

        first.Should().NotBe(second);
    }

    [Fact]
    public void Refresh_tokens_are_unique_and_high_entropy()
    {
        var service = Service();

        var tokens = Enumerable.Range(0, 100).Select(_ => service.CreateRefreshToken()).ToList();

        tokens.Distinct().Should().HaveCount(100);
        // 32 random bytes -> 44 base64 characters.
        tokens.Should().OnlyContain(t => t.Length >= 43);
    }

    [Fact]
    public void Refresh_token_hash_is_stable_and_fits_the_column()
    {
        var service = Service();
        var token = service.CreateRefreshToken();

        var hash = service.HashRefreshToken(token);

        // refresh_tokens.token_hash is CHAR(64) (data-model.md §2).
        hash.Should().HaveLength(64);
        service.HashRefreshToken(token).Should().Be(hash);
    }

    [Fact]
    public void Different_refresh_tokens_hash_differently()
    {
        var service = Service();

        service.HashRefreshToken("token-a").Should().NotBe(service.HashRefreshToken("token-b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void Rejects_a_weak_signing_key(string key)
    {
        var act = () => new JwtTokenService(new JwtOptions { Key = key });

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 characters*");
    }

    [Fact]
    public void Rejects_an_absurd_access_token_lifetime()
    {
        var act = () => new JwtTokenService(new JwtOptions
        {
            Key = "a-test-signing-key-of-at-least-32-characters",
            AccessTokenMinutes = 0,
        });

        act.Should().Throw<InvalidOperationException>();
    }
}
