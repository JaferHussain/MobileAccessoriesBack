using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Auth;

/// <summary>
/// T032 / T026 — the login, refresh and logout endpoints through the real pipeline, plus the
/// error envelope shape every client depends on.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthEndpointsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public AuthEndpointsTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message, ErrorDetailBody[]? Details, string? TraceId);

    private sealed record ErrorDetailBody(string Field, string Message);

    private sealed record AuthBody(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserBody User);

    private sealed record UserBody(long Id, string Username, string FullName, string Role);

    private async Task<Envelope<T>> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<T>>(Json))!;

    private async Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        await _api.CreateClient().PostAsJsonAsync("/api/auth/login", new { username, password });

    // ------------------------------------------------------------------ login

    [Fact]
    public async Task Login_with_correct_credentials_returns_a_token_pair()
    {
        var (id, username, password) = await _api.CreateUserAsync(UserRole.Admin);

        var response = await LoginAsync(username, password);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync<AuthBody>(response);
        body.Success.Should().BeTrue();
        body.Error.Should().BeNull();
        body.Data!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.Data.RefreshToken.Should().NotBeNullOrWhiteSpace();
        body.Data.User.Id.Should().Be(id);
        body.Data.User.Username.Should().Be(username);
        body.Data.User.Role.Should().Be("Admin");
    }

    [Fact]
    public async Task Login_returns_the_staff_role_for_a_staff_user()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Staff);

        var body = await ReadAsync<AuthBody>(await LoginAsync(username, password));

        body.Data!.User.Role.Should().Be("Staff");
    }

    [Fact]
    public async Task Login_never_returns_the_password_hash()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);

        var raw = await (await LoginAsync(username, password)).Content.ReadAsStringAsync();

        raw.Should().NotContain("passwordHash");
        raw.Should().NotContain("password_hash");
        raw.Should().NotContain(password);
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_rejected()
    {
        var (_, username, _) = await _api.CreateUserAsync(UserRole.Admin);

        var response = await LoginAsync(username, "definitely-not-the-password");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await ReadAsync<object>(response);
        body.Success.Should().BeFalse();
        body.Error!.Code.Should().Be("UNAUTHENTICATED");
    }

    [Fact]
    public async Task Login_with_an_unknown_user_gives_the_same_message_as_a_wrong_password()
    {
        var (_, username, _) = await _api.CreateUserAsync(UserRole.Admin);

        var wrongPassword = await ReadAsync<object>(await LoginAsync(username, "wrong"));
        var unknownUser = await ReadAsync<object>(await LoginAsync("no_such_user_here", "wrong"));

        // Identical responses: otherwise anyone could enumerate who works at the shop.
        unknownUser.Error!.Message.Should().Be(wrongPassword.Error!.Message);
    }

    [Fact]
    public async Task Login_with_a_missing_password_is_a_validation_failure()
    {
        var response = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { username = "someone", password = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await ReadAsync<object>(response);
        body.Error!.Code.Should().Be("VALIDATION_FAILED");
        body.Error.Details.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Every_error_response_carries_a_trace_id()
    {
        var body = await ReadAsync<object>(await LoginAsync("nobody", "wrong"));

        body.Error!.TraceId.Should().NotBeNullOrWhiteSpace();
    }

    // ---------------------------------------------------------------- refresh

    [Fact]
    public async Task Refresh_returns_a_new_pair_and_rotates_the_refresh_token()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        var first = (await ReadAsync<AuthBody>(await LoginAsync(username, password))).Data!;

        var response = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = (await ReadAsync<AuthBody>(response)).Data!;
        second.RefreshToken.Should().NotBe(first.RefreshToken, "refresh tokens rotate on use");
        second.User.Username.Should().Be(username);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_is_rejected_and_burns_the_chain()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        var first = (await ReadAsync<AuthBody>(await LoginAsync(username, password))).Data!;

        var second = (await ReadAsync<AuthBody>(await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken }))).Data!;

        // Replay the already-used token.
        var replay = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Reuse means the token leaked, so the replacement is revoked too.
        var afterBurn = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken = second.RefreshToken });

        afterBurn.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_with_an_unknown_token_is_rejected()
    {
        var response = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "not-a-real-token" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ----------------------------------------------------------------- logout

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        var tokens = (await ReadAsync<AuthBody>(await LoginAsync(username, password))).Data!;

        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var logout = await client.PostAsJsonAsync(
            "/api/auth/logout", new { refreshToken = tokens.RefreshToken });

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterLogout = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });

        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_requires_authentication()
    {
        var response = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/logout", new { refreshToken = "anything" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------- endpoint defaults

    [Fact]
    public async Task Health_is_reachable_without_a_token()
    {
        var response = await _api.CreateClient().GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
