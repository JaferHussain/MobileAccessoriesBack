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

    [Fact]
    public async Task The_salesman_sees_what_he_carries_at_its_price_and_what_he_sold_and_nothing_of_cost()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");

        var carried = await ProductAsync();
        await _api.IssueToSalesmanAsync(salesmanId, carried, 5);

        // Two sold at 1,100 each on one bill with a 200 discount: worth 2,000 (never below the shop's price).
        (await salesman.PostAsJsonAsync("/api/invoices", new
        {
            amountPaid = 2000m,
            paymentMethod = "Cash",
            orderDiscount = 200m,
            items = new[] { new { productId = carried, quantity = 2, unitSalePrice = 1100m, lineDiscount = 0m } },
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await salesman.GetAsync("/api/my-products");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var data = await DataAsync(response);

        var assigned = data.GetProperty("assigned").EnumerateArray().Single(p => p.GetProperty("productId").GetInt64() == carried);
        assigned.GetProperty("quantity").GetInt32().Should().Be(3, "5 issued, 2 sold out of his bag");
        assigned.GetProperty("price").GetDecimal().Should().Be(1000m);
        assigned.GetProperty("value").GetDecimal().Should().Be(3000m);

        var sold = data.GetProperty("sold").EnumerateArray().Single(p => p.GetProperty("productId").GetInt64() == carried);
        sold.GetProperty("quantitySold").GetInt32().Should().Be(2);
        sold.GetProperty("amount").GetDecimal().Should().Be(2000m, "the bill discount comes off what he sold");
        data.GetProperty("soldAmount").GetDecimal().Should().Be(2000m);

        body.Should().NotContainAny("cost", "wholesale", "profit");
    }

    [Fact]
    public async Task A_salesman_finds_and_takes_back_only_what_he_sold_himself()
    {
        var admin = await AdminAsync();
        var (_, ali, _) = await StaffAsync(admin, "FieldSales");
        var (_, bilal, _) = await StaffAsync(admin, "FieldSales");

        async Task<(long InvoiceId, string Number, long ItemId)> SaleOfAsync(HttpClient seller)
        {
            var sale = await DataAsync(await SellAsync(seller, 1000m));
            var invoiceId = sale.GetProperty("invoiceId").GetInt64();
            var items = (await DataAsync(await admin.GetAsync($"/api/invoices/{invoiceId}"))).GetProperty("items");
            return (invoiceId, sale.GetProperty("invoiceNumber").GetString()!, items[0].GetProperty("id").GetInt64());
        }

        var own = await SaleOfAsync(ali);
        var others = await SaleOfAsync(bilal);

        // His search finds his own sale, never Bilal's — even searched by Bilal's invoice number.
        var found = (await DataAsync(await ali.GetAsync($"/api/sale-returns/find?search={own.Number}"))).EnumerateArray().ToList();
        found.Should().Contain(r => r.GetProperty("invoiceId").GetInt64() == own.InvoiceId);
        (await DataAsync(await ali.GetAsync($"/api/sale-returns/find?search={others.Number}"))).EnumerateArray()
            .Should().BeEmpty();

        // Taking back Bilal's sale is refused, and nothing is saved.
        (await ali.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId = others.InvoiceId,
            refundMethod = "Cash",
            items = new[] { new { invoiceItemId = others.ItemId, quantity = 1 } },
        })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // His own goes through.
        (await ali.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId = own.InvoiceId,
            refundMethod = "Cash",
            items = new[] { new { invoiceItemId = own.ItemId, quantity = 1 } },
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        // Bilal returns his own; Ali's history lists only Ali's.
        (await bilal.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId = others.InvoiceId,
            refundMethod = "Cash",
            items = new[] { new { invoiceItemId = others.ItemId, quantity = 1 } },
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var history = (await DataAsync(await ali.GetAsync("/api/sale-returns?pageSize=200"))).GetProperty("items").EnumerateArray().ToList();
        history.Should().Contain(r => r.GetProperty("invoiceId").GetInt64() == own.InvoiceId);
        history.Should().NotContain(r => r.GetProperty("invoiceId").GetInt64() == others.InvoiceId);

        // The owner still sees both.
        var all = (await DataAsync(await admin.GetAsync("/api/sale-returns?pageSize=200"))).GetProperty("items").EnumerateArray().ToList();
        all.Should().Contain(r => r.GetProperty("invoiceId").GetInt64() == others.InvoiceId);
    }

    [Fact]
    public async Task The_counter_shopkeeper_too_takes_back_only_his_own_sales_and_the_owner_any()
    {
        var admin = await AdminAsync();
        var (_, counter, _) = await StaffAsync(admin, "Counter");

        var ownerSale = await DataAsync(await SellAsync(admin, 1000m));
        var ownerInvoice = ownerSale.GetProperty("invoiceId").GetInt64();
        var ownerItem = (await DataAsync(await admin.GetAsync($"/api/invoices/{ownerInvoice}"))).GetProperty("items")[0].GetProperty("id").GetInt64();

        (await DataAsync(await counter.GetAsync($"/api/sale-returns/find?search={ownerSale.GetProperty("invoiceNumber").GetString()}")))
            .EnumerateArray().Should().BeEmpty("the owner's sale is not his");

        (await counter.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId = ownerInvoice,
            refundMethod = "Cash",
            items = new[] { new { invoiceItemId = ownerItem, quantity = 1 } },
        })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var counterSale = await DataAsync(await SellAsync(counter, 1000m));
        var counterInvoice = counterSale.GetProperty("invoiceId").GetInt64();
        var counterItem = (await DataAsync(await admin.GetAsync($"/api/invoices/{counterInvoice}"))).GetProperty("items")[0].GetProperty("id").GetInt64();

        // The owner may take back anyone's — here, the shopkeeper's.
        (await admin.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId = counterInvoice,
            refundMethod = "Cash",
            items = new[] { new { invoiceItemId = counterItem, quantity = 1 } },
        })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task My_day_shows_online_sales_apart_from_cash_method_by_method()
    {
        var admin = await AdminAsync();
        var (_, ali, _) = await StaffAsync(admin, "FieldSales");
        var udhaar = await CustomerAsync(admin, creditAllowed: true);

        (await SellAsync(ali, 1000m, "Cash")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await SellAsync(ali, 1000m, "JazzCash")).StatusCode.Should().Be(HttpStatusCode.Created);
        // A part paid by bank transfer: only the 400 that came in counts as received online.
        (await SellAsync(ali, 400m, "BankTransfer", udhaar)).StatusCode.Should().Be(HttpStatusCode.Created);
        // Full udhaar: nothing came in, so it is no payment at all.
        (await SellAsync(ali, 0m, "Credit", udhaar)).StatusCode.Should().Be(HttpStatusCode.Created);

        var payments = (await DataAsync(await ali.GetAsync("/api/my-day"))).GetProperty("payments");

        payments.GetProperty("cashBills").GetInt32().Should().Be(1);
        payments.GetProperty("cashReceived").GetDecimal().Should().Be(1000m);
        payments.GetProperty("onlineBills").GetInt32().Should().Be(2);
        payments.GetProperty("onlineReceived").GetDecimal().Should().Be(1400m);

        var online = payments.GetProperty("online").EnumerateArray()
            .ToDictionary(row => row.GetProperty("method").GetString()!, row => row.GetProperty("received").GetDecimal());
        online.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["JazzCash"] = 1000m, ["BankTransfer"] = 400m });
    }

    /// <summary>An udhaar customer is registered with the ID card; anyone else is an ordinary contact.</summary>
    private static async Task<long> CustomerAsync(HttpClient admin, bool creditAllowed)
    {
        if (creditAllowed)
        {
            return await ApiFactory.RegisterUdhaarCustomerAsync(admin);
        }

        var created = await admin.PostAsJsonAsync("/api/customers", new { name = $"Shop {Guid.NewGuid():N}"[..14] });

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
    public async Task Only_the_owner_can_make_an_udhaar_customer_and_only_by_registering_them()
    {
        var admin = await AdminAsync();
        var (_, salesman, _) = await StaffAsync(admin, "FieldSales");

        // A customer the salesman creates is an ordinary contact…
        var created = await salesman.PostAsJsonAsync("/api/customers", new { name = $"Own {Guid.NewGuid():N}"[..13] });
        var customer = await DataAsync(created);
        customer.GetProperty("creditAllowed").GetBoolean().Should().BeFalse();
        var id = customer.GetProperty("id").GetInt64();

        // …no flag makes them an udhaar customer, by his hand or the owner's…
        (await salesman.PutAsJsonAsync($"/api/customers/{id}", new { name = customer.GetProperty("name").GetString(), creditAllowed = true }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await admin.PutAsJsonAsync($"/api/customers/{id}", new { name = customer.GetProperty("name").GetString(), creditAllowed = true }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // …and registering, with the ID card, is the owner's alone.
        var form = new MultipartFormDataContent
        {
            { ApiFactory.Photo(), "idCardFront", "front.jpg" },
            { ApiFactory.Photo(), "idCardBack", "back.jpg" },
            { new StringContent("03001234567"), "mobileNumber" },
        };
        (await salesman.PostAsync($"/api/udhaar-customers/{id}/register", form)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var again = new MultipartFormDataContent
        {
            { ApiFactory.Photo(), "idCardFront", "front.jpg" },
            { ApiFactory.Photo(), "idCardBack", "back.jpg" },
            { new StringContent("03001234567"), "mobileNumber" },
        };
        (await admin.PostAsync($"/api/udhaar-customers/{id}/register", again)).StatusCode.Should().Be(HttpStatusCode.OK);
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

    // ================================================================
    //  Proof of a handover that did not come as cash
    // ================================================================

    private static MultipartFormDataContent Screenshot() =>
        new() { { ApiFactory.Photo(), "file", "transfer.jpg" } };

    private static async Task<long> HandOverAsync(HttpClient admin, long salesmanId, string method)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/salesman-cash/{salesmanId}/handovers", new { amount = 1000m, paymentMethod = method });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // The new handover's id, so its proof can be attached straight after.
        return (await DataAsync(response)).GetProperty("handoverId").GetInt64();
    }

    [Fact]
    public async Task A_handover_by_transfer_takes_its_screenshot_and_is_on_proof_missing_until_it_has_one()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);

        var handoverId = await HandOverAsync(admin, salesmanId, "JazzCash");

        static bool Listed(JsonElement missing, long id) => missing.EnumerateArray()
            .Any(row => row.GetProperty("kind").GetString() == "SalesmanHandover" && row.GetProperty("referenceId").GetInt64() == id);

        Listed(await DataAsync(await admin.GetAsync("/api/proofs/missing")), handoverId).Should().BeTrue();

        (await admin.PostAsync($"/api/proofs/salesman-handover/{handoverId}", Screenshot())).StatusCode
            .Should().Be(HttpStatusCode.OK);

        Listed(await DataAsync(await admin.GetAsync("/api/proofs/missing")), handoverId).Should().BeFalse();
        (await admin.GetAsync($"/api/proofs/salesman-handover/{handoverId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var movement = (await DataAsync(await admin.GetAsync($"/api/salesman-cash/{salesmanId}")))
            .GetProperty("movements").EnumerateArray()
            .Single(row => row.GetProperty("kind").GetString() == "Handover");
        movement.GetProperty("hasProof").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_cash_handover_needs_no_proof_because_it_was_counted_into_the_drawer()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);

        var handoverId = await HandOverAsync(admin, salesmanId, "Cash");

        (await admin.PostAsync($"/api/proofs/salesman-handover/{handoverId}", Screenshot())).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Only_the_owner_attaches_or_sees_a_handover_proof()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman, _) = await StaffAsync(admin, "FieldSales");
        await SellAsync(salesman, 1000m);
        var handoverId = await HandOverAsync(admin, salesmanId, "BankTransfer");

        (await salesman.PostAsync($"/api/proofs/salesman-handover/{handoverId}", Screenshot())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await salesman.GetAsync($"/api/proofs/salesman-handover/{handoverId}")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
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
