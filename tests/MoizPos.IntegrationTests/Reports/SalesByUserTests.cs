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
/// Each salesman's period.
///
/// <para>On its own this is a convenience — the sales drill-down already names who sold each item.
/// Its real job is alongside the day's drawer count: a short is only answerable once you know who
/// was selling, and a pattern of discounts only means something attached to a person.</para>
///
/// <para>Carries no cost and no profit. This is accountability for cash and discounts, not
/// margin.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SalesByUserTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeZoneInfo Shop = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    /// <summary>A private past window per run, so no other fixture's sales land in it.</summary>
    private static readonly int RunBaseOffset = Random.Shared.Next(500, 30_000);

    private readonly ApiFactory _api;

    public SalesByUserTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private static DateOnly ShopToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Shop));

    private static DateOnly OwnDay(int offset) => ShopToday().AddDays(-(RunBaseOffset + offset));

    private static string Iso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    /// <summary>Books a sale to one user on a private day, with a known split and discount.</summary>
    private async Task SeedSaleAsync(
        DateOnly day,
        long userId,
        decimal paid,
        decimal remaining,
        decimal orderDiscount = 0m,
        decimal lineDiscount = 0m)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var total = paid + remaining;

        // A sale that owes money MUST name a customer (FR-017) — an invariant test asserts it
        // across the whole database, and seeding straight past the service does not excuse
        // breaking it. Fully paid sales stay walk-ins, as they are at the counter.
        long? customerId = null;

        if (remaining > 0m)
        {
            customerId = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO customers (name, outstanding_balance, is_active, created_at_utc)
                VALUES (@name, @remaining, TRUE, UTC_TIMESTAMP(6));
                SELECT LAST_INSERT_ID();
                """,
                new { name = $"Su {Guid.NewGuid():N}"[..18], remaining });
        }

        await connection.ExecuteAsync(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));

            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@product, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 400, 0,
                    1000, 50, 3, TRUE, @at);

            SET @productId = LAST_INSERT_ID();

            INSERT INTO invoices
                (invoice_number, customer_id, invoice_date_utc, sale_type, subtotal, order_discount,
                 total, amount_paid, amount_remaining, net_amount, payment_method,
                 user_id, created_at_utc)
            VALUES (@number, @customerId, @at, 'Retail', @total + @orderDiscount, @orderDiscount,
                    @total, @paid, @remaining, @total, 'Cash', @userId, @at);

            SET @invoiceId = LAST_INSERT_ID();

            INSERT INTO invoice_items
                (invoice_id, product_id, product_name, quantity, unit_sale_price, line_discount,
                 unit_cost_price, line_total, returned_qty)
            VALUES (@invoiceId, @productId, 'Cable', 1, @total + @orderDiscount + @lineDiscount,
                    @lineDiscount, 400, @total + @orderDiscount, 0);
            """,
            new
            {
                product = $"Su {Guid.NewGuid():N}"[..20],
                number = $"SU-{Guid.NewGuid():N}"[..20],
                at = MiddayUtc(day),
                total,
                paid,
                remaining,
                orderDiscount,
                lineDiscount,
                userId,
                customerId,
            });
    }

    private async Task<List<JsonElement>> ByUserAsync(HttpClient admin, DateOnly day)
    {
        var response = await admin.GetAsync(
            $"/api/reports/sales-by-user?from={Iso(day)}&to={Iso(day)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.EnumerateArray().ToList();
    }

    [Fact]
    public async Task Each_salesman_is_listed_with_what_they_sold()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (salesmanId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var day = OwnDay(101);

        await SeedSaleAsync(day, salesmanId, paid: 1000m, remaining: 0m);
        await SeedSaleAsync(day, salesmanId, paid: 500m, remaining: 0m);

        var row = (await ByUserAsync(admin, day))
            .Single(r => r.GetProperty("userId").GetInt64() == salesmanId);

        row.GetProperty("invoiceCount").GetInt32().Should().Be(2);
        row.GetProperty("totalSales").GetDecimal().Should().Be(1500m);
        row.GetProperty("userName").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cash_taken_and_credit_given_are_reported_separately()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (salesmanId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var day = OwnDay(102);

        await SeedSaleAsync(day, salesmanId, paid: 600m, remaining: 400m);

        var row = (await ByUserAsync(admin, day))
            .Single(r => r.GetProperty("userId").GetInt64() == salesmanId);

        // What they physically took versus what they let walk out on credit — different risks.
        row.GetProperty("cashTaken").GetDecimal().Should().Be(600m);
        row.GetProperty("creditGiven").GetDecimal().Should().Be(400m);
    }

    [Fact]
    public async Task Both_kinds_of_discount_are_counted_against_the_person_who_gave_them()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (salesmanId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var day = OwnDay(103);

        await SeedSaleAsync(
            day, salesmanId, paid: 1000m, remaining: 0m, orderDiscount: 50m, lineDiscount: 30m);

        var row = (await ByUserAsync(admin, day))
            .Single(r => r.GetProperty("userId").GetInt64() == salesmanId);

        // A line discount is already inside the subtotal, so the whole-bill figure alone would
        // understate what was actually given away.
        row.GetProperty("discountGiven").GetDecimal().Should().Be(80m);
    }

    [Fact]
    public async Task Two_salesmen_are_kept_apart()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (first, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var (second, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var day = OwnDay(104);

        await SeedSaleAsync(day, first, paid: 2000m, remaining: 0m);
        await SeedSaleAsync(day, second, paid: 500m, remaining: 0m);

        var rows = await ByUserAsync(admin, day);

        rows.Single(r => r.GetProperty("userId").GetInt64() == first)
            .GetProperty("totalSales").GetDecimal().Should().Be(2000m);

        rows.Single(r => r.GetProperty("userId").GetInt64() == second)
            .GetProperty("totalSales").GetDecimal().Should().Be(500m);
    }

    [Fact]
    public async Task The_biggest_seller_comes_first()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (small, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var (big, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var day = OwnDay(105);

        await SeedSaleAsync(day, small, paid: 100m, remaining: 0m);
        await SeedSaleAsync(day, big, paid: 9000m, remaining: 0m);

        (await ByUserAsync(admin, day))[0].GetProperty("userId").GetInt64().Should().Be(big);
    }

    [Fact]
    public async Task A_day_with_no_sales_lists_nobody_rather_than_inventing_zeroes()
    {
        var admin = await ClientAsync(UserRole.Admin);

        (await ByUserAsync(admin, OwnDay(106))).Should().BeEmpty();
    }

    [Fact]
    public async Task The_report_reveals_no_cost_or_profit()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (salesmanId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var day = OwnDay(107);

        await SeedSaleAsync(day, salesmanId, paid: 1000m, remaining: 0m);

        var raw = (await ByUserAsync(admin, day))
            .Single(r => r.GetProperty("userId").GetInt64() == salesmanId)
            .GetRawText().ToLowerInvariant();

        raw.Should().NotContain("cost");
        raw.Should().NotContain("profit");
    }

    [Fact]
    public async Task A_salesman_cannot_read_the_report_on_everyone()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var day = OwnDay(108);

        var response = await staff.GetAsync(
            $"/api/reports/sales-by-user?from={Iso(day)}&to={Iso(day)}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
