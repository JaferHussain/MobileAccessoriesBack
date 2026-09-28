using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace MoizPos.IntegrationTests.Invoices;

/// <summary>
/// Feature 008 — the screenshot behind a non-cash payment.
///
/// <para>Cash is its own proof; a bank transfer is a claim. These tests hold three things: the
/// proof reaches the invoice it belongs to, a Cash sale is refused one (there is nothing to
/// prove, and allowing it would quietly invite proof on sales that never had a transfer), and
/// the sale itself never depends on the picture — the counter cannot be held up because a
/// customer's screenshot is slow to arrive.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PaymentProofTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public PaymentProofTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message);

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

    private async Task<long> CreateProductAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 800, 0,
                    1000, 50, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Pp {Guid.NewGuid():N}"[..20] });
    }

    private async Task<long> SellAsync(HttpClient client, string paymentMethod)
    {
        var productId = await CreateProductAsync();

        var response = await client.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1000m,
            paymentMethod,
            items = new[]
            {
                new { productId, quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        return data.GetProperty("invoiceId").GetInt64();
    }

    private static MultipartFormDataContent Screenshot()
    {
        using var image = new Image<Rgba32>(600, 900);
        var bytes = new MemoryStream();
        image.Save(bytes, new JpegEncoder());

        var file = new ByteArrayContent(bytes.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        return new MultipartFormDataContent { { file, "file", "transfer.jpg" } };
    }

    private string OnDisk(string relativePath) =>
        Path.Combine(_api.ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task A_bank_transfer_can_carry_its_screenshot()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SellAsync(admin, "BankTransfer");

        using var upload = Screenshot();
        var response = await admin.PostAsync($"/api/invoices/{invoiceId}/payment-proof", upload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
        var path = data.GetProperty("paymentProofPath").GetString()!;

        File.Exists(OnDisk(path)).Should().BeTrue();

        var read = await admin.GetAsync($"/api/invoices/{invoiceId}");
        var invoice = (await read.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoice");

        invoice.GetProperty("paymentProofPath").GetString().Should().Be(path);
    }

    [Theory]
    [InlineData("JazzCash")]
    [InlineData("EasyPaisa")]
    [InlineData("Raast")]
    public async Task Every_non_cash_method_may_carry_proof(string method)
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SellAsync(admin, method);

        using var upload = Screenshot();
        var response = await admin.PostAsync($"/api/invoices/{invoiceId}/payment-proof", upload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_cash_sale_is_refused_a_payment_proof()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SellAsync(admin, "Cash");

        using var upload = Screenshot();
        var response = await admin.PostAsync($"/api/invoices/{invoiceId}/payment-proof", upload);

        // The money was in the drawer. A "proof" here would be evidence of nothing, and
        // accepting it invites proof on sales that never involved a transfer at all.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Cash");
    }

    [Fact]
    public async Task A_sale_completes_perfectly_well_without_one()
    {
        var admin = await ClientAsync(UserRole.Admin);

        // FR-002: the picture is optional, permanently. The counter must never wait for it.
        var invoiceId = await SellAsync(admin, "BankTransfer");

        var read = await admin.GetAsync($"/api/invoices/{invoiceId}");
        var invoice = (await read.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoice");

        invoice.GetProperty("paymentProofPath").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Replacing_a_proof_leaves_the_old_file_behind_nowhere()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SellAsync(admin, "BankTransfer");

        using var first = Screenshot();
        var firstResponse = await admin.PostAsync($"/api/invoices/{invoiceId}/payment-proof", first);
        var firstPath = (await firstResponse.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("paymentProofPath").GetString()!;

        using var second = Screenshot();
        var secondResponse = await admin.PostAsync($"/api/invoices/{invoiceId}/payment-proof", second);
        var secondPath = (await secondResponse.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("paymentProofPath").GetString()!;

        secondPath.Should().NotBe(firstPath);
        File.Exists(OnDisk(firstPath)).Should().BeFalse("a corrected screenshot must not leave the old one on disk");
        File.Exists(OnDisk(secondPath)).Should().BeTrue();
    }

    [Fact]
    public async Task A_salesman_may_attach_proof_too()
    {
        // The salesman is the one who takes the payment, so the salesman must be able to
        // record what backs it up (FR-007).
        var staff = await ClientAsync(UserRole.Staff);
        var invoiceId = await SellAsync(staff, "BankTransfer");

        using var upload = Screenshot();
        var response = await staff.PostAsync($"/api/invoices/{invoiceId}/payment-proof", upload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var invoiceId = await SellAsync(admin, "BankTransfer");

        var file = new ByteArrayContent("not a screenshot"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var upload = new MultipartFormDataContent { { file, "file", "payload.jpg" } };

        var response = await admin.PostAsync($"/api/invoices/{invoiceId}/payment-proof", upload);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_invoice_that_does_not_exist_is_a_clean_404()
    {
        var admin = await ClientAsync(UserRole.Admin);

        using var upload = Screenshot();
        var response = await admin.PostAsync("/api/invoices/99999999/payment-proof", upload);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
