using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Ledger;

/// <summary>
/// The Recovery page: everyone who owes the shop money, most overdue first, with the bills still
/// open — settled oldest first, the same rule the reminder and receipts follow.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RecoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public RecoveryTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

    private async Task<HttpClient> SignedInAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();
        var data = await DataAsync(await client.PostAsJsonAsync("/api/auth/login", new { username, password }));

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<long> ProductAsync()
    {
        var (categoryId, brandId) = await _api.EnsureCatalogueAsync();
        await using var connection = await _api.OpenDatabaseAsync();

        var productId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, @categoryId, @brandId, 0, 0, 0, 0, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Rec {Guid.NewGuid():N}"[..18], categoryId, brandId });

        await _api.StockProductAsync(productId, quantity: 50, costPrice: 600m, salePrice: 1000m);

        return productId;
    }

    private async Task<(long InvoiceId, string Number)> SellAsync(
        HttpClient owner, long? customerId, decimal amountPaid, string method, object? newCustomer = null)
    {
        var response = await owner.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            newCustomer,
            amountPaid,
            paymentMethod = method,
            items = new[] { new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var data = await DataAsync(response);

        return (data.GetProperty("invoiceId").GetInt64(), data.GetProperty("invoiceNumber").GetString()!);
    }

    private static async Task<JsonElement?> AccountAsync(HttpClient client, long customerId)
    {
        var report = await DataAsync(await client.GetAsync("/api/recovery"));

        return report.GetProperty("accounts").EnumerateArray()
            .Where(account => account.GetProperty("customerId").GetInt64() == customerId)
            .Select(account => (JsonElement?)account)
            .SingleOrDefault();
    }

    [Fact]
    public async Task An_udhaar_customer_who_bought_on_credit_is_listed_with_the_bill_still_open()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var customerId = await ApiFactory.RegisterUdhaarCustomerAsync(owner);
        var (_, number) = await SellAsync(owner, customerId, 0m, "Credit");

        var account = (await AccountAsync(owner, customerId))!.Value;

        account.GetProperty("outstanding").GetDecimal().Should().Be(1000m);
        account.GetProperty("isUdhaarCustomer").GetBoolean().Should().BeTrue();
        account.GetProperty("notPaidBills").GetInt32().Should().Be(1);

        var bill = account.GetProperty("openBills").EnumerateArray().Single();
        bill.GetProperty("referenceNumber").GetString().Should().Be(number);
        bill.GetProperty("remaining").GetDecimal().Should().Be(1000m);
        bill.GetProperty("status").GetString().Should().Be("NotPaid");
    }

    [Fact]
    public async Task A_walk_in_who_paid_part_is_listed_as_part_paid()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var sale = await owner.PostAsJsonAsync("/api/invoices", new
        {
            newCustomer = new { name = "Walk-in Rashid", mobileNumber = "03211234567" },
            amountPaid = 400m,
            paymentMethod = "Partial",
            items = new[] { new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m } },
        });
        var customerId = (await DataAsync(sale)).GetProperty("customerId").GetInt64();

        var account = (await AccountAsync(owner, customerId))!.Value;

        account.GetProperty("isUdhaarCustomer").GetBoolean().Should().BeFalse();
        account.GetProperty("partPaidBills").GetInt32().Should().Be(1);
        account.GetProperty("openBills").EnumerateArray().Single().GetProperty("remaining").GetDecimal().Should().Be(600m);
    }

    [Fact]
    public async Task A_payment_settles_the_oldest_bill_first_and_a_settled_customer_drops_off()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var customerId = await ApiFactory.RegisterUdhaarCustomerAsync(owner);
        await SellAsync(owner, customerId, 0m, "Credit");
        var (_, newer) = await SellAsync(owner, customerId, 0m, "Credit");

        await owner.PostAsJsonAsync($"/api/customers/{customerId}/payments", new { amount = 1200m, paymentMethod = "Cash" });

        var bill = (await AccountAsync(owner, customerId))!.Value.GetProperty("openBills").EnumerateArray().Single();
        bill.GetProperty("referenceNumber").GetString().Should().Be(newer);
        bill.GetProperty("remaining").GetDecimal().Should().Be(800m);
        bill.GetProperty("status").GetString().Should().Be("PartPaid");

        await owner.PostAsJsonAsync($"/api/customers/{customerId}/payments", new { amount = 800m, paymentMethod = "Cash" });

        (await AccountAsync(owner, customerId)).Should().BeNull("a settled account has nothing to recover");
    }

    [Fact]
    public async Task A_debt_unpaid_past_its_month_is_overdue_and_comes_first()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var customerId = await ApiFactory.RegisterUdhaarCustomerAsync(owner);
        var (invoiceId, _) = await SellAsync(owner, customerId, 0m, "Credit");

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            // The sale, three months ago — on the invoice and on the ledger line it wrote.
            await connection.ExecuteAsync(
                """
                UPDATE invoices SET invoice_date_utc = DATE_SUB(invoice_date_utc, INTERVAL 3 MONTH) WHERE id = @invoiceId;
                UPDATE ledger_entries SET entry_date_utc = DATE_SUB(entry_date_utc, INTERVAL 3 MONTH)
                WHERE entry_type = 'Invoice' AND reference_id = @invoiceId;
                """,
                new { invoiceId });
        }

        var report = await DataAsync(await owner.GetAsync("/api/recovery"));
        var account = report.GetProperty("accounts").EnumerateArray()
            .Single(row => row.GetProperty("customerId").GetInt64() == customerId);

        account.GetProperty("monthsOverdue").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        report.GetProperty("overdueCustomers").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        // Most overdue first: nobody listed ahead of it is less overdue.
        var months = report.GetProperty("accounts").EnumerateArray().Select(row => row.GetProperty("monthsOverdue").GetInt32()).ToList();
        months.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task The_totals_add_up_to_what_the_listed_customers_owe()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        await SellAsync(owner, await ApiFactory.RegisterUdhaarCustomerAsync(owner), 0m, "Credit");

        var report = await DataAsync(await owner.GetAsync("/api/recovery"));
        var accounts = report.GetProperty("accounts").EnumerateArray().ToList();

        report.GetProperty("customersOwing").GetInt32().Should().Be(accounts.Count);
        report.GetProperty("totalOwed").GetDecimal().Should().Be(accounts.Sum(row => row.GetProperty("outstanding").GetDecimal()));
    }

    [Fact]
    public async Task Staff_can_read_it_because_collecting_is_everyones_job_and_it_carries_no_cost()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var staff = await SignedInAsync(UserRole.Staff);
        await SellAsync(owner, await ApiFactory.RegisterUdhaarCustomerAsync(owner), 0m, "Credit");

        var response = await staff.GetAsync("/api/recovery");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("cost").And.NotContainEquivalentOf("profit");
    }
}
