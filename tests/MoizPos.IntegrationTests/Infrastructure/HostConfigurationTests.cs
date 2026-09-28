using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MoizPos.Application.Abstractions;

namespace MoizPos.IntegrationTests.Infrastructure;

/// <summary>
/// Which database does the hosted API actually write to?
///
/// <para><c>DatabaseFixture.GuardAgainstNonTestDatabase</c> protects the fixture's own connection.
/// The API is a separate host with its own configuration, and if any source outranks the
/// connection string the factory injects, every test in the suite writes somewhere else.</para>
///
/// <para>That happened. <c>appsettings.{Environment}.local.json</c> was added to
/// <c>Program.cs</c> as a per-machine override; it is added last, and the connection string is
/// read moments later to build the connection factory — before the factory's own
/// <c>ConfigureAppConfiguration</c> is applied. On a machine whose local file named the live
/// server, the suite ran against it and 201 tests failed at login, because their users had been
/// created in the test database. <c>ApiFactory</c> now sets <c>SkipMachineLocalSettings</c>.</para>
///
/// <para><b>Asserting on <c>IConfiguration</c> alone is not enough</b> — it showed the correct test
/// database while the repositories were already built against the wrong one. These tests check the
/// connection the data layer actually holds, and prove a write lands where the fixture can see it.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HostConfigurationTests
{
    private readonly ApiFactory _api;

    public HostConfigurationTests(ApiFactory api) => _api = api;

    [Fact]
    public async Task The_data_layer_is_connected_to_the_fixtures_test_database()
    {
        using var scope = _api.Services.CreateScope();

        // The connection the repositories were built with — not what configuration says now.
        await using var connection = await scope.ServiceProvider
            .GetRequiredService<IDbConnectionFactory>()
            .OpenAsync();

        var database = await connection.ExecuteScalarAsync<string>("SELECT DATABASE();");

        database.Should().NotBeNullOrWhiteSpace();
        database.Should().Contain(
            "test",
            "the hosted API must write to a test database — these tests create and drop schema, "
            + "and a machine-local settings file must never be able to redirect them at live data");
    }

    [Fact]
    public async Task A_write_through_the_api_lands_where_the_fixture_can_see_it()
    {
        // The end-to-end version of the same guarantee, and the one that actually broke: the API
        // host and the fixture must be looking at the same rows, or every login in the suite fails.
        var (userId, username, _) = await _api.CreateUserAsync(MoizPos.Domain.Enums.UserRole.Admin);

        using var scope = _api.Services.CreateScope();

        await using var connection = await scope.ServiceProvider
            .GetRequiredService<IDbConnectionFactory>()
            .OpenAsync();

        var found = await connection.ExecuteScalarAsync<string>(
            "SELECT username FROM users WHERE id = @userId;", new { userId });

        found.Should().Be(
            username,
            "a user created through the fixture must be visible to the API's own connection");
    }

    [Fact]
    public void Configuration_agrees_with_the_fixture()
    {
        using var scope = _api.Services.CreateScope();

        var resolved = scope.ServiceProvider
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("Default");

        resolved.Should().Be(_api.ConnectionString);
    }
}
