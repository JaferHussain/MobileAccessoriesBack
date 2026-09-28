using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Auth;

/// <summary>
/// Changing a password. A system where the seeded password can never be changed is not usable,
/// and a change that leaves old sessions alive does not actually end a leak.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ChangePasswordTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ChangePasswordTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private async Task<(HttpClient Client, string RefreshToken)> SignInAsync(
        string username, string password)
    {
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers
            .AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return (client, data.GetProperty("refreshToken").GetString()!);
    }

    [Fact]
    public async Task A_user_can_change_their_own_password()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        var (client, _) = await SignInAsync(username, password);

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword = "BrandNew@2026" });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The new one works...
        var withNew = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { username, password = "BrandNew@2026" });
        withNew.StatusCode.Should().Be(HttpStatusCode.OK);

        // ...and the old one does not.
        var withOld = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { username, password });
        withOld.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_current_password_must_be_correct()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        var (client, _) = await SignInAsync(username, password);

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = "not-the-right-one", newPassword = "BrandNew@2026" });

        // An unattended counter terminal must not be usable to lock the owner out.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var stillWorks = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { username, password });
        stillWorks.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_a_password_ends_every_other_session()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Staff);

        // Two devices signed in as the same person.
        var (phone, phoneRefresh) = await SignInAsync(username, password);
        var (counter, _) = await SignInAsync(username, password);

        await counter.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword = "BrandNew@2026" });

        // The other device cannot renew — which is the point of changing a leaked password.
        var refresh = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken = phoneRefresh });

        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        phone.Dispose();
    }

    [Fact]
    public async Task A_short_new_password_is_refused()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        var (client, _) = await SignInAsync(username, password);

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword = "short" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reusing_the_same_password_is_refused()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        var (client, _) = await SignInAsync(username, password);

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = password, newPassword = password });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Changing_a_password_requires_a_login()
    {
        var response = await _api.CreateClient().PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword = "anything", newPassword = "BrandNew@2026" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- admin reset

    [Fact]
    public async Task An_admin_can_reset_a_forgotten_password()
    {
        var (staffId, staffName, _) = await _api.CreateUserAsync(UserRole.Staff);
        var (_, adminName, adminPassword) = await _api.CreateUserAsync(UserRole.Admin);
        var (admin, _) = await SignInAsync(adminName, adminPassword);

        var response = await admin.PostAsJsonAsync(
            $"/api/admin/users/{staffId}/reset-password", new { newPassword = "Reset@12345" });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var login = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { username = staffName, password = "Reset@12345" });

        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Staff_cannot_reset_anyones_password()
    {
        var (adminId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var (_, staffName, staffPassword) = await _api.CreateUserAsync(UserRole.Staff);
        var (staff, _) = await SignInAsync(staffName, staffPassword);

        // Otherwise a salesman could take over the owner's account and read every cost price.
        var response = await staff.PostAsJsonAsync(
            $"/api/admin/users/{adminId}/reset-password", new { newPassword = "Hijack@12345" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resetting_an_unknown_user_is_not_found()
    {
        var (_, adminName, adminPassword) = await _api.CreateUserAsync(UserRole.Admin);
        var (admin, _) = await SignInAsync(adminName, adminPassword);

        var response = await admin.PostAsJsonAsync(
            "/api/admin/users/999999999/reset-password", new { newPassword = "Reset@12345" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
