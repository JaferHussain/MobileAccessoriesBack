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
/// The exact URLs the counter app asks for.
///
/// <para>Every other test here builds its own URL from what the server declares, so all of them
/// passed while the browser got a 404 on every Print and Download: the client had been written
/// against a contract document that did not match the routes. A test that constructs the path the
/// same way the code under test does proves only that the code agrees with itself.</para>
///
/// <para>So these hold the literal strings from
/// <c>frontend/src/features/documents/documentApi.ts</c>. If that file changes a path, or a
/// controller route moves, one of these fails.</para>
///
/// <para><b>This is a mitigation, not a guarantee.</b> Nothing here can force the frontend to keep
/// using these strings — only an end-to-end test could. What it does is make the two sides
/// disagree loudly in CI rather than silently in the shop.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ClientDocumentRouteTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ClientDocumentRouteTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    // --- the literal paths documentApi.ts builds, with {id} where it interpolates ---
    private const string InvoicePdfPath = "/api/invoices/{0}/pdf";
    private const string PaymentReceiptPdfPath = "/api/customer-payments/{0}/pdf";
    private const string ShareLinkPath = "/api/documents/share-link";
    private const string ShareLinksPath = "/api/documents/share-links";
    private const string RevokePath = "/api/documents/share-links/{0}/revoke";

    /// <summary>Invariant, because a URL is not a thing the shop's locale gets a say in.</summary>
    private static string Path(string template, long id) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, template, id);

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
            new { name = $"Rte {Guid.NewGuid():N}"[..20] });

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

    [Fact]
    public async Task The_path_the_app_uses_for_an_invoice_pdf_is_served()
    {
        var admin = await AdminAsync();
        var invoiceId = await SaleAsync(admin);

        var response = await admin.GetAsync(Path(InvoicePdfPath, invoiceId));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "Print and Download call exactly this");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task The_path_the_app_uses_for_a_receipt_pdf_is_served()
    {
        var admin = await AdminAsync();

        var created = await admin.PostAsJsonAsync("/api/customers", new
        {
            name = $"Cust {Guid.NewGuid():N}"[..18],
        });

        var customerId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Something to pay against.
        await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 0m,
            paymentMethod = "Credit",
            items = new[]
            {
                new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m },
            },
        });

        var payment = await admin.PostAsJsonAsync(
            $"/api/customers/{customerId}/payments", new { amount = 500m, paymentMethod = "Cash" });

        var paymentId = (await payment.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("paymentId").GetInt64();

        var response = await admin.GetAsync(Path(PaymentReceiptPdfPath, paymentId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task The_path_the_app_uses_to_mint_a_share_link_is_served()
    {
        var admin = await AdminAsync();

        var response = await admin.PostAsJsonAsync(ShareLinkPath, new
        {
            documentType = "Invoice",
            referenceId = await SaleAsync(admin),
            mobileNumber = (string?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_paths_the_app_uses_to_list_and_revoke_links_are_served()
    {
        var admin = await AdminAsync();
        var invoiceId = await SaleAsync(admin);

        await admin.PostAsJsonAsync(
            ShareLinkPath, new { documentType = "Invoice", referenceId = invoiceId });

        var list = await admin.GetAsync(
            $"{ShareLinksPath}?documentType=Invoice&referenceId={invoiceId}");

        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var linkId = (await list.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.EnumerateArray().First().GetProperty("id").GetInt64();

        (await admin.PostAsync(Path(RevokePath, linkId), null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<long> ProductAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 400, 0,
                    1000, 500, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Rtp {Guid.NewGuid():N}"[..20] });
    }
}
