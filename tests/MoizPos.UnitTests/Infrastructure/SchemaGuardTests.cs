using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using MoizPos.Api.Middleware;

namespace MoizPos.UnitTests.Infrastructure;

/// <summary>
/// A deploy that skipped <c>migrate</c>.
///
/// <para>Seen in the shop: new code went live against a database without its new columns, and
/// EVERY screen said "An unexpected error occurred" — the right answer was one command away, and
/// nothing on screen said so. The guard says so, on every API request, until the database is
/// brought up to date.</para>
/// </summary>
public sealed class SchemaGuardTests
{
    private static async Task<(DefaultHttpContext Context, bool NextRan)> SendAsync(
        IReadOnlyList<string> pending, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var nextRan = false;
        var guard = new SchemaGuardMiddleware(_ => { nextRan = true; return Task.CompletedTask; }, pending);

        await guard.InvokeAsync(context);

        return (context, nextRan);
    }

    [Fact]
    public async Task With_updates_waiting_every_api_request_is_told_to_run_migrate()
    {
        var (context, nextRan) = await SendAsync(["0030_transaction_proofs.sql"], "/api/customers");

        nextRan.Should().BeFalse("the request would only fail on a missing column");
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        context.Response.Body.Position = 0;
        var error = JsonDocument.Parse(context.Response.Body).RootElement.GetProperty("error");

        error.GetProperty("code").GetString().Should().Be("DATABASE_OUT_OF_DATE");
        error.GetProperty("message").GetString().Should()
            .Contain("migrate").And.Contain("0030_transaction_proofs").And.NotContain("unexpected");
    }

    [Fact]
    public async Task With_nothing_waiting_it_steps_aside()
    {
        var (context, nextRan) = await SendAsync([], "/api/customers");

        nextRan.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/")]
    [InlineData("/assets/index.js")]
    public async Task The_page_itself_and_the_health_check_still_load(string path)
    {
        // The app must still open, so the message has somewhere to be read; and a health check
        // that went dark would hide the one thing worth knowing.
        var (_, nextRan) = await SendAsync(["0030_transaction_proofs.sql"], path);

        nextRan.Should().BeTrue();
    }
}
