using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Documents;

/// <summary>
/// T157 — the share link and the one unauthenticated endpoint in the system.
///
/// These tests police the containment the plan promised: a hashed, expiring, revocable token
/// that resolves to exactly one document and tells a probe nothing.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DocumentSharingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public DocumentSharingTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private async Task<HttpClient> ClientAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var body = await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers
            .AuthenticationHeaderValue("Bearer", body!.Data!.GetProperty("accessToken").GetString());

        return client;
    }

    /// <summary>Creates a real sale so there is a genuine invoice to render.</summary>
    private async Task<(long InvoiceId, long CustomerId)> CreateSaleAsync(
        HttpClient admin, string? mobileNumber = "03001234567")
    {
        var product = await admin.PostAsJsonAsync("/api/products", new
        {
            name = $"Doc {Guid.NewGuid():N}"[..18],
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            costPrice = 800m, wholesalePrice = 0m, retailPrice = 0m, salePrice = 1100m,
            quantityOnHand = 10, minStockThreshold = 3,
        });

        var productId = (await product.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Prices and stock arrive with the first delivery now, not with the product.
        await _api.StockProductAsync(productId, quantity: 50, salePrice: 1100m);

        var customer = await admin.PostAsJsonAsync("/api/customers", new
        {
            name = $"Doc {Guid.NewGuid():N}"[..16],
            mobileNumber,
        });

        var customerId = (await customer.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();
        await _api.MarkUdhaarAsync(customerId);

        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 1000m,
            paymentMethod = "Partial",
            items = new[] { new { productId, quantity = 2, unitSalePrice = 1100m } },
        });

        sale.StatusCode.Should().Be(HttpStatusCode.Created);

        var invoiceId = (await sale.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();

        return (invoiceId, customerId);
    }

    private async Task<JsonElement> ShareAsync(HttpClient client, long invoiceId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/documents/share-link", new { documentType = "Invoice", referenceId = invoiceId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
    }

    private static string TokenFrom(JsonElement share) =>
        share.GetProperty("shareUrl").GetString()!.Split('/')[^1];

    // ================================================================
    //  The PDFs
    // ================================================================

    [Fact]
    public async Task An_invoice_renders_as_a_pdf()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);

        var response = await admin.GetAsync($"/api/invoices/{invoiceId}/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty();
        // Every PDF begins with %PDF.
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task Staff_can_produce_a_receipt_for_a_customer()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);

        var staff = await ClientAsync(UserRole.Staff);

        // Handing a customer their receipt is counter work.
        (await staff.GetAsync($"/api/invoices/{invoiceId}/pdf")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_payment_receipt_renders_as_a_pdf()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (_, customerId) = await CreateSaleAsync(admin);

        var payment = await admin.PostAsJsonAsync(
            $"/api/customers/{customerId}/payments", new { amount = 500m, paymentMethod = "Cash" });

        var paymentId = (await payment.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("paymentId").GetInt64();

        var response = await admin.GetAsync($"/api/customer-payments/{paymentId}/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }

    // ================================================================
    //  The share link — FR-044
    // ================================================================

    [Fact]
    public async Task A_share_link_carries_a_whatsapp_url_addressed_to_the_customer()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);

        var share = await ShareAsync(admin, invoiceId);

        share.GetProperty("shareUrl").GetString().Should().Contain("/api/public/documents/");

        var whatsApp = share.GetProperty("whatsAppUrl").GetString();
        whatsApp.Should().StartWith("https://api.whatsapp.com/send?phone=923001234567&text=");

        // wa.me cannot attach a file, so the receipt travels as a link inside the message.
        Uri.UnescapeDataString(whatsApp!).Should().Contain("/api/public/documents/");
    }

    [Fact]
    public async Task A_customer_with_no_mobile_number_gets_a_link_but_no_whatsapp_url()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin, mobileNumber: null);

        var share = await ShareAsync(admin, invoiceId);

        // The shopkeeper can still copy the link; the UI disables the send button (FR-044).
        share.GetProperty("shareUrl").GetString().Should().NotBeNullOrWhiteSpace();
        share.GetProperty("whatsAppUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_raw_token_is_never_stored()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);

        var token = TokenFrom(await ShareAsync(admin, invoiceId));

        await using var connection = await _api.OpenDatabaseAsync();

        var matches = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_tokens WHERE token_hash = @token;", new { token });

        // Only the SHA-256 hash is persisted, so a database leak hands over no live links.
        matches.Should().Be(0);

        var hashes = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_tokens WHERE CHAR_LENGTH(token_hash) = 64;");

        hashes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_token_carries_enough_entropy()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);

        var token = TokenFrom(await ShareAsync(admin, invoiceId));

        // 32 random bytes as URL-safe base64 without padding.
        token.Length.Should().BeGreaterThanOrEqualTo(43);
        token.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public async Task Two_share_links_never_share_a_token()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);

        var first = TokenFrom(await ShareAsync(admin, invoiceId));
        var second = TokenFrom(await ShareAsync(admin, invoiceId));

        second.Should().NotBe(first);
    }

    // ================================================================
    //  The public endpoint
    // ================================================================

    [Fact]
    public async Task A_customer_can_open_their_receipt_without_signing_in()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);
        var token = TokenFrom(await ShareAsync(admin, invoiceId));

        // No Authorization header — this is a customer's phone.
        var anonymous = _api.CreateClient();
        var response = await anonymous.GetAsync($"/api/public/documents/{token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await response.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task It_opens_in_the_browser_rather_than_forcing_a_download()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);
        var token = TokenFrom(await ShareAsync(admin, invoiceId));

        var response = await _api.CreateClient().GetAsync($"/api/public/documents/{token}");

        response.Content.Headers.ContentDisposition?.DispositionType
            .Should().BeOneOf("inline", null);
    }

    [Fact]
    public async Task An_unknown_token_is_not_found()
    {
        var response = await _api.CreateClient()
            .GetAsync("/api/public/documents/definitely-not-a-real-token-value-here");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_expired_token_is_indistinguishable_from_an_unknown_one()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);
        var token = TokenFrom(await ShareAsync(admin, invoiceId));

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE document_tokens SET expires_at_utc = UTC_TIMESTAMP(6) - INTERVAL 1 DAY;");
        }

        var expired = await _api.CreateClient().GetAsync($"/api/public/documents/{token}");
        var unknown = await _api.CreateClient().GetAsync("/api/public/documents/not-a-token");

        expired.StatusCode.Should().Be(HttpStatusCode.NotFound);
        expired.StatusCode.Should().Be(unknown.StatusCode, "a probe must learn nothing");
    }

    [Fact]
    public async Task A_revoked_token_stops_working()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);
        var token = TokenFrom(await ShareAsync(admin, invoiceId));

        (await _api.CreateClient().GetAsync($"/api/public/documents/{token}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE document_tokens SET revoked_at_utc = UTC_TIMESTAMP(6) WHERE revoked_at_utc IS NULL;");
        }

        (await _api.CreateClient().GetAsync($"/api/public/documents/{token}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_access_is_counted_and_timestamped()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (invoiceId, _) = await CreateSaleAsync(admin);
        var share = await ShareAsync(admin, invoiceId);
        var token = TokenFrom(share);

        var anonymous = _api.CreateClient();
        await anonymous.GetAsync($"/api/public/documents/{token}");
        await anonymous.GetAsync($"/api/public/documents/{token}");

        await using var connection = await _api.OpenDatabaseAsync();

        var row = await connection.QuerySingleAsync<(int Count, DateTime? LastAccess)>(
            """
            SELECT access_count, last_accessed_utc FROM document_tokens
            ORDER BY id DESC LIMIT 1;
            """);

        row.Count.Should().Be(2);
        row.LastAccess.Should().NotBeNull();
    }

    [Fact]
    public async Task The_share_link_endpoint_itself_still_requires_a_login()
    {
        var anonymous = _api.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/documents/share-link", new { documentType = "Invoice", referenceId = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_pdf_download_still_requires_a_login()
    {
        var anonymous = _api.CreateClient();

        (await anonymous.GetAsync("/api/invoices/1/pdf")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }
}
