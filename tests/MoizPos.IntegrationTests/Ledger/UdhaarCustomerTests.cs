using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Ledger;

/// <summary>
/// Udhaar customers: registered by the owner — name, phone, both sides of the ID card — before
/// anyone may be sold to on full udhaar. A part payment is open to a walk-in too, as long as the
/// counter takes a name and phone, because a debt with no name can never be collected.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UdhaarCustomerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public UdhaarCustomerTests(ApiFactory api) => _api = api;

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
            new { name = $"Udh {Guid.NewGuid():N}"[..18], categoryId, brandId });

        await _api.StockProductAsync(productId, quantity: 50, costPrice: 600m, salePrice: 1000m);

        return productId;
    }

    /// <summary>An ordinary customer record with no udhaar mark — like any old contact in the register.</summary>
    private async Task<long> PlainCustomerAsync(string? mobile = "03007654321")
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO customers (name, mobile_number, outstanding_balance, is_active, created_at_utc)
            VALUES (@name, @mobile, 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Plain {Guid.NewGuid():N}"[..18], mobile });
    }

    private async Task<HttpResponseMessage> SellAsync(
        HttpClient client, decimal amountPaid, string method, long? customerId = null, object? newCustomer = null) =>
        await client.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            newCustomer,
            amountPaid,
            paymentMethod = method,
            items = new[] { new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m } },
        });

    private static MultipartFormDataContent Registration(bool front = true, bool back = true, string? mobile = "03001234567")
    {
        var form = new MultipartFormDataContent { { new StringContent($"Udhaar {Guid.NewGuid():N}"[..20]), "name" } };

        if (mobile is not null)
        {
            form.Add(new StringContent(mobile), "mobileNumber");
        }

        if (front)
        {
            form.Add(ApiFactory.Photo(), "idCardFront", "front.jpg");
        }

        if (back)
        {
            form.Add(ApiFactory.Photo(), "idCardBack", "back.jpg");
        }

        return form;
    }

    private static async Task<JsonElement> StatusAsync(HttpClient owner, long customerId) =>
        await DataAsync(await owner.GetAsync($"/api/udhaar-customers/{customerId}"));

    // ================================================================
    //  Registering
    // ================================================================

    [Fact]
    public async Task The_owner_registers_an_udhaar_customer_with_both_sides_of_the_id_card()
    {
        var owner = await SignedInAsync(UserRole.Admin);

        var id = await ApiFactory.RegisterUdhaarCustomerAsync(owner, "Rehman Mobiles");

        var status = await StatusAsync(owner, id);
        status.GetProperty("isUdhaarCustomer").GetBoolean().Should().BeTrue();
        status.GetProperty("hasIdCardFront").GetBoolean().Should().BeTrue();
        status.GetProperty("hasIdCardBack").GetBoolean().Should().BeTrue();
        status.GetProperty("idCardMissing").GetBoolean().Should().BeFalse();

        var listed = (await DataAsync(await owner.GetAsync("/api/udhaar-customers?search=Rehman")))
            .EnumerateArray().Select(row => row.GetProperty("id").GetInt64());
        listed.Should().Contain(id);

        var front = await owner.GetAsync($"/api/udhaar-customers/{id}/id-card/front");
        front.StatusCode.Should().Be(HttpStatusCode.OK);
        front.Content.Headers.ContentType!.MediaType.Should().StartWith("image/");
    }

    [Theory]
    [InlineData(false, true, "03001234567")]
    [InlineData(true, false, "03001234567")]
    [InlineData(true, true, null)]
    public async Task Registration_needs_the_phone_and_both_sides_of_the_card(bool front, bool back, string? mobile)
    {
        var owner = await SignedInAsync(UserRole.Admin);

        var response = await owner.PostAsync("/api/udhaar-customers", Registration(front, back, mobile));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_existing_customer_is_made_an_udhaar_customer_from_their_ledger()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var customerId = await PlainCustomerAsync();

        (await StatusAsync(owner, customerId)).GetProperty("isUdhaarCustomer").GetBoolean().Should().BeFalse();

        var form = new MultipartFormDataContent
        {
            { ApiFactory.Photo(), "idCardFront", "front.jpg" },
            { ApiFactory.Photo(), "idCardBack", "back.jpg" },
        };
        (await owner.PostAsync($"/api/udhaar-customers/{customerId}/register", form)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusAsync(owner, customerId)).GetProperty("isUdhaarCustomer").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_customer_marked_before_id_cards_were_asked_for_stays_eligible_and_is_flagged()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var customerId = await PlainCustomerAsync();

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            // As 0034's backfill left them: marked, no photos.
            await connection.ExecuteAsync("UPDATE customers SET credit_allowed = TRUE WHERE id = @customerId;", new { customerId });
        }

        (await StatusAsync(owner, customerId)).GetProperty("idCardMissing").GetBoolean().Should().BeTrue();
        (await SellAsync(owner, 0m, "Credit", customerId)).StatusCode.Should().Be(HttpStatusCode.Created);

        // Completing the card later clears the flag.
        var form = new MultipartFormDataContent
        {
            { ApiFactory.Photo(), "idCardFront", "front.jpg" },
            { ApiFactory.Photo(), "idCardBack", "back.jpg" },
        };
        await owner.PostAsync($"/api/udhaar-customers/{customerId}/register", form);

        (await StatusAsync(owner, customerId)).GetProperty("idCardMissing").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_owner_can_take_the_mark_away_and_the_id_card_is_kept_as_evidence()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var id = await ApiFactory.RegisterUdhaarCustomerAsync(owner);

        (await owner.DeleteAsync($"/api/udhaar-customers/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusAsync(owner, id)).GetProperty("isUdhaarCustomer").GetBoolean().Should().BeFalse();
        (await owner.GetAsync($"/api/udhaar-customers/{id}/id-card/back")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SellAsync(owner, 0m, "Credit", id)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_old_tick_can_no_longer_grant_udhaar_without_the_id_card()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var customerId = await PlainCustomerAsync();

        (await owner.PutAsJsonAsync($"/api/customers/{customerId}", new { name = "Someone", creditAllowed = true }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await owner.PostAsJsonAsync("/api/customers", new { name = "Someone else", creditAllowed = true }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ================================================================
    //  Owner only — and the ID card never leaves through the customer API
    // ================================================================

    [Fact]
    public async Task Only_the_owner_registers_lists_or_sees_an_id_card()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var staff = await SignedInAsync(UserRole.Staff);
        var id = await ApiFactory.RegisterUdhaarCustomerAsync(owner);

        (await staff.PostAsync("/api/udhaar-customers", Registration())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.GetAsync("/api/udhaar-customers")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.GetAsync($"/api/udhaar-customers/{id}/id-card/front")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.DeleteAsync($"/api/udhaar-customers/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_customer_api_never_carries_where_an_id_card_is_stored()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var staff = await SignedInAsync(UserRole.Staff);
        var id = await ApiFactory.RegisterUdhaarCustomerAsync(owner);

        foreach (var client in new[] { owner, staff })
        {
            var raw = await (await client.GetAsync($"/api/customers/{id}")).Content.ReadAsStringAsync();
            raw.Should().NotContainEquivalentOf("idCard").And.NotContain("id-cards");
        }
    }

    [Fact]
    public async Task Checkout_can_find_only_the_udhaar_customers()
    {
        var owner = await SignedInAsync(UserRole.Admin);
        var staff = await SignedInAsync(UserRole.Staff);
        // One shared name, so the search finds exactly these two among everything the test database keeps.
        var tag = $"Pick {Guid.NewGuid():N}"[..16];
        var marked = await ApiFactory.RegisterUdhaarCustomerAsync(owner, $"{tag} A");
        var plain = await PlainCustomerAsync();

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync("UPDATE customers SET name = @name WHERE id = @plain;", new { name = $"{tag} B", plain });
        }

        var found = (await DataAsync(await staff.GetAsync($"/api/customers?udhaarOnly=true&search={Uri.EscapeDataString(tag)}")))
            .GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt64()).ToList();

        found.Should().Equal(marked);
    }

    // ================================================================
    //  Who may owe what
    // ================================================================

    [Fact]
    public async Task Full_udhaar_is_only_for_a_registered_udhaar_customer_even_for_the_owner()
    {
        var owner = await SignedInAsync(UserRole.Admin);

        (await SellAsync(owner, 0m, "Credit", await PlainCustomerAsync())).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);

        (await SellAsync(owner, 0m, "Credit", await ApiFactory.RegisterUdhaarCustomerAsync(owner))).StatusCode
            .Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_walk_in_may_pay_part_once_the_counter_takes_a_name_and_phone()
    {
        var owner = await SignedInAsync(UserRole.Admin);

        var sale = await SellAsync(owner, 400m, "Partial", newCustomer: new { name = "Walk-in Asif", mobileNumber = "03211234567" });
        sale.StatusCode.Should().Be(HttpStatusCode.Created);

        var customerId = (await DataAsync(sale)).GetProperty("customerId").GetInt64();
        (await DataAsync(await owner.GetAsync($"/api/customers/{customerId}"))).GetProperty("outstandingBalance").GetDecimal()
            .Should().Be(600m);

        // A walk-in is not an udhaar customer: paying part is not becoming one.
        (await StatusAsync(owner, customerId)).GetProperty("isUdhaarCustomer").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_part_payment_without_a_phone_number_is_refused()
    {
        var owner = await SignedInAsync(UserRole.Admin);

        (await SellAsync(owner, 400m, "Partial", newCustomer: new { name = "Walk-in no phone" })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_walk_in_can_never_take_the_whole_bill_on_udhaar()
    {
        var owner = await SignedInAsync(UserRole.Admin);

        (await SellAsync(owner, 0m, "Credit", newCustomer: new { name = "Walk-in Bilal", mobileNumber = "03211234567" })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
