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
/// Finding a past bill.
///
/// <para>The customer ledger already lists a customer's own invoices, so this exists for the sale
/// that belongs to nobody: a <b>walk-in</b>, which is most counter sales. Those appear in no
/// ledger, and without this list they become unreachable the moment the counter resets.</para>
///
/// <para>Which means the list must name who each sale was to — including when the answer is
/// "nobody" — or the owner cannot tell the walk-ins apart from anything else.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvoiceListTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public InvoiceListTests(ApiFactory api) => _api = api;

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

    private async Task<long> ProductAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 800, 0,
                    1100, 50, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Lst {Guid.NewGuid():N}"[..20] });
    }

    private async Task<long> SellAsync(HttpClient admin, long? customerId)
    {
        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[]
            {
                new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m },
            },
        });

        sale.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await sale.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();
    }

    private async Task<JsonElement> ListAsync(HttpClient admin, string query = "")
    {
        var response = await admin.GetAsync($"/api/invoices?pageSize=100{query}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
    }

    private static JsonElement Find(JsonElement page, long invoiceId) =>
        page.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetInt64() == invoiceId);

    [Fact]
    public async Task The_list_names_the_customer_a_sale_was_made_to()
    {
        var admin = await AdminAsync();
        var name = $"Cust {Guid.NewGuid():N}"[..18];

        var created = await admin.PostAsJsonAsync("/api/customers", new { name });
        var customerId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var invoiceId = await SellAsync(admin, customerId);

        Find(await ListAsync(admin, $"&customerId={customerId}"), invoiceId)
            .GetProperty("customerName").GetString().Should().Be(name);
    }

    [Fact]
    public async Task A_walk_in_sale_appears_with_no_customer_name()
    {
        var admin = await AdminAsync();
        var invoiceId = await SellAsync(admin, null);

        var row = Find(await ListAsync(admin), invoiceId);

        // Null rather than an invented label: "nobody" is a fact, and the screen decides how to
        // word it. A server-side "Walk-in customer" string would be a presentation choice baked
        // into the data.
        row.GetProperty("customerName").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("customerId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_list_carries_what_a_row_needs_to_be_read_and_shared()
    {
        var admin = await AdminAsync();
        var invoiceId = await SellAsync(admin, null);

        var row = Find(await ListAsync(admin), invoiceId);

        row.GetProperty("invoiceNumber").GetString().Should().StartWith("INV-");
        row.GetProperty("total").GetDecimal().Should().Be(1100m);
        row.GetProperty("invoiceDateUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_newest_sale_is_first_because_that_is_the_one_being_asked_about()
    {
        var admin = await AdminAsync();

        await SellAsync(admin, null);
        var newest = await SellAsync(admin, null);

        var items = (await ListAsync(admin)).GetProperty("items").EnumerateArray().ToList();

        items[0].GetProperty("id").GetInt64().Should().Be(newest);
    }

    [Fact]
    public async Task A_staff_user_can_find_a_bill_to_hand_over()
    {
        // Handing a customer their own receipt is counter work (FR-110). The row carries no cost.
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Staff);
        var staff = _api.CreateClient();

        var login = await staff.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        staff.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        var response = await staff.GetAsync("/api/invoices?pageSize=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
