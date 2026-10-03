using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MoizPos.Application.Abstractions;

namespace MoizPos.IntegrationTests.Infrastructure;

/// <summary>
/// What the shopkeeper sees when the database cannot be reached.
///
/// <para>It used to be "An unexpected error occurred. Quote the trace id" — which, arriving on the
/// Returns screen, read as a bug in supplier returns. The cause was a dropped connection to the
/// hosted database. Now it is a 503 that says so, in words someone at the counter can act on.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DatabaseUnavailableTests
{
    private readonly ApiFactory _api;

    public DatabaseUnavailableTests(ApiFactory api) => _api = api;

    private sealed class UnreachableDatabase : IDbConnectionFactory
    {
        public Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default) =>
            throw new DatabaseUnavailableException(new TimeoutException("Connect Timeout expired."));
    }

    [Fact]
    public async Task An_unreachable_database_is_a_503_that_says_so_in_words()
    {
        using var unreachable = _api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IDbConnectionFactory, UnreachableDatabase>()));

        var client = unreachable.CreateClient();

        // Signing in is the first thing that touches the database, and needs no token.
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "x", password = "y" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");

        error.GetProperty("code").GetString().Should().Be("DATABASE_UNAVAILABLE");
        error.GetProperty("message").GetString().Should()
            .Contain("Could not reach the database")
            .And.NotContain("unexpected");
    }
}
