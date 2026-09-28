using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Documents;

/// <summary>
/// Withdrawing a link that should not have gone out.
///
/// <para>This is the containment that makes an unauthenticated link acceptable at all. Until now
/// the column and the repository method both existed and <b>nothing called them</b> — revocation
/// was reachable only by editing the database.</para>
///
/// <para>Listing comes with it, because revoking what you cannot see is not an action anyone can
/// take. The listing returns ids, never tokens: only the hash is stored, and re-exposing a live
/// link through an authenticated screen would turn whoever is reading over the owner's shoulder
/// into a link holder.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ShareLinkRevocationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ShareLinkRevocationTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private async Task<HttpClient> ClientAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<long> SaleAsync(HttpClient admin)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var productId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 800, 0,
                    1100, 50, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Rev {Guid.NewGuid():N}"[..20] });

        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m } },
        });

        return (await sale.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();
    }

    private async Task<string> ShareAsync(HttpClient admin, long invoiceId)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/documents/share-link", new { documentType = "Invoice", referenceId = invoiceId });

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        return data.GetProperty("shareUrl").GetString()!.Split('/')[^1];
    }

    private async Task<List<JsonElement>> ListAsync(HttpClient client, long invoiceId)
    {
        var response = await client.GetAsync(
            $"/api/documents/share-links?documentType=Invoice&referenceId={invoiceId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.EnumerateArray().ToList();
    }

    private Task<HttpResponseMessage> RevokeAsync(HttpClient client, long linkId) =>
        client.PostAsync($"/api/documents/share-links/{linkId}/revoke", null);

    private Task<HttpResponseMessage> OpenAsync(string token) =>
        _api.CreateClient().GetAsync($"/api/public/documents/{token}");

    // ================================================================
    //  Seeing what is outstanding
    // ================================================================

    [Fact]
    public async Task The_owner_can_see_the_links_issued_for_a_document()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin);
        await ShareAsync(admin, invoiceId);

        var link = (await ListAsync(admin, invoiceId)).Single();

        link.GetProperty("id").GetInt64().Should().BeGreaterThan(0);
        link.GetProperty("expiresAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
        link.GetProperty("revokedAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        link.GetProperty("isUsable").GetBoolean().Should().BeTrue();
        link.GetProperty("createdByUserName").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_listing_never_hands_back_the_token_itself()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin);
        var token = await ShareAsync(admin, invoiceId);

        var raw = (await ListAsync(admin, invoiceId)).Single().GetRawText();

        // Only the hash is stored. Re-exposing a live link through an authenticated screen would
        // turn a bystander reading over the owner's shoulder into a link holder.
        raw.Should().NotContain(token);
        raw.ToLowerInvariant().Should().NotContain("token");
    }

    [Fact]
    public async Task The_listing_says_whether_a_link_has_ever_been_opened()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin);
        var token = await ShareAsync(admin, invoiceId);

        (await ListAsync(admin, invoiceId)).Single()
            .GetProperty("lastAccessedAtUtc").ValueKind.Should().Be(JsonValueKind.Null);

        (await OpenAsync(token)).StatusCode.Should().Be(HttpStatusCode.OK);

        // "Was this ever opened, and when" is the question a worried owner actually asks.
        (await ListAsync(admin, invoiceId)).Single()
            .GetProperty("lastAccessedAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    // ================================================================
    //  Withdrawing one
    // ================================================================

    [Fact]
    public async Task Revoking_a_link_stops_it_opening()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin);
        var token = await ShareAsync(admin, invoiceId);

        (await OpenAsync(token)).StatusCode.Should().Be(HttpStatusCode.OK);

        var linkId = (await ListAsync(admin, invoiceId)).Single().GetProperty("id").GetInt64();
        (await RevokeAsync(admin, linkId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await OpenAsync(token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Revoking_twice_is_not_an_error_and_does_not_move_the_timestamp()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin);
        await ShareAsync(admin, invoiceId);

        var linkId = (await ListAsync(admin, invoiceId)).Single().GetProperty("id").GetInt64();

        var first = await RevokeAsync(admin, linkId);
        var firstAt = (await first.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("revokedAtUtc").GetDateTime();

        var second = await RevokeAsync(admin, linkId);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var secondAt = (await second.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("revokedAtUtc").GetDateTime();

        // A second click must not be an error, and must not rewrite when the withdrawal actually
        // happened — that timestamp is evidence.
        secondAt.Should().Be(firstAt);
    }

    [Fact]
    public async Task Revoking_one_link_leaves_another_for_the_same_document_working()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin);

        var first = await ShareAsync(admin, invoiceId);
        var second = await ShareAsync(admin, invoiceId);

        var links = await ListAsync(admin, invoiceId);
        links.Should().HaveCount(2);

        var firstId = links.Single(l =>
            l.GetProperty("id").GetInt64() == links.Min(x => x.GetProperty("id").GetInt64()))
            .GetProperty("id").GetInt64();

        await RevokeAsync(admin, firstId);

        // Sharing is not a one-shot action, and withdrawing one link must not poison the document.
        var stillWorks = (await OpenAsync(first)).StatusCode == HttpStatusCode.OK
                         || (await OpenAsync(second)).StatusCode == HttpStatusCode.OK;

        stillWorks.Should().BeTrue();
    }

    [Fact]
    public async Task Revoking_a_link_that_does_not_exist_is_not_found()
    {
        var admin = await ClientAsync(UserRole.Admin);

        (await RevokeAsync(admin, 999_999_999)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ================================================================
    //  Whose job this is
    // ================================================================

    [Fact]
    public async Task A_salesman_may_not_list_the_links_for_a_document()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin);
        await ShareAsync(admin, invoiceId);

        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync(
            $"/api/documents/share-links?documentType=Invoice&referenceId={invoiceId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_salesman_may_not_revoke()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin);
        await ShareAsync(admin, invoiceId);

        var linkId = (await ListAsync(admin, invoiceId)).Single().GetProperty("id").GetInt64();
        var staff = await ClientAsync(UserRole.Staff);

        // Revoking is containment over something already released. A salesman who mis-sent a
        // bill tells the owner; sharing itself stays open to them.
        (await RevokeAsync(staff, linkId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
