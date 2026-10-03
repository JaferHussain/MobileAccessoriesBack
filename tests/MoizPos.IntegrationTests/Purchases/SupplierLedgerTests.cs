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
/// A supplier's account, and paying a supplier properly.
///
/// <para>Before this there was no way to see a supplier's history: a payment, once made, could not
/// be found again on any screen. And every payment was recorded as Cash, so a bank transfer to a
/// supplier showed up as a short in the evening's drawer count.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SupplierLedgerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public SupplierLedgerTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private async Task<HttpClient> SignedInAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

    private static async Task<long> SupplierAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/suppliers", new { name = $"Ahmad {Guid.NewGuid():N}"[..16] });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await DataAsync(response)).GetProperty("id").GetInt64();
    }

    private async Task<long> ProductAsync()
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
            new { name = $"Tempered Glass {Guid.NewGuid():N}"[..24], categoryId, brandId });
    }

    private async Task<long> PurchaseAsync(
        HttpClient admin, long supplierId, decimal unitCost, int quantity, DateTime? purchaseDate = null)
    {
        var response = await admin.PostAsJsonAsync("/api/purchases", new
        {
            supplierId,
            productId = await ProductAsync(),
            unitCost,
            quantity,
            purchaseDate,
            newRetailPrice = unitCost * 1.5m,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await DataAsync(response)).GetProperty("purchaseId").GetInt64();
    }

    private static async Task<HttpResponseMessage> PayAsync(
        HttpClient admin, long supplierId, decimal amount, string? paymentMethod, string? note = null) =>
        await admin.PostAsJsonAsync($"/api/suppliers/{supplierId}/payments", new { amount, paymentMethod, note });

    private static async Task<JsonElement> LedgerAsync(HttpClient admin, long supplierId, string query = "")
    {
        var response = await admin.GetAsync($"/api/suppliers/{supplierId}/ledger{query}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await DataAsync(response);
    }

    private static DateOnly KarachiToday() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));

    // ================================================================
    //  The ledger
    // ================================================================

    [Fact]
    public async Task The_ledger_reads_like_the_owners_book_and_ends_at_what_is_owed()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync(admin);

        // The owner's own example: buy 100,000, send 10,000 back, pay 20,000 by bank.
        var purchaseId = await PurchaseAsync(admin, supplierId, unitCost: 1_000m, quantity: 100);

        (await admin.PostAsJsonAsync("/api/purchase-returns", new { purchaseId, quantity = 10, reason = "Cracked" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await PayAsync(admin, supplierId, 20_000m, "BankTransfer", "TXN 4471"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var ledger = await LedgerAsync(admin, supplierId);
        var entries = ledger.GetProperty("entries").EnumerateArray().ToList();

        entries.Select(entry => entry.GetProperty("entryType").GetString())
            .Should().Equal("Purchase", "Return", "Payment");
        entries.Select(entry => entry.GetProperty("balanceAfter").GetDecimal())
            .Should().Equal(100_000m, 90_000m, 70_000m);

        entries[0].GetProperty("quantity").GetInt32().Should().Be(100);
        entries[1].GetProperty("note").GetString().Should().Be("Cracked");
        entries[2].GetProperty("paymentMethod").GetString().Should().Be("BankTransfer");
        entries[2].GetProperty("note").GetString().Should().Be("TXN 4471");

        ledger.GetProperty("totalPurchased").GetDecimal().Should().Be(100_000m);
        ledger.GetProperty("totalReturned").GetDecimal().Should().Be(10_000m);
        ledger.GetProperty("totalPaid").GetDecimal().Should().Be(20_000m);

        // The account and the supplier's balance are one figure, never two that can disagree.
        ledger.GetProperty("payableBalance").GetDecimal().Should().Be(70_000m);
    }

    [Fact]
    public async Task A_date_range_starts_from_what_was_already_owed()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync(admin);

        await PurchaseAsync(admin, supplierId, unitCost: 1_000m, quantity: 50, purchaseDate: DateTime.UtcNow.AddDays(-40));
        (await PayAsync(admin, supplierId, 5_000m, "Cash")).StatusCode.Should().Be(HttpStatusCode.OK);

        var today = KarachiToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ledger = await LedgerAsync(admin, supplierId, $"?from={today}&to={today}");

        // The old purchase is before the range, so it is not listed — but it is still owed, and
        // the range opens on it rather than pretending the account started at zero today.
        ledger.GetProperty("openingBalance").GetDecimal().Should().Be(50_000m);

        var entries = ledger.GetProperty("entries").EnumerateArray().ToList();
        entries.Should().ContainSingle();
        entries[0].GetProperty("balanceAfter").GetDecimal().Should().Be(45_000m);
    }

    [Fact]
    public async Task A_salesman_cannot_read_a_supplier_ledger()
    {
        // It shows purchase cost and what the shop owes — financial data (FR-040).
        var admin = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync(admin);
        var staff = await SignedInAsync(UserRole.Staff);

        (await staff.GetAsync($"/api/suppliers/{supplierId}/ledger")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_supplier_has_no_ledger()
    {
        var admin = await SignedInAsync(UserRole.Admin);

        (await admin.GetAsync("/api/suppliers/999999999/ledger")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ================================================================
    //  Paying a supplier
    // ================================================================

    [Fact]
    public async Task A_payment_must_say_how_it_was_made()
    {
        // Defaulting to Cash is exactly what put bank transfers into the drawer count.
        var admin = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync(admin);
        await PurchaseAsync(admin, supplierId, unitCost: 1_000m, quantity: 10);

        (await PayAsync(admin, supplierId, 1_000m, paymentMethod: null)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Credit")]
    [InlineData("Partial")]
    public async Task A_payment_cannot_be_labelled_credit_or_partial(string method)
    {
        // Those describe a SALE left unpaid; money handed to a supplier is one of the real ways
        // to pay, or it is not a payment.
        var admin = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync(admin);
        await PurchaseAsync(admin, supplierId, unitCost: 1_000m, quantity: 10);

        (await PayAsync(admin, supplierId, 1_000m, method)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_a_cash_payment_comes_out_of_the_drawer()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var supplierId = await SupplierAsync(admin);
        await PurchaseAsync(admin, supplierId, unitCost: 1_000m, quantity: 100);

        var today = KarachiToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        async Task<decimal> CashToSuppliersAsync() =>
            (await DataAsync(await admin.GetAsync($"/api/day-closings/preview?date={today}")))
                .GetProperty("cashToSuppliers").GetDecimal();

        var before = await CashToSuppliersAsync();

        (await PayAsync(admin, supplierId, 7_000m, "BankTransfer")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CashToSuppliersAsync()).Should().Be(before, "a bank transfer never left the till");

        (await PayAsync(admin, supplierId, 3_000m, "Cash")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CashToSuppliersAsync()).Should().Be(before + 3_000m);
    }
}
