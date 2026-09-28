using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Reports;

/// <summary>
/// The day's udhaar, split into what was taken wholly on credit and what was left owing on a
/// part payment.
///
/// <para><b>These are visibility, not new arithmetic.</b> Every rupee they report is already
/// inside <c>TotalReceivables</c> and already inside <c>CreditSales</c>; the owner simply could
/// not see how the figure was made up. A second total beside the first would be a second place
/// for the same money to be counted.</para>
///
/// <para><b>Split by the money, never by the label.</b> A sale marked "Cash" whose payment fell
/// short is still credit — the same reasoning that decides credit authority in InvoiceService.
/// Full udhaar is "paid nothing and owes something"; part paid is "paid something and still owes
/// something".</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CreditSalesBreakdownTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public CreditSalesBreakdownTests(ApiFactory api) => _api = api;

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

    private async Task<long> CreateProductAsync()
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
            new { name = $"Cb {Guid.NewGuid():N}"[..20] });
    }

    private async Task<long> CreateCustomerAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/customers", new
        {
            name = $"Cust {Guid.NewGuid():N}"[..18],
        });

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();
    }

    private async Task SellAsync(
        HttpClient client, long customerId, decimal amountPaid, string paymentMethod)
    {
        var productId = await CreateProductAsync();

        var response = await client.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid,
            paymentMethod,
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m } },
        });

        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> DashboardAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/dashboard?period=Today");

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
    }

    [Fact]
    public async Task A_full_udhaar_sale_is_counted_and_totalled()
    {
        var client = await AdminAsync();
        var before = await DashboardAsync(client);

        var beforeCount = before.GetProperty("udhaarSalesCount").GetInt32();
        var beforeAmount = before.GetProperty("udhaarSalesAmount").GetDecimal();

        var customerId = await CreateCustomerAsync(client);
        await SellAsync(client, customerId, amountPaid: 0m, "Credit");

        var after = await DashboardAsync(client);

        after.GetProperty("udhaarSalesCount").GetInt32().Should().Be(beforeCount + 1);
        after.GetProperty("udhaarSalesAmount").GetDecimal().Should().Be(beforeAmount + 1000m);
    }

    [Fact]
    public async Task A_part_payment_reports_only_what_is_still_owed()
    {
        var client = await AdminAsync();
        var before = await DashboardAsync(client);

        var beforeCount = before.GetProperty("partPaidSalesCount").GetInt32();
        var beforeRemaining = before.GetProperty("partPaidRemaining").GetDecimal();

        var customerId = await CreateCustomerAsync(client);
        await SellAsync(client, customerId, amountPaid: 600m, "Partial");

        var after = await DashboardAsync(client);

        after.GetProperty("partPaidSalesCount").GetInt32().Should().Be(beforeCount + 1);
        // The 600 already came in; only the 400 is outstanding.
        after.GetProperty("partPaidRemaining").GetDecimal().Should().Be(beforeRemaining + 400m);
    }

    [Fact]
    public async Task A_part_payment_is_not_counted_as_full_udhaar()
    {
        var client = await AdminAsync();
        var before = await DashboardAsync(client);
        var beforeUdhaar = before.GetProperty("udhaarSalesCount").GetInt32();

        var customerId = await CreateCustomerAsync(client);
        await SellAsync(client, customerId, amountPaid: 600m, "Partial");

        var after = await DashboardAsync(client);

        // The two halves answer different questions and must not overlap.
        after.GetProperty("udhaarSalesCount").GetInt32().Should().Be(beforeUdhaar);
    }

    [Fact]
    public async Task A_fully_paid_sale_appears_in_neither()
    {
        var client = await AdminAsync();
        var before = await DashboardAsync(client);

        var customerId = await CreateCustomerAsync(client);
        await SellAsync(client, customerId, amountPaid: 1000m, "Cash");

        var after = await DashboardAsync(client);

        after.GetProperty("udhaarSalesCount").GetInt32()
            .Should().Be(before.GetProperty("udhaarSalesCount").GetInt32());
        after.GetProperty("partPaidSalesCount").GetInt32()
            .Should().Be(before.GetProperty("partPaidSalesCount").GetInt32());
    }

    /// <summary>
    /// The rule this codebase already applies to credit authority: a sale labelled Cash whose
    /// payment falls short is still credit. The label is the one part a caller controls.
    /// </summary>
    [Fact]
    public async Task A_sale_labelled_cash_that_was_underpaid_still_counts_as_credit()
    {
        var client = await AdminAsync();
        var before = await DashboardAsync(client);
        var beforeRemaining = before.GetProperty("partPaidRemaining").GetDecimal();

        var customerId = await CreateCustomerAsync(client);
        await SellAsync(client, customerId, amountPaid: 250m, "Cash");

        var after = await DashboardAsync(client);

        after.GetProperty("partPaidRemaining").GetDecimal().Should().Be(beforeRemaining + 750m);
    }

    /// <summary>
    /// The discipline the returns figures already follow: the new fields must explain the
    /// existing total, never add a second one beside it.
    /// </summary>
    [Fact]
    public async Task The_two_halves_add_up_to_the_periods_credit_sales()
    {
        var client = await AdminAsync();
        var customerId = await CreateCustomerAsync(client);

        await SellAsync(client, customerId, amountPaid: 0m, "Credit");
        await SellAsync(client, customerId, amountPaid: 600m, "Partial");

        var dashboard = await DashboardAsync(client);

        var udhaar = dashboard.GetProperty("udhaarSalesAmount").GetDecimal();
        var partPaid = dashboard.GetProperty("partPaidRemaining").GetDecimal();

        (udhaar + partPaid).Should().Be(
            dashboard.GetProperty("creditSales").GetDecimal(),
            "these are visibility into CreditSales, not money counted a second time");
    }
}
