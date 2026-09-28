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
/// What a prober can learn from the one unauthenticated endpoint. The answer must be: nothing.
///
/// <para>The existing suite already asserts that an unknown token 404s, that an expired one is
/// indistinguishable from it, and that a revoked one stops working. This adds the case those
/// leave open — that a <b>revoked</b> token is also indistinguishable, and that the responses
/// match in their <b>bodies</b>, not merely in their status codes (FR-118).</para>
///
/// <para>Why it matters: a differentiated response tells whoever is holding a dead link that it
/// once existed and was withdrawn. That is a fact about the shop's customers, leaked to someone
/// who by definition should no longer have access.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PublicDocumentProbingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public PublicDocumentProbingTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private async Task<HttpClient> AdminAsync()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<long> CreateSaleAsync(HttpClient admin)
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
            new { name = $"Probe {Guid.NewGuid():N}"[..20] });

        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m } },
        });

        sale.StatusCode.Should().Be(HttpStatusCode.Created);

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

    private Task<HttpResponseMessage> OpenAsync(string token) =>
        _api.CreateClient().GetAsync($"/api/public/documents/{token}");

    /// <summary>
    /// The response body with its trace id removed.
    ///
    /// <para>A trace id is unique to every request and is issued before the token is even looked
    /// at, so it is identical in kind for an unknown token, an expired one and a live one. It
    /// tells a prober nothing about which of those they hold — only that they made a request,
    /// which they already knew. Everything ELSE in the body must match exactly, and that is what
    /// these tests assert.</para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TraceId =
        new("\"traceId\":\"[^\"]*\"");

    private static async Task<string> BodyWithoutTraceIdAsync(HttpResponseMessage response) =>
        TraceId.Replace(await response.Content.ReadAsStringAsync(), "\"traceId\":\"<per-request>\"");

    [Fact]
    public async Task A_revoked_token_is_indistinguishable_from_one_that_never_existed()
    {
        var admin = await AdminAsync();
        var token = await ShareAsync(admin, await CreateSaleAsync(admin));

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE document_tokens SET revoked_at_utc = UTC_TIMESTAMP(6) WHERE token_hash IS NOT NULL;");
        }

        var revoked = await OpenAsync(token);
        var unknown = await OpenAsync("a-token-that-was-never-issued-at-all");

        revoked.StatusCode.Should().Be(HttpStatusCode.NotFound);
        revoked.StatusCode.Should().Be(unknown.StatusCode);

        // Bodies too, not just status. A body that differs is a body that tells a prober their
        // dead link once worked — a fact about a customer, handed to whoever now holds the link.
        var revokedBody = await BodyWithoutTraceIdAsync(revoked);
        var unknownBody = await BodyWithoutTraceIdAsync(unknown);

        revokedBody.Should().Be(unknownBody, "a probe must learn nothing from the difference");
    }

    [Fact]
    public async Task An_expired_token_matches_an_unknown_one_body_and_all()
    {
        var admin = await AdminAsync();
        var token = await ShareAsync(admin, await CreateSaleAsync(admin));

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE document_tokens SET expires_at_utc = UTC_TIMESTAMP(6) - INTERVAL 1 DAY;");
        }

        var expired = await OpenAsync(token);
        var unknown = await OpenAsync("another-token-that-never-existed");

        expired.StatusCode.Should().Be(unknown.StatusCode);
        (await BodyWithoutTraceIdAsync(expired))
            .Should().Be(await BodyWithoutTraceIdAsync(unknown));
    }

    /// <summary>
    /// The three dead states must agree with each other as well as with "unknown" — otherwise a
    /// prober can still separate "expired" from "revoked", which is the same leak in a smaller box.
    /// </summary>
    [Fact]
    public async Task Expired_and_revoked_are_indistinguishable_from_each_other()
    {
        var admin = await AdminAsync();

        var expiredToken = await ShareAsync(admin, await CreateSaleAsync(admin));

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE document_tokens SET expires_at_utc = UTC_TIMESTAMP(6) - INTERVAL 1 DAY;");
        }

        var revokedToken = await ShareAsync(admin, await CreateSaleAsync(admin));

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE document_tokens SET revoked_at_utc = UTC_TIMESTAMP(6)
                WHERE revoked_at_utc IS NULL AND expires_at_utc > UTC_TIMESTAMP(6);
                """);
        }

        var expired = await OpenAsync(expiredToken);
        var revoked = await OpenAsync(revokedToken);

        expired.StatusCode.Should().Be(revoked.StatusCode);
        (await BodyWithoutTraceIdAsync(expired))
            .Should().Be(await BodyWithoutTraceIdAsync(revoked));
    }
}
