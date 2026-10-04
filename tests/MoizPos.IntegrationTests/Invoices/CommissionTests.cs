using System.Globalization;
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
/// The field salesman's commission — the owner's rule: <b>half of whatever he sells above the
/// owner's price, earned once the customer has paid for it; never below that price</b>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CommissionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public CommissionTests(ApiFactory api) => _api = api;

    /// <summary>
    /// Field salesmen by their signed-in client. He can sell only what he carries, and these tests
    /// are about something else — so each sale first issues him the goods he is about to sell.
    /// </summary>
    private readonly Dictionary<HttpClient, long> _fieldSalesmen = [];

    private sealed record Envelope<T>(bool Success, T? Data);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _api.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = await DataAsync(login);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        return await LoginAsync(username, password);
    }

    private async Task<(long Id, HttpClient Client)> StaffAsync(HttpClient admin, string job)
    {
        var username = $"com{Guid.NewGuid():N}"[..13];

        var created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username,
            fullName = $"Salesman {username[^4..]}",
            password = "Staff@12345",
            role = "Staff",
            job,
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await DataAsync(created)).GetProperty("id").GetInt64();
        var client = await LoginAsync(username, "Staff@12345");

        if (job == "FieldSales")
        {
            _fieldSalesmen[client] = id;
        }

        return (id, client);
    }

    /// <summary>A product priced by the owner: retail 1,000 and, optionally, wholesale.</summary>
    private async Task<long> ProductAsync(decimal wholesale = 0m)
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
            new { name = $"Com {Guid.NewGuid():N}"[..18], categoryId, brandId });

        await _api.StockProductAsync(productId, quantity: 100, costPrice: 600m, salePrice: 1000m, wholesalePrice: wholesale);

        return productId;
    }

    private async Task<HttpResponseMessage> SellAsync(
        HttpClient client, long productId, decimal unitPrice, int quantity = 1, decimal orderDiscount = 0m,
        string saleType = "Retail")
    {
        if (_fieldSalesmen.TryGetValue(client, out var salesmanId))
        {
            await _api.IssueToSalesmanAsync(salesmanId, productId, quantity);
        }

        return await client.PostAsJsonAsync("/api/invoices", new
        {
            saleType,
            orderDiscount,
            amountPaid = unitPrice * quantity - orderDiscount,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity, unitSalePrice = unitPrice, lineDiscount = 0m } },
        });
    }

    private static async Task<JsonElement> StatementAsync(HttpClient client, long? userId = null) =>
        await DataAsync(await client.GetAsync(userId is null ? "/api/commissions/me" : $"/api/commissions/{userId}"));

    private static string Today() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ================================================================
    //  The owner's price
    // ================================================================

    [Fact]
    public async Task Selling_at_1200_what_the_owner_prices_at_1000_earns_the_salesman_100()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");

        (await SellAsync(salesman, await ProductAsync(), 1200m)).StatusCode.Should().Be(HttpStatusCode.Created);

        var statement = await StatementAsync(admin, salesmanId);
        var line = statement.GetProperty("lines").EnumerateArray().Single();

        line.GetProperty("baseUnitPrice").GetDecimal().Should().Be(1000m);
        line.GetProperty("soldAtUnitPrice").GetDecimal().Should().Be(1200m);
        line.GetProperty("commission").GetDecimal().Should().Be(100m);
        line.GetProperty("status").GetString().Should().Be("Earned", "a cash sale is paid for at once");
        line.GetProperty("earnedOn").GetString().Should().Be(Today());

        statement.GetProperty("earned").GetDecimal().Should().Be(100m);
        statement.GetProperty("owed").GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task A_salesman_cannot_sell_below_the_owners_price()
    {
        var admin = await AdminAsync();
        var (_, salesman) = await StaffAsync(admin, "FieldSales");
        var productId = await ProductAsync();

        (await SellAsync(salesman, productId, 950m)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // Nor get there with a whole-bill discount: what each unit really fetched is what counts.
        (await SellAsync(salesman, productId, 1000m, orderDiscount: 100m)).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_counter_is_unchanged_and_earns_no_commission()
    {
        var admin = await AdminAsync();
        var (shopkeeperId, shopkeeper) = await StaffAsync(admin, "Counter");

        (await SellAsync(shopkeeper, await ProductAsync(), 950m)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await StatementAsync(admin, shopkeeperId)).GetProperty("lines").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_wholesale_sale_is_measured_against_the_wholesale_price()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");

        (await SellAsync(salesman, await ProductAsync(wholesale: 800m), 900m, saleType: "Wholesale"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var line = (await StatementAsync(admin, salesmanId)).GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("baseUnitPrice").GetDecimal().Should().Be(800m);
        line.GetProperty("commission").GetDecimal().Should().Be(50m);
    }

    [Fact]
    public async Task A_returned_unit_earns_nothing()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");

        var sale = await SellAsync(salesman, await ProductAsync(), 1200m, quantity: 2);
        var invoiceId = (await DataAsync(sale)).GetProperty("invoiceId").GetInt64();

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            var itemId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM invoice_items WHERE invoice_id = @invoiceId;", new { invoiceId });

            (await salesman.PostAsJsonAsync("/api/sale-returns", new
            {
                invoiceId,
                refundMethod = "Cash",
                items = new[] { new { invoiceItemId = itemId, quantity = 1 } },
            })).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        (await StatementAsync(admin, salesmanId)).GetProperty("lines").EnumerateArray().Single()
            .GetProperty("commission").GetDecimal().Should().Be(100m, "one of the two came back");
    }

    // ================================================================
    //  Earned once the customer has paid
    // ================================================================

    [Fact]
    public async Task Udhaar_earns_nothing_until_recovered_and_then_earns_the_day_it_is()
    {
        var admin = await AdminAsync();
        var (salesmanId, _) = await StaffAsync(admin, "FieldSales");

        var customer = await admin.PostAsJsonAsync("/api/customers", new { name = $"Shop {Guid.NewGuid():N}"[..14] });
        var customerId = (await DataAsync(customer)).GetProperty("id").GetInt64();
        await _api.MarkUdhaarAsync(customerId);

        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 0m,
            paymentMethod = "Credit",
            items = new[] { new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1200m, lineDiscount = 0m } },
        });
        var invoiceId = (await DataAsync(sale)).GetProperty("invoiceId").GetInt64();

        // A salesman cannot yet complete an udhaar sale himself (only the owner can), so the
        // owner's sale is attributed to him as his sale would have been recorded.
        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE invoices SET user_id = @salesmanId WHERE id = @invoiceId;
                UPDATE invoice_items SET base_unit_price = 1000, commission_rate = 50 WHERE invoice_id = @invoiceId;
                """,
                new { salesmanId, invoiceId });
        }

        var before = (await StatementAsync(admin, salesmanId)).GetProperty("lines").EnumerateArray().Single();
        before.GetProperty("status").GetString().Should().Be("Pending");
        before.GetProperty("pending").GetDecimal().Should().Be(100m);

        await admin.PostAsJsonAsync($"/api/customers/{customerId}/payments", new { amount = 600m, paymentMethod = "Cash" });

        var half = await StatementAsync(admin, salesmanId);
        half.GetProperty("lines").EnumerateArray().Single().GetProperty("status").GetString().Should().Be("PartEarned");
        half.GetProperty("earned").GetDecimal().Should().Be(50m);
        half.GetProperty("owed").GetDecimal().Should().Be(50m, "only what is earned can be owed");

        await admin.PostAsJsonAsync($"/api/customers/{customerId}/payments", new { amount = 600m, paymentMethod = "Cash" });

        var line = (await StatementAsync(admin, salesmanId)).GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("status").GetString().Should().Be("Earned");
        line.GetProperty("earnedOn").GetString().Should().Be(Today(), "recovered today, earned today");
    }

    // ================================================================
    //  Paying him
    // ================================================================

    [Fact]
    public async Task The_owner_pays_commission_and_never_more_than_is_earned()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, await ProductAsync(), 1200m);

        var paid = await admin.PostAsJsonAsync($"/api/commissions/{salesmanId}/payouts", new { amount = 60m, paymentMethod = "Cash" });
        paid.StatusCode.Should().Be(HttpStatusCode.Created);
        (await DataAsync(paid)).GetProperty("owed").GetDecimal().Should().Be(40m);

        (await admin.PostAsJsonAsync($"/api/commissions/{salesmanId}/payouts", new { amount = 50m, paymentMethod = "Cash" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ================================================================
    //  Proof of commission paid by transfer
    // ================================================================

    private static MultipartFormDataContent Screenshot() =>
        new() { { ApiFactory.Photo(), "file", "payout.jpg" } };

    private static async Task<long> PayAsync(HttpClient admin, long salesmanId, string method)
    {
        var paid = await admin.PostAsJsonAsync($"/api/commissions/{salesmanId}/payouts", new { amount = 60m, paymentMethod = method });
        paid.StatusCode.Should().Be(HttpStatusCode.Created);

        // The new payout's id, so its screenshot can be attached straight after.
        return (await DataAsync(paid)).GetProperty("payoutId").GetInt64();
    }

    [Fact]
    public async Task Commission_paid_by_transfer_takes_its_screenshot_and_is_on_proof_missing_until_it_has_one()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, await ProductAsync(), 1200m);

        var payoutId = await PayAsync(admin, salesmanId, "JazzCash");

        static bool Listed(JsonElement missing, long id) => missing.EnumerateArray()
            .Any(row => row.GetProperty("kind").GetString() == "CommissionPayout" && row.GetProperty("referenceId").GetInt64() == id);

        Listed(await DataAsync(await admin.GetAsync("/api/proofs/missing")), payoutId).Should().BeTrue();

        (await admin.PostAsync($"/api/proofs/commission-payout/{payoutId}", Screenshot())).StatusCode.Should().Be(HttpStatusCode.OK);

        Listed(await DataAsync(await admin.GetAsync("/api/proofs/missing")), payoutId).Should().BeFalse();
        (await StatementAsync(admin, salesmanId)).GetProperty("payouts").EnumerateArray()
            .Single(row => row.GetProperty("id").GetInt64() == payoutId)
            .GetProperty("hasProof").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Commission_paid_in_cash_needs_no_proof()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, await ProductAsync(), 1200m);

        var payoutId = await PayAsync(admin, salesmanId, "Cash");

        (await admin.PostAsync($"/api/proofs/commission-payout/{payoutId}", Screenshot())).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Only_the_owner_attaches_or_sees_a_commission_proof()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, await ProductAsync(), 1200m);
        var payoutId = await PayAsync(admin, salesmanId, "BankTransfer");

        (await salesman.PostAsync($"/api/proofs/commission-payout/{payoutId}", Screenshot())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await salesman.GetAsync($"/api/proofs/commission-payout/{payoutId}")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_salesman_sees_his_own_commission_and_nobody_elses_and_cannot_pay_himself()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");
        var (otherId, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, await ProductAsync(), 1200m);

        (await StatementAsync(salesman)).GetProperty("owed").GetDecimal().Should().Be(100m);

        (await salesman.GetAsync($"/api/commissions/{otherId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await salesman.PostAsJsonAsync($"/api/commissions/{salesmanId}/payouts", new { amount = 10m, paymentMethod = "Cash" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Commission_paid_in_cash_comes_out_of_the_drawer_and_by_transfer_does_not()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, await ProductAsync(), 1400m);

        async Task<decimal> CashPaidOutAsync() =>
            (await DataAsync(await admin.GetAsync($"/api/day-closings/preview?date={Today()}")))
                .GetProperty("cashPaidOut").GetDecimal();

        var before = await CashPaidOutAsync();

        await admin.PostAsJsonAsync($"/api/commissions/{salesmanId}/payouts", new { amount = 80m, paymentMethod = "JazzCash" });
        (await CashPaidOutAsync()).Should().Be(before, "a transfer never left the till");

        await admin.PostAsJsonAsync($"/api/commissions/{salesmanId}/payouts", new { amount = 70m, paymentMethod = "Cash" });
        (await CashPaidOutAsync()).Should().Be(before + 70m);
    }
}
