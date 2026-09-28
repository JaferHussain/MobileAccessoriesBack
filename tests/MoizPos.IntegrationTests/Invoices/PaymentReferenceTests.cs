using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Invoices;

/// <summary>
/// Where a non-cash payment came from (migration 0021).
///
/// <para>The invoice already records WHICH method was used and can carry a screenshot of it. Neither
/// answers the question asked in a dispute: which account did the money come from, and what was
/// the reference? These hold three things — the reference reaches the invoice and reads back,
/// a Cash sale is refused one, and the sale never depends on it.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PaymentReferenceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public PaymentReferenceTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message);

    private async Task<HttpClient> ClientAsync(UserRole role = UserRole.Admin)
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
            new { name = $"Pr {Guid.NewGuid():N}"[..20] });
    }

    private async Task<HttpResponseMessage> SellAsync(
        HttpClient client, string paymentMethod, string? accountNumber, string? transactionId)
    {
        var productId = await CreateProductAsync();

        return await client.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1000m,
            paymentMethod,
            paymentAccountNumber = accountNumber,
            paymentTransactionId = transactionId,
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m } },
        });
    }

    [Fact]
    public async Task A_transfer_records_the_account_it_came_from_and_the_reference()
    {
        var client = await ClientAsync();

        var response = await SellAsync(client, "JazzCash", "03001234567", "TXN8842910");
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var invoiceId = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();

        await using var connection = await _api.OpenDatabaseAsync();

        var stored = await connection.QuerySingleAsync<(string? Account, string? Transaction)>(
            """
            SELECT payment_account_number, payment_transaction_id
            FROM invoices WHERE id = @invoiceId;
            """,
            new { invoiceId });

        stored.Account.Should().Be("03001234567");
        stored.Transaction.Should().Be("TXN8842910");
    }

    /// <summary>
    /// The trap this codebase has hit before: a column reaching the SELECT but not the
    /// hand-written mapper is dropped in silence, and the API reads back null on data that
    /// stored perfectly well.
    /// </summary>
    [Fact]
    public async Task The_reference_reads_back_from_the_invoice()
    {
        var client = await ClientAsync();

        var response = await SellAsync(client, "BankTransfer", "PK36SCBL0000001123456702", "FT26091234");
        var invoiceId = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();

        var read = await client.GetAsync($"/api/invoices/{invoiceId}");
        var invoice = (await read.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoice");

        invoice.GetProperty("paymentAccountNumber").GetString().Should().Be("PK36SCBL0000001123456702");
        invoice.GetProperty("paymentTransactionId").GetString().Should().Be("FT26091234");
    }

    [Fact]
    public async Task A_cash_sale_is_refused_a_payment_reference()
    {
        var client = await ClientAsync();

        var response = await SellAsync(client, "Cash", "03001234567", "TXN1");

        // Money counted into the drawer came from no account. Allowing a reference here would
        // quietly invite one on sales that never had a transfer — the same reasoning that
        // refuses a Cash sale a proof screenshot.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_refused_cash_reference_records_no_sale_at_all()
    {
        var client = await ClientAsync();

        await using var connection = await _api.OpenDatabaseAsync();
        var before = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM invoices;");

        await SellAsync(client, "Cash", "03001234567", null);

        var after = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM invoices;");

        after.Should().Be(before, "a refusal must leave no invoice and no stock movement");
    }

    [Fact]
    public async Task A_transfer_without_a_reference_is_perfectly_ordinary()
    {
        var client = await ClientAsync();

        // Optional, permanently: the counter must never wait while somebody hunts for a
        // transaction id with a customer standing there.
        var response = await SellAsync(client, "EasyPaisa", null, null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_cash_sale_with_no_reference_is_unaffected()
    {
        var client = await ClientAsync();

        var response = await SellAsync(client, "Cash", null, null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task An_over_long_account_number_is_rejected_before_the_database_sees_it()
    {
        var client = await ClientAsync();

        var response = await SellAsync(client, "BankTransfer", new string('9', 80), null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
