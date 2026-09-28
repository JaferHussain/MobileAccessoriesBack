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
/// How a bill reaches the customer's phone.
///
/// <para>Two channels, both <b>deep links</b>: nothing is sent from the server. <c>wa.me</c> and
/// <c>sms:</c> both prepare a message in an app the shopkeeper already has, and they tap Send. So
/// the shop holds no messaging account, registers no sender id, and pays nothing per message.</para>
///
/// <para>And a walk-in — a sale with no customer, which is most counter sales — can supply a
/// number for that one send. Without it, most customers could never be sent their own bill.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ShareLinkChannelTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ShareLinkChannelTests(ApiFactory api) => _api = api;

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
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 800, 0,
                    1100, 50, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Chan {Guid.NewGuid():N}"[..20] });
    }

    /// <summary>A sale with a customer, who may or may not have a number on file.</summary>
    private async Task<long> SaleWithCustomerAsync(HttpClient admin, string? mobileNumber)
    {
        var created = await admin.PostAsJsonAsync("/api/customers", new
        {
            name = $"Cust {Guid.NewGuid():N}"[..18],
            mobileNumber,
        });

        var customerId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        return await SaleAsync(admin, customerId);
    }

    /// <summary>A walk-in: no customer at all.</summary>
    private Task<long> WalkInSaleAsync(HttpClient admin) => SaleAsync(admin, null);

    private async Task<long> SaleAsync(HttpClient admin, long? customerId)
    {
        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[]
            {
                new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m },
            },
        });

        sale.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await sale.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();
    }

    private async Task<JsonElement> ShareAsync(HttpClient admin, long invoiceId, string? mobileNumber = null)
    {
        var response = await admin.PostAsJsonAsync("/api/documents/share-link", new
        {
            documentType = "Invoice",
            referenceId = invoiceId,
            mobileNumber,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
    }

    private static string? Text(JsonElement share, string property) =>
        share.GetProperty(property).ValueKind == JsonValueKind.Null
            ? null
            : share.GetProperty(property).GetString();

    // ================================================================
    //  SMS as a second channel
    // ================================================================

    [Fact]
    public async Task A_share_link_carries_an_sms_link_beside_the_whatsapp_one()
    {
        var admin = await AdminAsync();
        var share = await ShareAsync(admin, await SaleWithCustomerAsync(admin, "03001234567"));

        Text(share, "smsUrl").Should().StartWith("sms:")
            .And.Contain("923001234567", "the same normalised number both channels use");
        Text(share, "whatsAppUrl").Should().StartWith("https://api.whatsapp.com/send?phone=");
    }

    [Fact]
    public async Task The_sms_states_the_figures_but_carries_no_link()
    {
        var admin = await AdminAsync();
        var share = await ShareAsync(admin, await SaleWithCustomerAsync(admin, "03001234567"));

        var sms = Uri.UnescapeDataString(Text(share, "smsUrl")!);
        var whatsApp = Uri.UnescapeDataString(Text(share, "whatsAppUrl")!);

        // The owner's choice: an SMS is the figures alone. Every character there is paid for, and
        // the link stays on WhatsApp, which costs nothing per line.
        sms.Should().Contain("body=", "the message is prepared for the shopkeeper to send");
        sms.Should().Contain("Total Amount");
        sms.Should().NotContain(Text(share, "shareUrl")!);
        whatsApp.Should().Contain(Text(share, "shareUrl")!);
    }

    [Fact]
    public async Task Both_channels_are_absent_together_when_there_is_no_number()
    {
        var admin = await AdminAsync();
        var share = await ShareAsync(admin, await SaleWithCustomerAsync(admin, null));

        // The screen then disables both and says why, rather than preparing a message to nobody.
        Text(share, "whatsAppUrl").Should().BeNull();
        Text(share, "smsUrl").Should().BeNull();
        Text(share, "shareUrl").Should().NotBeNull("the link itself is still there to copy");
    }

    // ================================================================
    //  A walk-in can supply a number
    // ================================================================

    [Fact]
    public async Task A_walk_in_can_be_sent_a_bill_using_a_number_typed_at_the_counter()
    {
        var admin = await AdminAsync();
        var share = await ShareAsync(admin, await WalkInSaleAsync(admin), "03009876543");

        Text(share, "whatsAppUrl").Should().Contain("923009876543");
        Text(share, "smsUrl").Should().Contain("923009876543");
    }

    [Fact]
    public async Task A_supplied_number_never_overrides_the_one_on_file()
    {
        var admin = await AdminAsync();
        var invoiceId = await SaleWithCustomerAsync(admin, "03001111111");

        var share = await ShareAsync(admin, invoiceId, "03009999999");

        // A stored number is the shop's own record of where a bill was sent. Silently sending
        // somewhere else would make that question unanswerable.
        Text(share, "whatsAppUrl").Should().Contain("923001111111");
        Text(share, "whatsAppUrl").Should().NotContain("923009999999");
    }

    [Fact]
    public async Task A_number_typed_for_a_walk_in_creates_no_customer_and_is_not_stored()
    {
        var admin = await AdminAsync();
        var invoiceId = await WalkInSaleAsync(admin);

        await using var connection = await _api.OpenDatabaseAsync();
        var customersBefore = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customers;");

        await ShareAsync(admin, invoiceId, "03007654321");

        var customersAfter = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customers;");

        customersAfter.Should().Be(customersBefore,
            "a one-off buyer is not a customer relationship, and recording them as one would fill " +
            "the list with people who will never return");

        var stillWalkIn = await connection.ExecuteScalarAsync<long?>(
            "SELECT customer_id FROM invoices WHERE id = @invoiceId;", new { invoiceId });

        stillWalkIn.Should().BeNull("the number is used once, not attached to the sale");
    }

    [Fact]
    public async Task An_unusable_supplied_number_yields_no_link_rather_than_a_broken_one()
    {
        var admin = await AdminAsync();

        var share = await ShareAsync(admin, await WalkInSaleAsync(admin), "not-a-number");

        Text(share, "whatsAppUrl").Should().BeNull();
        Text(share, "smsUrl").Should().BeNull();
    }

    [Fact]
    public async Task An_over_long_supplied_number_is_rejected_before_a_link_is_minted()
    {
        var admin = await AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/documents/share-link", new
        {
            documentType = "Invoice",
            referenceId = await WalkInSaleAsync(admin),
            mobileNumber = new string('9', 100),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
