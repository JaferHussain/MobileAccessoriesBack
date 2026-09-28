using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Documents;

/// <summary>
/// Reminding a customer of what they owe, from their register.
///
/// <para>The owner's rule: udhaar is due <b>one month after the purchase</b>. Unpaid past that, the
/// date rolls a month forward and the reminder says how many months overdue. The clock runs from
/// the <b>oldest purchase still unpaid</b>, so a small part payment never restarts it.</para>
///
/// <para>The test host runs on the real clock, so "a purchase 45 days ago" is made by moving that
/// customer's own ledger dates back — their rows only, and no figure changes.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PaymentReminderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public PaymentReminderTests(ApiFactory api) => _api = api;

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

    private async Task<long> ProductAsync()
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
            new { name = $"Rem {Guid.NewGuid():N}"[..20] });
    }

    private static async Task<long> CustomerAsync(HttpClient admin, string? mobileNumber = "03001234567")
    {
        var created = await admin.PostAsJsonAsync("/api/customers", new
        {
            name = $"Asif {Guid.NewGuid():N}"[..14],
            mobileNumber,
        });

        return (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();
    }

    private async Task SellOnCreditAsync(HttpClient admin, long customerId, decimal amount)
    {
        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 0m,
            paymentMethod = "Credit",
            items = new[]
            {
                new { productId = await ProductAsync(), quantity = 1, unitSalePrice = amount, lineDiscount = 0m },
            },
        });

        sale.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task ReceivePaymentAsync(HttpClient admin, long customerId, decimal amount)
    {
        var response = await admin.PostAsJsonAsync($"/api/customers/{customerId}/payments", new
        {
            amount,
            paymentMethod = "Cash",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>Moves every ledger date this customer has so far back by <paramref name="days"/>.</summary>
    private async Task BackDateAsync(long customerId, int days)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            """
            UPDATE ledger_entries
            SET entry_date_utc = DATE_SUB(entry_date_utc, INTERVAL @days DAY)
            WHERE customer_id = @customerId;
            """,
            new { customerId, days });
    }

    private static async Task<JsonElement> ReminderAsync(HttpClient client, long customerId)
    {
        var response = await client.GetAsync($"/api/customers/{customerId}/reminder");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
    }

    private static string Decoded(JsonElement reminder, string property, string separator) =>
        Uri.UnescapeDataString(reminder.GetProperty(property).GetString()!.Split(separator)[^1]);

    private static DateOnly KarachiToday() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));

    [Fact]
    public async Task A_new_debt_is_due_one_month_after_the_purchase()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);

        var reminder = await ReminderAsync(admin, customerId);

        reminder.GetProperty("outstanding").GetDecimal().Should().Be(1000m);
        reminder.GetProperty("monthsOverdue").GetInt32().Should().Be(0);
        DateOnly.Parse(reminder.GetProperty("dueOn").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(KarachiToday().AddMonths(1));

        var whatsApp = Decoded(reminder, "whatsAppUrl", "&text=");
        whatsApp.Should().Contain("Please pay your dues *Rs 1,000*");
        whatsApp.Should().NotContain("overdue");
    }

    [Fact]
    public async Task A_debt_unpaid_past_its_month_says_it_is_overdue()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);
        await BackDateAsync(customerId, days: 45);

        var reminder = await ReminderAsync(admin, customerId);

        reminder.GetProperty("monthsOverdue").GetInt32().Should().Be(1);
        Decoded(reminder, "whatsAppUrl", "&text=").Should().Contain("(1 month overdue)");
        Decoded(reminder, "smsUrl", "?body=").Should().Contain("(1 month overdue)");
    }

    [Fact]
    public async Task A_small_part_payment_does_not_restart_the_clock()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);
        await BackDateAsync(customerId, days: 45);

        // Paying Rs 100 today must not make a 45-day-old debt "not yet due".
        await ReceivePaymentAsync(admin, customerId, 100m);

        var reminder = await ReminderAsync(admin, customerId);

        reminder.GetProperty("outstanding").GetDecimal().Should().Be(900m);
        reminder.GetProperty("monthsOverdue").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_sms_reminder_is_plain_text_with_no_credit_line()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);

        var reminder = await ReminderAsync(admin, customerId);
        var sms = Decoded(reminder, "smsUrl", "?body=");
        var whatsApp = Decoded(reminder, "whatsAppUrl", "&text=");

        sms.Should().Contain("Remaining Amount: Rs 1,000");
        sms.Should().NotContain("Asyntex");
        whatsApp.Should().Contain("Asyntex");
    }

    [Fact]
    public async Task A_customer_who_owes_nothing_is_not_reminded()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);

        var response = await admin.GetAsync($"/api/customers/{customerId}/reminder");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_salesman_can_send_a_reminder()
    {
        // Chasing udhaar is counter work, and the message carries nothing but what is owed.
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);

        var staff = await SignedInAsync(UserRole.Staff);
        var reminder = await ReminderAsync(staff, customerId);

        Decoded(reminder, "whatsAppUrl", "&text=").ToLowerInvariant()
            .Should().NotContain("cost").And.NotContain("profit").And.NotContain("rs 400");
    }

    [Fact]
    public async Task With_no_number_on_file_both_links_are_absent()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin, mobileNumber: null);

        await SellOnCreditAsync(admin, customerId, 1000m);

        var reminder = await ReminderAsync(admin, customerId);

        reminder.GetProperty("whatsAppUrl").ValueKind.Should().Be(JsonValueKind.Null);
        reminder.GetProperty("smsUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
