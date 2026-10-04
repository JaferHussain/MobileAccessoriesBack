using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Purchases;

/// <summary>
/// A purchase bill: one supplier's bill, many products, and — if the owner pays it there and then —
/// the payment, all saved together. Stock goes in first and the payment follows it; the payment
/// carries the day it was really made, never earlier than the bill.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PurchaseBillTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public PurchaseBillTests(ApiFactory api) => _api = api;

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

    private static string ShopToday() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string DaysAgo(int days) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Task<long> SupplierAsync() => _api.EnsureSupplierAsync($"Bill Supplier {Guid.NewGuid():N}"[..30]);

    /// <summary>A catalogue entry never stocked — its first purchase must set a retail price.</summary>
    private async Task<long> NewProductAsync()
    {
        var (categoryId, brandId) = await _api.EnsureCatalogueAsync();
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, @categoryId, @brandId, 0, 0, 0, 0, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Bill item {Guid.NewGuid():N}"[..24], categoryId, brandId });
    }

    private async Task<decimal> PayableAsync(long supplierId)
    {
        await using var connection = await _api.OpenDatabaseAsync();
        return await connection.ExecuteScalarAsync<decimal>("SELECT payable_balance FROM suppliers WHERE id = @supplierId;", new { supplierId });
    }

    private async Task<int> StockAsync(long productId)
    {
        await using var connection = await _api.OpenDatabaseAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT quantity_on_hand FROM products WHERE id = @productId;", new { productId });
    }

    /// <summary>Two products: 10 at 100 and 5 at 200 — a bill of Rs 2,000.</summary>
    private static object[] TwoLines(long first, long second) =>
    [
        new { productId = first, quantity = 10, unitCost = 100m, newRetailPrice = 150m },
        new { productId = second, quantity = 5, unitCost = 200m, newRetailPrice = 300m },
    ];

    private static Task<HttpResponseMessage> RecordAsync(HttpClient owner, object bill) =>
        owner.PostAsJsonAsync("/api/purchase-bills", bill);

    // ================================================================
    //  The bill and its stock
    // ================================================================

    [Fact]
    public async Task A_bill_of_several_products_puts_every_one_in_stock_and_owes_its_total()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        var response = await RecordAsync(owner, new
        {
            supplierId,
            billNumber = "SUP-4471",
            billDate = ShopToday(),
            lines = TwoLines(charger, cable),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var result = await DataAsync(response);
        result.GetProperty("total").GetDecimal().Should().Be(2000m);

        (await StockAsync(charger)).Should().Be(10);
        (await StockAsync(cable)).Should().Be(5);
        (await PayableAsync(supplierId)).Should().Be(2000m, "nothing was paid, so the whole bill is owed");

        var bill = await DataAsync(await owner.GetAsync($"/api/purchase-bills/{result.GetProperty("billId").GetInt64()}"));
        bill.GetProperty("billNumber").GetString().Should().Be("SUP-4471");
        bill.GetProperty("lines").GetArrayLength().Should().Be(2);
        bill.GetProperty("status").GetString().Should().Be("Unpaid");
    }

    [Fact]
    public async Task Paying_the_whole_bill_at_once_stocks_first_then_pays_on_the_day_given()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        var response = await RecordAsync(owner, new
        {
            supplierId,
            billDate = DaysAgo(3),
            lines = TwoLines(charger, cable),
            payment = new { amount = 2000m, paymentMethod = "BankTransfer", paidOn = DaysAgo(2), reference = "TXN 88" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var result = await DataAsync(response);

        (await PayableAsync(supplierId)).Should().Be(0m, "the bill was paid in full");
        result.GetProperty("paymentId").GetInt64().Should().BePositive();

        // In the supplier's ledger the goods come first and the payment after them, on its own day.
        var ledger = await DataAsync(await owner.GetAsync($"/api/suppliers/{supplierId}/ledger"));
        var entries = ledger.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("entryType").GetString()).ToList();
        entries.Should().Equal("Purchase", "Purchase", "Payment");

        var bill = await DataAsync(await owner.GetAsync($"/api/purchase-bills/{result.GetProperty("billId").GetInt64()}"));
        bill.GetProperty("status").GetString().Should().Be("Paid");
        bill.GetProperty("payments").EnumerateArray().Single().GetProperty("paidOn").GetString().Should().Be(DaysAgo(2));
    }

    [Fact]
    public async Task A_part_payment_leaves_the_rest_owed_and_the_bill_part_paid()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        var result = await DataAsync(await RecordAsync(owner, new
        {
            supplierId,
            billDate = ShopToday(),
            lines = TwoLines(charger, cable),
            payment = new { amount = 500m, paymentMethod = "Cash", paidOn = ShopToday() },
        }));

        (await PayableAsync(supplierId)).Should().Be(1500m);

        var bill = await DataAsync(await owner.GetAsync($"/api/purchase-bills/{result.GetProperty("billId").GetInt64()}"));
        bill.GetProperty("status").GetString().Should().Be("PartPaid");
        bill.GetProperty("paid").GetDecimal().Should().Be(500m);
        bill.GetProperty("due").GetDecimal().Should().Be(1500m);
    }

    [Fact]
    public async Task A_bill_left_to_pay_later_is_paid_against_that_bill_and_never_beyond_it()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        var billId = (await DataAsync(await RecordAsync(owner, new
        {
            supplierId,
            billDate = DaysAgo(5),
            lines = TwoLines(charger, cable),
        }))).GetProperty("billId").GetInt64();

        (await owner.PostAsJsonAsync($"/api/purchase-bills/{billId}/payments", new { amount = 2500m, paymentMethod = "Cash", paidOn = ShopToday() }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "the bill is only Rs 2,000");

        var paid = await owner.PostAsJsonAsync($"/api/purchase-bills/{billId}/payments", new { amount = 2000m, paymentMethod = "JazzCash", paidOn = DaysAgo(1) });
        paid.StatusCode.Should().Be(HttpStatusCode.Created, await paid.Content.ReadAsStringAsync());
        (await DataAsync(paid)).GetProperty("paymentId").GetInt64().Should().BePositive();

        (await PayableAsync(supplierId)).Should().Be(0m);
        (await DataAsync(await owner.GetAsync($"/api/purchase-bills/{billId}"))).GetProperty("status").GetString().Should().Be("Paid");
    }

    // ================================================================
    //  What a payment with a bill may not be
    // ================================================================

    [Theory]
    [InlineData(2500, "Cash", 0, 0, "more than the bill")]
    [InlineData(500, "Credit", 0, 0, "Credit is not a way of paying")]
    [InlineData(500, "Cash", 3, 5, "paid before the goods were bought")]
    [InlineData(500, "Cash", 0, -1, "a day that has not happened")]
    public async Task A_payment_that_cannot_be_true_is_refused_and_nothing_is_saved(
        int amount, string method, int billDaysAgo, int paidDaysAgo, string why)
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        var response = await RecordAsync(owner, new
        {
            supplierId,
            billDate = DaysAgo(billDaysAgo),
            lines = TwoLines(charger, cable),
            payment = new { amount = (decimal)amount, paymentMethod = method, paidOn = DaysAgo(paidDaysAgo) },
        });

        response.StatusCode.Should().BeOneOf([HttpStatusCode.UnprocessableEntity, HttpStatusCode.BadRequest], why);

        // All or nothing: no stock, no bill, nothing owed.
        (await StockAsync(charger)).Should().Be(0);
        (await PayableAsync(supplierId)).Should().Be(0m);
    }

    [Fact]
    public async Task A_bill_cannot_be_dated_in_the_future()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        (await RecordAsync(owner, new { supplierId, billDate = DaysAgo(-2), lines = TwoLines(charger, cable) }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task One_bad_line_saves_nothing_at_all()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        // The second product has never been stocked and no retail price is given for it.
        var response = await RecordAsync(owner, new
        {
            supplierId,
            billDate = ShopToday(),
            lines = new object[]
            {
                new { productId = charger, quantity = 10, unitCost = 100m, newRetailPrice = 150m },
                new { productId = cable, quantity = 5, unitCost = 200m },
            },
            payment = new { amount = 2000m, paymentMethod = "Cash", paidOn = ShopToday() },
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await StockAsync(charger)).Should().Be(0, "the good line is undone with the bad one");
        (await PayableAsync(supplierId)).Should().Be(0m);
    }

    [Fact]
    public async Task A_cash_payment_cannot_be_dated_onto_a_day_whose_drawer_is_already_counted()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        // A past day of this test's own, closed — its drawer is counted and snapshotted.
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).AddDays(-Random.Shared.Next(40_000, 60_000));
        var iso = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        (await owner.PostAsJsonAsync("/api/day-closings", new { closingDate = iso, openingFloat = 0m, countedCash = 0m }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var cash = await RecordAsync(owner, new
        {
            supplierId,
            billDate = iso,
            lines = TwoLines(charger, cable),
            payment = new { amount = 2000m, paymentMethod = "Cash", paidOn = iso },
        });
        cash.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // A transfer never touched the drawer, so that day is no obstacle.
        (await RecordAsync(owner, new
        {
            supplierId,
            billDate = iso,
            lines = TwoLines(charger, cable),
            payment = new { amount = 2000m, paymentMethod = "BankTransfer", paidOn = iso },
        })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_cash_payment_made_with_a_bill_today_comes_out_of_todays_drawer()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        var before = (await DataAsync(await owner.GetAsync($"/api/day-closings/preview?date={ShopToday()}"))).GetProperty("cashToSuppliers").GetDecimal();

        await RecordAsync(owner, new
        {
            supplierId,
            billDate = ShopToday(),
            lines = TwoLines(charger, cable),
            payment = new { amount = 2000m, paymentMethod = "Cash", paidOn = ShopToday() },
        });

        (await DataAsync(await owner.GetAsync($"/api/day-closings/preview?date={ShopToday()}"))).GetProperty("cashToSuppliers").GetDecimal()
            .Should().Be(before + 2000m);
    }

    // ================================================================
    //  The bill photo and the payment screenshot
    // ================================================================

    [Fact]
    public async Task The_suppliers_bill_photo_and_the_payment_screenshot_are_attached_to_the_bill_and_its_payment()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        var result = await DataAsync(await RecordAsync(owner, new
        {
            supplierId,
            billDate = ShopToday(),
            lines = TwoLines(charger, cable),
            payment = new { amount = 2000m, paymentMethod = "EasyPaisa", paidOn = ShopToday() },
        }));
        var billId = result.GetProperty("billId").GetInt64();
        var paymentId = result.GetProperty("paymentId").GetInt64();

        (await owner.PostAsync($"/api/proofs/purchase-bill/{billId}", new MultipartFormDataContent { { ApiFactory.Photo(), "file", "bill.jpg" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsync($"/api/proofs/supplier-payment/{paymentId}", new MultipartFormDataContent { { ApiFactory.Photo(), "file", "paid.jpg" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var bill = await DataAsync(await owner.GetAsync($"/api/purchase-bills/{billId}"));
        bill.GetProperty("hasBillImage").GetBoolean().Should().BeTrue();
        bill.GetProperty("payments").EnumerateArray().Single().GetProperty("hasProof").GetBoolean().Should().BeTrue();
        (await owner.GetAsync($"/api/proofs/purchase-bill/{billId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ================================================================
    //  Who
    // ================================================================

    [Fact]
    public async Task Only_the_owner_records_a_bill_or_sees_one_because_bills_carry_cost()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var staff = await SignedInAsync(UserRole.Staff);
        var supplierId = await SupplierAsync();
        var (charger, cable) = (await NewProductAsync(), await NewProductAsync());

        (await RecordAsync(staff, new { supplierId, billDate = ShopToday(), lines = TwoLines(charger, cable) }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var billId = (await DataAsync(await RecordAsync(owner, new { supplierId, billDate = ShopToday(), lines = TwoLines(charger, cable) })))
            .GetProperty("billId").GetInt64();

        (await staff.GetAsync($"/api/purchase-bills/{billId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.GetAsync("/api/purchase-bills")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.PostAsync($"/api/proofs/purchase-bill/{billId}", new MultipartFormDataContent { { ApiFactory.Photo(), "file", "bill.jpg" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
