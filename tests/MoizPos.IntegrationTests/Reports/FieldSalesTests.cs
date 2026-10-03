using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Reports;

/// <summary>
/// The salesman in the market: udhaar only to the owner's udhaar customers, and the cash he carries
/// until he hands it over.
///
/// <para>His cash is in his pocket, not the counter drawer. Day close must leave it out — or every
/// market sale shows as a short — and count it in the day he hands it over.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FieldSalesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public FieldSalesTests(ApiFactory api) => _api = api;

    /// <summary>
    /// Field salesmen by their signed-in client. He can sell only what he carries, and these tests
    /// are about something else — so each sale first issues him the goods he is about to sell.
    /// </summary>
    private readonly Dictionary<HttpClient, long> _fieldSalesmen = [];

    private sealed record Envelope<T>(bool Success, T? Data);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

    private async Task<(HttpClient Client, JsonElement User)> LoginAsync(string username, string password)
    {
        var client = _api.CreateClient();
        var data = await DataAsync(await client.PostAsJsonAsync("/api/auth/login", new { username, password }));

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return (client, data.GetProperty("user"));
    }

    private async Task<HttpClient> AdminAsync()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        return (await LoginAsync(username, password)).Client;
    }

    private async Task<(long Id, HttpClient Client, JsonElement User)> StaffAsync(HttpClient admin, string job)
    {
        var username = $"fld{Guid.NewGuid():N}"[..13];

        var created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username,
            fullName = $"Ali {username[^4..]}",
            password = "Staff@12345",
            role = "Staff",
            job,
        });

        var (client, user) = await LoginAsync(username, "Staff@12345");
        var id = (await DataAsync(created)).GetProperty("id").GetInt64();

        if (job == "FieldSales")
        {
            _fieldSalesmen[client] = id;
        }

        return (id, client, user);
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
            new { name = $"Fld {Guid.NewGuid():N}"[..18], categoryId, brandId });

        await _api.StockProductAsync(productId, quantity: 100, costPrice: 600m, salePrice: 1000m);

        return productId;
    }

    private async Task<HttpResponseMessage> SellAsync(
        HttpClient client, decimal amountPaid, string method = "Cash", long? customerId = null)
    {
        var productId = await ProductAsync();

        if (_fieldSalesmen.TryGetValue(client, out var salesmanId))
        {
            await _api.IssueToSalesmanAsync(salesmanId, productId, 1);
        }

        return await client.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid,
            paymentMethod = method,
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m } },
        });
    }

    private static async Task<long> CustomerAsync(HttpClient admin, bool creditAllowed)
    {
        var created = await admin.PostAsJsonAsync("/api/customers", new
        {
            name = $"Shop {Guid.NewGuid():N}"[..14],
            creditAllowed,
        });

        return (await DataAsync(created)).GetProperty("id").GetInt64();
    }

    private static string Today() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> DayAsync(HttpClient admin) =>
        await DataAsync(await admin.GetAsync($"/api/day-closings/preview?date={Today()}"));

    private static async Task<decimal> InHandAsync(HttpClient client, long? userId = null) =>
        (await DataAsync(await client.GetAsync(userId is null ? "/api/salesman-cash/me" : $"/api/salesman-cash/{userId}")))
            .GetProperty("inHand").GetDecimal();

    // ================================================================
    //  Udhaar — only to the owner's udhaar customers
    // ================================================================

    [Fact]
    public async Task A_salesman_may_give_udhaar_to_an_udhaar_customer()
    {
        var admin = await AdminAsync();
        var (_, salesman, _) = await StaffAsync(admin, "FieldSales");

        var sale = await SellAsync(salesman, 0m, "Credit", await CustomerAsync(admin, creditAllowed: true));

        sale.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_salesman_may_not_give_udhaar_to_anyone_else()
    {
        var admin = await AdminAsync();
        var (_, salesman, _) = await StaffAsync(admin, "FieldSales");

        var sale = await SellAsync(salesman, 0m, "Credit", await CustomerAsync(admin, creditAllowed: false));

        sale.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sale.Content.ReadAsStringAsync()).Should().Contain("udhaar customers");
    }

    [Fact]
    public async Task The_counter_still_may_not_give_udhaar_even_to_an_udhaar_customer()
    {
        var admin = await AdminAsync();
        var (_, shopkeeper, _) = await StaffAsync(admin, "Counter");

        (await SellAsync(shopkeeper, 0m, "Credit", await CustomerAsync(admin, creditAllowed: true)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Only_the_owner_can_mark_an_udhaar_customer()
    {
        var admin = await AdminAsync();
        var (_, salesman, _) = await StaffAsync(admin, "FieldSales");

        // A customer the salesman creates himself is never an udhaar customer…
        var created = await salesman.PostAsJsonAsync("/api/customers", new { name = $"Own {Guid.NewGuid():N}"[..13], creditAllowed = true });
        var customer = await DataAsync(created);
        customer.GetProperty("creditAllowed").GetBoolean().Should().BeFalse();

        // …and cannot become one by his hand.
        var id = customer.GetProperty("id").GetInt64();
        await salesman.PutAsJsonAsync($"/api/customers/{id}", new { name = customer.GetProperty("name").GetString(), creditAllowed = true });
        (await DataAsync(await admin.GetAsync($"/api/customers/{id}"))).GetProperty("creditAllowed").GetBoolean().Should().BeFalse();

        // The owner's mark is what counts.
        await admin.PutAsJsonAsync($"/api/customers/{id}", new { name = customer.GetProperty("name").GetString(), creditAllowed = true });
        (await DataAsync(await admin.GetAsync($"/api/customers/{id}"))).GetProperty("creditAllowed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Signing_in_tells_the_screen_which_job_the_person_does()
    {
        var admin = await AdminAsync();
        var (_, _, user) = await StaffAsync(admin, "FieldSales");

        user.GetProperty("job").GetString().Should().Be("FieldSales");
    }

    // ================================================================
    //  The cash he carries
    // ================================================================

    [Fact]
    public async Task A_market_sale_is_cash_in_his_hand_not_in_the_drawer()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        var (_, shopkeeper, _) = await StaffAsync(admin, "Counter");

        var before = (await DayAsync(admin)).GetProperty("cashSales").GetDecimal();

        (await SellAsync(salesman, 1000m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await DayAsync(admin)).GetProperty("cashSales").GetDecimal().Should().Be(before, "it is in his pocket");
        (await InHandAsync(admin, salesmanId)).Should().Be(1000m);

        // The counter's own sale still lands in the drawer.
        (await SellAsync(shopkeeper, 1000m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await DayAsync(admin)).GetProperty("cashSales").GetDecimal().Should().Be(before + 1000m);
    }

    [Fact]
    public async Task Cash_he_hands_over_joins_the_drawer_and_leaves_his_hand()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);

        var day = await DayAsync(admin);
        var fromSalesmen = day.GetProperty("cashFromSalesmen").GetDecimal();
        var expected = day.GetProperty("expectedCash").GetDecimal();

        (await admin.PostAsJsonAsync($"/api/salesman-cash/{salesmanId}/handovers", new { amount = 600m, paymentMethod = "Cash" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var after = await DayAsync(admin);
        after.GetProperty("cashFromSalesmen").GetDecimal().Should().Be(fromSalesmen + 600m);
        after.GetProperty("expectedCash").GetDecimal().Should().Be(expected + 600m, "the notes are in the drawer now");
        (await InHandAsync(admin, salesmanId)).Should().Be(400m);
    }

    [Fact]
    public async Task Money_he_pays_into_a_shop_account_never_touches_the_drawer()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);

        var before = (await DayAsync(admin)).GetProperty("cashFromSalesmen").GetDecimal();

        await admin.PostAsJsonAsync($"/api/salesman-cash/{salesmanId}/handovers", new { amount = 1000m, paymentMethod = "JazzCash" });

        (await DayAsync(admin)).GetProperty("cashFromSalesmen").GetDecimal().Should().Be(before);
        (await InHandAsync(admin, salesmanId)).Should().Be(0m);
    }

    [Fact]
    public async Task No_one_can_record_more_than_he_is_holding()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);

        (await admin.PostAsJsonAsync($"/api/salesman-cash/{salesmanId}/handovers", new { amount = 1200m, paymentMethod = "Cash" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Udhaar_he_collects_in_the_market_is_his_to_hand_over_and_a_cash_refund_comes_from_it()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        var customerId = await CustomerAsync(admin, creditAllowed: true);

        var sale = await SellAsync(salesman, 0m, "Credit", customerId);
        var invoiceId = (await DataAsync(sale)).GetProperty("invoiceId").GetInt64();

        var recoveredBefore = (await DayAsync(admin)).GetProperty("cashRecovery").GetDecimal();
        (await salesman.PostAsJsonAsync($"/api/customers/{customerId}/payments", new { amount = 1000m, paymentMethod = "Cash" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await DayAsync(admin)).GetProperty("cashRecovery").GetDecimal().Should().Be(recoveredBefore, "collected in the market");
        (await InHandAsync(admin, salesmanId)).Should().Be(1000m);

        // A cash refund on a cash sale he made in the market comes out of what he is holding.
        // (A return against udhaar reduces what is owed instead, and hands back no cash.)
        var cashSale = await SellAsync(salesman, 1000m);
        invoiceId = (await DataAsync(cashSale)).GetProperty("invoiceId").GetInt64();
        (await InHandAsync(admin, salesmanId)).Should().Be(2000m);

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

        (await InHandAsync(admin, salesmanId)).Should().Be(1000m, "he paid the 1,000 refund out of what he was holding");
    }

    [Fact]
    public async Task He_sees_what_he_is_holding_and_nobody_else_can_record_his_handover_but_the_owner()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        var (otherId, _, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);

        (await InHandAsync(salesman)).Should().Be(1000m);

        (await salesman.GetAsync($"/api/salesman-cash/{otherId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await salesman.PostAsJsonAsync($"/api/salesman-cash/{salesmanId}/handovers", new { amount = 100m, paymentMethod = "Cash" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_team_card_shows_the_cash_he_is_holding()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);

        var card = (await DataAsync(await admin.GetAsync($"/api/team?from={Today()}&to={Today()}")))
            .EnumerateArray().Single(member => member.GetProperty("userId").GetInt64() == salesmanId);

        card.GetProperty("cashInHand").GetDecimal().Should().Be(1000m);
    }

    // ================================================================
    //  My day — the salesman's own screen, on his phone
    // ================================================================

    [Fact]
    public async Task His_own_day_shows_his_sales_and_nobody_elses()
    {
        var admin = await AdminAsync();
        var (_, salesman, _) = await StaffAsync(admin, "FieldSales");
        var (_, other, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);
        await SellAsync(other, 1000m);

        var day = await DataAsync(await salesman.GetAsync($"/api/my-day?from={Today()}&to={Today()}"));

        day.GetProperty("card").GetProperty("invoiceCount").GetInt32().Should().Be(1);
        day.GetProperty("card").GetProperty("totalSales").GetDecimal().Should().Be(1000m);
        day.GetProperty("card").GetProperty("cashInHand").GetDecimal().Should().Be(1000m);
        day.GetProperty("activity").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("kind").GetString().Should().Be("Sale");
    }

    [Fact]
    public async Task His_own_day_is_only_his_selling_never_sign_ins_or_the_owners_business()
    {
        var admin = await AdminAsync();
        var (_, salesman, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);

        var activity = (await DataAsync(await salesman.GetAsync("/api/my-day"))).GetProperty("activity");

        // He signed in to get here, but a sign-in is not something he did for the shop.
        activity.EnumerateArray().Select(entry => entry.GetProperty("kind").GetString())
            .Should().OnlyContain(kind => kind == "Sale" || kind == "Return" || kind == "Recovery");
    }
}
