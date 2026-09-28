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
/// Counting the drawer against what the day recorded.
///
/// <para>The only control the shop has over physical cash. A salesman can take a cash sale, hand
/// over the goods, record it perfectly and pocket the notes — every report still balances, because
/// the sale really was recorded. These tests hold the arithmetic that makes that visible, and the
/// rules that stop a short being closed away.</para>
///
/// <para><b>Each test owns its own trading day, in the past, and seeds that day directly.</b> The
/// first version of this file cleared today's tables to reason about its figures in isolation —
/// which wiped data other test classes in this collection were relying on and reddened one of
/// them. A closing is unique per date, so a private date per test gives real isolation without
/// touching anything shared.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DayClosingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public DayClosingTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    /// <summary>ISO, invariant: a date in a URL is not a thing the machine's locale decides.</summary>
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static readonly TimeZoneInfo Shop = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    private static DateOnly ShopToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Shop));

    /// <summary>
    /// A base offset drawn once per test run.
    ///
    /// <para>The test database is created once and kept, so rows survive between runs. A day is
    /// closed at most once, ever — so days derived from "today minus a fixed number" collided
    /// with the PREVIOUS run's closings and every close came back 422. The random base moves each
    /// run onto untouched days; the per-test offset keeps them apart within a run.</para>
    /// </summary>
    private static readonly int RunBaseOffset = Random.Shared.Next(500, 30_000);

    /// <summary>A trading day nobody else is using — far enough back that no fixture lands on it.</summary>
    private static DateOnly OwnDay(int offset) => ShopToday().AddDays(-(RunBaseOffset + offset));

    /// <summary>Midday on that shop day, in UTC — comfortably inside the day's boundaries.</summary>
    private static DateTime MiddayUtc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(12, 0)), Shop);

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

    // ================================================================
    //  Seeding a past day directly — no API, so nothing shared moves
    // ================================================================

    private async Task SeedSaleAsync(DateOnly day, decimal amountPaid, string method)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO invoices
                (invoice_number, customer_id, invoice_date_utc, sale_type, subtotal, order_discount,
                 total, amount_paid, amount_remaining, net_amount, payment_method,
                 user_id, created_at_utc)
            VALUES
                (@number, NULL, @at, 'Retail', 1000, 0, 1000, @amountPaid, 1000 - @amountPaid,
                 1000, @method, (SELECT id FROM users ORDER BY id LIMIT 1), @at);
            """,
            new
            {
                number = $"DC-{Guid.NewGuid():N}"[..20],
                at = MiddayUtc(day),
                amountPaid,
                method,
            });
    }

    private async Task SeedRecoveryAsync(DateOnly day, decimal amount, string method)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO customers (name, outstanding_balance, is_active, created_at_utc)
            VALUES (@customer, 0, TRUE, UTC_TIMESTAMP(6));

            INSERT INTO customer_payments
                (customer_id, receipt_number, amount, payment_method, payment_date_utc,
                 is_overpayment, user_id, created_at_utc)
            VALUES (LAST_INSERT_ID(), @receipt, @amount, @method, @at, FALSE,
                    (SELECT id FROM users ORDER BY id LIMIT 1), @at);
            """,
            new
            {
                customer = $"DC {Guid.NewGuid():N}"[..18],
                receipt = $"DCR-{Guid.NewGuid():N}"[..20],
                amount,
                method,
                at = MiddayUtc(day),
            });
    }

    private async Task SeedExpenseAsync(DateOnly day, decimal amount, string? paymentSource)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            """
            INSERT IGNORE INTO expense_categories (name, created_at_utc)
            VALUES ('Sundry', UTC_TIMESTAMP(6));

            INSERT INTO expenses
                (category_id, amount, payment_source, expense_date_utc, note, user_id, created_at_utc)
            VALUES ((SELECT id FROM expense_categories WHERE name = 'Sundry' LIMIT 1),
                    @amount, @paymentSource, @at, 'day close test',
                    (SELECT id FROM users ORDER BY id LIMIT 1), @at);
            """,
            new { amount, paymentSource, at = MiddayUtc(day) });
    }

    /// <summary>
    /// Sends stock back to a supplier on a past day.
    ///
    /// <para>Reduces what the shop owes and puts the goods back on the van. No notes move, which
    /// is exactly what this seeds a test for.</para>
    /// </summary>
    private async Task SeedPurchaseReturnAsync(DateOnly day, decimal total)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));

            INSERT INTO suppliers (name, payable_balance, is_active, created_at_utc)
            VALUES (@supplier, 0, TRUE, UTC_TIMESTAMP(6));
            SET @supplierId = LAST_INSERT_ID();

            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@product, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 400, 0,
                    1000, 100, 3, TRUE, @at);
            SET @productId = LAST_INSERT_ID();

            INSERT INTO purchases
                (supplier_id, product_id, quantity, unit_cost, total, purchase_date_utc,
                 returned_qty, user_id, created_at_utc)
            VALUES (@supplierId, @productId, 10, @unitCost, @total, @at, 10,
                    (SELECT id FROM users ORDER BY id LIMIT 1), @at);
            SET @purchaseId = LAST_INSERT_ID();

            INSERT INTO purchase_returns
                (purchase_id, supplier_id, product_id, return_number, return_date_utc, quantity,
                 unit_cost, total, user_id, created_at_utc)
            VALUES (@purchaseId, @supplierId, @productId, @number, @at, 10, @unitCost, @total,
                    (SELECT id FROM users ORDER BY id LIMIT 1), @at);
            """,
            new
            {
                supplier = $"DCP {Guid.NewGuid():N}"[..18],
                product = $"Dcp {Guid.NewGuid():N}"[..20],
                number = $"PRT-{Guid.NewGuid():N}"[..20],
                unitCost = total / 10m,
                total,
                at = MiddayUtc(day),
            });
    }

    /// <summary>Settles a supplier bill on a past day, by the given method.</summary>
    private async Task SeedSupplierPaymentAsync(DateOnly day, decimal amount, string method)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO suppliers (name, payable_balance, is_active, created_at_utc)
            VALUES (@supplier, 0, TRUE, UTC_TIMESTAMP(6));

            INSERT INTO supplier_payments
                (supplier_id, amount, payment_date_utc, payment_method, is_overpayment,
                 user_id, created_at_utc)
            VALUES (LAST_INSERT_ID(), @amount, @at, @method, FALSE,
                    (SELECT id FROM users ORDER BY id LIMIT 1), @at);
            """,
            new
            {
                supplier = $"DCS {Guid.NewGuid():N}"[..18],
                amount,
                method,
                at = MiddayUtc(day),
            });
    }

    private async Task<JsonElement> PreviewAsync(HttpClient admin, DateOnly day, decimal openingFloat = 0m)
    {
        var response = await admin.GetAsync(
            $"/api/day-closings/preview?date={Iso(day)}&openingFloat={openingFloat.ToString(CultureInfo.InvariantCulture)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
    }

    private Task<HttpResponseMessage> CloseAsync(
        HttpClient admin, DateOnly day, decimal openingFloat, decimal counted, string? note = null) =>
        admin.PostAsJsonAsync("/api/day-closings", new
        {
            closingDate = Iso(day),
            openingFloat,
            countedCash = counted,
            note,
        });

    // ================================================================
    //  What counts as cash
    // ================================================================

    [Fact]
    public async Task A_cash_sale_is_expected_in_the_drawer()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(1);

        await SeedSaleAsync(day, amountPaid: 1000m, "Cash");

        var preview = await PreviewAsync(admin, day, openingFloat: 2000m);

        preview.GetProperty("cashSales").GetDecimal().Should().Be(1000m);
        preview.GetProperty("expectedCash").GetDecimal().Should().Be(3000m);
    }

    [Fact]
    public async Task A_bank_transfer_never_entered_the_drawer_and_is_not_expected_in_it()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(2);

        await SeedSaleAsync(day, amountPaid: 1000m, "BankTransfer");

        // Counting it would make the drawer look short by exactly the amount that arrived
        // electronically — which teaches the shopkeeper to ignore the difference.
        (await PreviewAsync(admin, day)).GetProperty("cashSales").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task A_credit_sale_puts_no_notes_in_the_drawer()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(3);

        await SeedSaleAsync(day, amountPaid: 0m, "Credit");

        (await PreviewAsync(admin, day)).GetProperty("cashSales").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task A_part_payment_contributes_only_the_part_that_was_paid()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(4);

        await SeedSaleAsync(day, amountPaid: 600m, "Partial");

        (await PreviewAsync(admin, day)).GetProperty("cashSales").GetDecimal().Should().Be(600m);
    }

    [Fact]
    public async Task Recovering_an_old_debt_in_cash_is_expected_in_the_drawer()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(5);

        await SeedRecoveryAsync(day, 400m, "Cash");

        var preview = await PreviewAsync(admin, day);

        // Not a sale — the goods left weeks ago — but the notes are in the drawer tonight.
        preview.GetProperty("cashRecovery").GetDecimal().Should().Be(400m);
        preview.GetProperty("expectedCash").GetDecimal().Should().Be(400m);
    }

    [Fact]
    public async Task A_debt_recovered_by_transfer_is_not_expected_in_the_drawer()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(6);

        await SeedRecoveryAsync(day, 400m, "BankTransfer");

        (await PreviewAsync(admin, day)).GetProperty("cashRecovery").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task An_expense_paid_from_the_till_takes_money_out_of_the_drawer()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(7);

        await SeedSaleAsync(day, 1000m, "Cash");
        await SeedExpenseAsync(day, 250m, "Till");

        var preview = await PreviewAsync(admin, day);

        preview.GetProperty("cashPaidOut").GetDecimal().Should().Be(250m);
        preview.GetProperty("expectedCash").GetDecimal().Should().Be(750m);
    }

    [Fact]
    public async Task An_expense_paid_from_the_bank_leaves_the_drawer_alone()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(8);

        await SeedSaleAsync(day, 1000m, "Cash");
        await SeedExpenseAsync(day, 250m, "Bank");

        (await PreviewAsync(admin, day)).GetProperty("cashPaidOut").GetDecimal().Should().Be(0m);
    }

    /// <summary>
    /// This used to assert that an expense with no recorded source was excluded rather than
    /// guessed at. Migration 0024 removed the possibility: the column is NOT NULL, so the
    /// scenario can no longer be created. What is asserted now is that it cannot.
    /// </summary>
    [Fact]
    public async Task An_expense_can_no_longer_be_recorded_without_a_source()
    {
        var day = OwnDay(9);

        var act = async () => await SeedExpenseAsync(day, 250m, paymentSource: null);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Paying_a_supplier_in_cash_takes_money_out_of_the_drawer()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(20);

        await SeedSaleAsync(day, 5000m, "Cash");
        await SeedSupplierPaymentAsync(day, 3000m, "Cash");

        var preview = await PreviewAsync(admin, day);

        // Hand a supplier notes from the till and the evening must account for it. Before this
        // was counted, a payment like this reported a short with nothing to explain it.
        preview.GetProperty("cashToSuppliers").GetDecimal().Should().Be(3000m);
        preview.GetProperty("expectedCash").GetDecimal().Should().Be(2000m);
    }

    [Fact]
    public async Task Paying_a_supplier_by_transfer_leaves_the_drawer_alone()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(21);

        await SeedSaleAsync(day, 5000m, "Cash");
        await SeedSupplierPaymentAsync(day, 3000m, "BankTransfer");

        var preview = await PreviewAsync(admin, day);

        preview.GetProperty("cashToSuppliers").GetDecimal().Should().Be(0m);
        preview.GetProperty("expectedCash").GetDecimal().Should().Be(5000m);
    }

    [Fact]
    public async Task Stock_sent_back_to_a_supplier_moves_no_cash()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(22);

        await SeedSaleAsync(day, 5000m, "Cash");
        await SeedPurchaseReturnAsync(day, 4000m);

        // A purchase return reduces stock and what the shop owes. No notes move, so the drawer
        // is untouched — the question that found the supplier-payment gap in the first place.
        (await PreviewAsync(admin, day)).GetProperty("expectedCash").GetDecimal().Should().Be(5000m);
    }

    [Fact]
    public async Task Both_ways_cash_leaves_the_till_are_shown_apart()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(23);

        await SeedSaleAsync(day, 9000m, "Cash");
        await SeedExpenseAsync(day, 250m, "Till");
        await SeedSupplierPaymentAsync(day, 4000m, "Cash");

        var preview = await PreviewAsync(admin, day);

        // Buying stock and paying the electricity bill are different questions; a shopkeeper
        // reading a short wants to see which of the two moved.
        preview.GetProperty("cashPaidOut").GetDecimal().Should().Be(250m);
        preview.GetProperty("cashToSuppliers").GetDecimal().Should().Be(4000m);
        preview.GetProperty("expectedCash").GetDecimal().Should().Be(4750m);
    }

    [Fact]
    public async Task Yesterdays_takings_are_not_counted_against_today()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(10);

        await SeedSaleAsync(day.AddDays(-1), 1000m, "Cash");

        // The trading day is resolved in Asia/Karachi, not UTC — a sale at 9pm belongs to the day
        // the shopkeeper was standing in, not to the next one.
        (await PreviewAsync(admin, day)).GetProperty("cashSales").GetDecimal().Should().Be(0m);
    }

    // ================================================================
    //  Closing the day
    // ================================================================

    [Fact]
    public async Task A_balanced_drawer_closes_with_no_difference()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(11);

        await SeedSaleAsync(day, 1000m, "Cash");

        var response = await CloseAsync(admin, day, openingFloat: 2000m, counted: 3000m);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var closing = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        closing.GetProperty("difference").GetDecimal().Should().Be(0m);
        closing.GetProperty("isClosed").GetBoolean().Should().BeTrue();
        closing.GetProperty("closedByUserName").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_short_drawer_is_recorded_as_a_negative_difference()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(12);

        await SeedSaleAsync(day, 1000m, "Cash");

        var response = await CloseAsync(admin, day, 0m, counted: 800m, note: "200 unexplained");
        var closing = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        closing.GetProperty("difference").GetDecimal().Should().Be(-200m);
        // A difference is shown, never accused — the note is where it gets explained.
        closing.GetProperty("note").GetString().Should().Be("200 unexplained");
    }

    [Fact]
    public async Task An_over_drawer_is_reported_too()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(13);

        await SeedSaleAsync(day, 1000m, "Cash");

        var response = await CloseAsync(admin, day, 0m, counted: 1200m);
        var closing = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        // More money than the day accounts for usually means a sale went unrecorded.
        closing.GetProperty("difference").GetDecimal().Should().Be(200m);
    }

    [Fact]
    public async Task A_day_cannot_be_closed_twice()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(14);

        (await CloseAsync(admin, day, 0m, 0m)).StatusCode.Should().Be(HttpStatusCode.Created);

        // A day that could be closed twice would let a short be closed away and reopened at a
        // more comfortable figure.
        (await CloseAsync(admin, day, 0m, 0m)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_closed_day_reads_back_what_was_recorded_not_a_fresh_calculation()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(15);

        await SeedSaleAsync(day, 1000m, "Cash");
        await CloseAsync(admin, day, openingFloat: 0m, counted: 1000m);

        // A late correction against that day. The evening's evidence must not move.
        await SeedSaleAsync(day, 500m, "Cash");

        var preview = await PreviewAsync(admin, day);

        preview.GetProperty("cashSales").GetDecimal().Should().Be(1000m);
        preview.GetProperty("difference").GetDecimal().Should().Be(0m);
        preview.GetProperty("isClosed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_day_that_has_not_happened_yet_cannot_be_closed()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await CloseAsync(admin, ShopToday().AddDays(3), 0m, 0m);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_negative_count_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);

        (await CloseAsync(admin, OwnDay(16), 0m, -1m)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    // ================================================================
    //  Whose control this is
    // ================================================================

    [Fact]
    public async Task A_salesman_cannot_close_the_day()
    {
        var staff = await ClientAsync(UserRole.Staff);

        // This is the control OVER the salesman's handling of cash. A short that the person
        // responsible for it can close away is not a control at all.
        (await CloseAsync(staff, OwnDay(17), 0m, 0m)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_salesman_cannot_see_what_the_drawer_should_hold()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync($"/api/day-closings/preview?date={Iso(OwnDay(18))}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Recent_closings_are_listed_so_a_pattern_of_small_shorts_shows()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var day = OwnDay(19);

        await CloseAsync(admin, day, 0m, 0m);

        var response = await admin.GetAsync("/api/day-closings?count=200");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.EnumerateArray().ToList();

        rows.Should().Contain(r => r.GetProperty("closingDate").GetString() == Iso(day));
    }
}
