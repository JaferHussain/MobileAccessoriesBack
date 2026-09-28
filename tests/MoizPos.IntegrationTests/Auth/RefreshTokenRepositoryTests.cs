using Dapper;
using FluentAssertions;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Enums;
using MoizPos.Infrastructure.Repositories;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Auth;

/// <summary>Direct coverage of refresh-token storage, rotation and revocation.</summary>
[Collection(ApiCollection.Name)]
public sealed class RefreshTokenRepositoryTests
{
    private readonly ApiFactory _api;

    public RefreshTokenRepositoryTests(ApiFactory api) => _api = api;

    private IRefreshTokenRepository Repository() =>
        new RefreshTokenRepository(
            new MoizPos.Infrastructure.Data.MySqlConnectionFactory(_api.ConnectionString));

    private static string Hash() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).PadRight(64, '0')[..64];

    [Fact]
    public async Task Stores_and_reads_back_a_token()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var repository = Repository();
        var hash = Hash();
        var now = DateTime.UtcNow;

        await repository.StoreAsync(userId, hash, now.AddDays(30), now);

        var stored = await repository.FindAsync(hash);

        stored.Should().NotBeNull();
        stored!.UserId.Should().Be(userId);
        stored.RevokedAtUtc.Should().BeNull();
        stored.IsActive(now).Should().BeTrue();
    }

    [Fact]
    public async Task Returns_null_for_an_unknown_token()
    {
        (await Repository().FindAsync(Hash())).Should().BeNull();
    }

    [Fact]
    public async Task Revoking_marks_the_token_inactive()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var repository = Repository();
        var hash = Hash();
        var now = DateTime.UtcNow;

        await repository.StoreAsync(userId, hash, now.AddDays(30), now);
        var stored = await repository.FindAsync(hash);

        await repository.RevokeAsync(stored!.Id, now);

        var after = await repository.FindAsync(hash);
        after!.RevokedAtUtc.Should().NotBeNull();
        after.IsActive(now).Should().BeFalse();
    }

    [Fact]
    public async Task Revoking_all_burns_every_live_token_for_that_user()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var repository = Repository();
        var now = DateTime.UtcNow;

        var first = Hash();
        var second = Hash();
        await repository.StoreAsync(userId, first, now.AddDays(30), now);
        await repository.StoreAsync(userId, second, now.AddDays(30), now);

        await repository.RevokeAllForUserAsync(userId, now);

        (await repository.FindAsync(first))!.IsActive(now).Should().BeFalse();
        (await repository.FindAsync(second))!.IsActive(now).Should().BeFalse();
    }

    [Fact]
    public async Task An_expired_token_is_not_active()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var repository = Repository();
        var hash = Hash();
        var now = DateTime.UtcNow;

        await repository.StoreAsync(userId, hash, now.AddDays(-1), now.AddDays(-31));

        (await repository.FindAsync(hash))!.IsActive(now).Should().BeFalse();
    }

    [Fact]
    public async Task Timestamps_come_back_as_utc()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var repository = Repository();
        var hash = Hash();
        var now = DateTime.UtcNow;

        await repository.StoreAsync(userId, hash, now.AddDays(30), now);

        var stored = await repository.FindAsync(hash);

        stored!.ExpiresAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Token_hash_is_unique()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var repository = Repository();
        var hash = Hash();
        var now = DateTime.UtcNow;

        await repository.StoreAsync(userId, hash, now.AddDays(30), now);

        var act = async () => await repository.StoreAsync(userId, hash, now.AddDays(30), now);

        await act.Should().ThrowAsync<Exception>();
    }
}
