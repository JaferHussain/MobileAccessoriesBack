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
/// What a payment acknowledgement tells the customer.
///
/// <para>The figures go in the <b>message body</b>, not only behind a link. Most customers will
/// never tap the link, and an acknowledgement that only works if opened acknowledges nothing —
/// which matters most here, because a payment against udhaar is the single most disputed event in
/// the shop.</para>
///
/// <para>And the figures are the ones <b>recorded on the ledger</b>, not the customer's balance as
/// it stands today. Those differ the moment the customer buys again, and a receipt that quietly
/// restates itself is worse than no receipt: it contradicts the shop's own book.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PaymentMessageTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public PaymentMessageTests(ApiFactory api) => _api = api;

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
            new { name = $"Msg {Guid.NewGuid():N}"[..20] });
    }

    private async Task<(long CustomerId, string Name)> CustomerAsync(HttpClient admin)
    {
        var name = $"Akhlaq {Guid.NewGuid():N}"[..16];

        var created = await admin.PostAsJsonAsync("/api/customers", new
        {
            name,
            mobileNumber = "03001234567",
        });

        var id = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        return (id, name);
    }

    /// <summary>Sells on full credit, leaving the whole amount owing.</summary>
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

    private async Task<long> ReceivePaymentAsync(HttpClient admin, long customerId, decimal amount)
    {
        var response = await admin.PostAsJsonAsync($"/api/customers/{customerId}/payments", new
        {
            amount,
            paymentMethod = "Cash",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("paymentId").GetInt64();
    }

    /// <summary>The prepared message text, decoded out of the WhatsApp deep link.</summary>
    private async Task<string> MessageAsync(HttpClient admin, long paymentId)
    {
        var response = await admin.PostAsJsonAsync("/api/documents/share-link", new
        {
            documentType = "PaymentReceipt",
            referenceId = paymentId,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var url = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("whatsAppUrl").GetString()!;

        return Uri.UnescapeDataString(url.Split("&text=")[^1]);
    }

    [Fact]
    public async Task The_message_states_what_was_received_and_what_remains()
    {
        var admin = await AdminAsync();
        var (customerId, _) = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);
        var paymentId = await ReceivePaymentAsync(admin, customerId, 500m);

        var message = await MessageAsync(admin, paymentId);

        // The owner's own example: paid 500 of 1,000, so 500 remains.
        message.Should().Contain("Remaining Amount: *Rs 500*");
        message.Should().MatchRegex("(?i)received");
        message.Should().MatchRegex("(?i)balance|remaining");
    }

    [Fact]
    public async Task The_message_greets_the_customer_by_name()
    {
        var admin = await AdminAsync();
        var (customerId, name) = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);
        var paymentId = await ReceivePaymentAsync(admin, customerId, 500m);

        // A receipt addressed to nobody reads like a broadcast. It is their money.
        (await MessageAsync(admin, paymentId)).Should().Contain(name);
    }

    /// <summary>
    /// The decisive one. Re-sharing an old receipt after the customer has bought again must still
    /// state the balance that receipt recorded.
    /// </summary>
    [Fact]
    public async Task The_balance_is_the_one_recorded_at_that_payment_not_todays()
    {
        var admin = await AdminAsync();
        var (customerId, _) = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);
        var paymentId = await ReceivePaymentAsync(admin, customerId, 500m);

        // They come back and buy more on credit: the live balance is now 1,500.
        await SellOnCreditAsync(admin, customerId, 1000m);

        var message = await MessageAsync(admin, paymentId);

        message.Should().NotContain("Rs 1,500",
            "the receipt records the balance at the moment it was issued; restating it later " +
            "would contradict the shop's own ledger");
        message.Should().Contain("Remaining Amount: *Rs 500*");
    }

    [Fact]
    public async Task A_payment_that_settles_the_account_says_so()
    {
        var admin = await AdminAsync();
        var (customerId, _) = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);
        var paymentId = await ReceivePaymentAsync(admin, customerId, 1000m);

        var message = await MessageAsync(admin, paymentId);

        // "Balance: Rs 0.00" is technically true and reads like an error. Nothing is owed.
        message.Should().MatchRegex("(?i)settled|cleared|nothing");
    }

    [Fact]
    public async Task The_message_reveals_no_cost_or_profit()
    {
        var admin = await AdminAsync();
        var (customerId, _) = await CustomerAsync(admin);

        await SellOnCreditAsync(admin, customerId, 1000m);
        var paymentId = await ReceivePaymentAsync(admin, customerId, 500m);

        var message = (await MessageAsync(admin, paymentId)).ToLowerInvariant();

        message.Should().NotContain("cost");
        message.Should().NotContain("profit");
        // The product was bought at 400; that figure has no business in a customer's message.
        message.Should().NotContain("rs 400");
    }
}
