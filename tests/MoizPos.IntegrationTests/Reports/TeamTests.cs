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
/// The owner's view of the team: what each person's job is, what they did, and what is worth a look.
///
/// <para>One owner; staff who work the counter (the shopkeeper) or the market (the salesman). Both
/// keep the Staff role — the job only says which work — and only the owner sees the Team screens.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TeamTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public TeamTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MoizPosTests/1.0");

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await DataAsync(login);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        return await LoginAsync(username, password);
    }

    /// <summary>A staff member created the way the owner creates one, and signed in.</summary>
    private async Task<(long Id, HttpClient Client)> StaffAsync(HttpClient admin, string? job = "Counter")
    {
        var username = $"staff{Guid.NewGuid():N}"[..14];

        var created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username,
            fullName = $"Ali {username[^4..]}",
            password = "Staff@12345",
            role = "Staff",
            job,
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        return ((await DataAsync(created)).GetProperty("id").GetInt64(), await LoginAsync(username, "Staff@12345"));
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
            new { name = $"Team {Guid.NewGuid():N}"[..18], categoryId, brandId });

        await _api.StockProductAsync(productId, quantity: 100, costPrice: 400m, salePrice: 1000m);

        return productId;
    }

    private async Task<long> SaleAsync(
        HttpClient client, decimal lineDiscount = 0m, string method = "Cash", long? customerId = null, decimal? amountPaid = null)
    {
        var response = await client.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = amountPaid ?? 1000m - lineDiscount,
            paymentMethod = method,
            items = new[] { new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1000m, lineDiscount } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await DataAsync(response)).GetProperty("invoiceId").GetInt64();
    }

    private async Task<long> ReturnAsync(HttpClient client, long invoiceId, string refundMethod = "Cash")
    {
        await using var connection = await _api.OpenDatabaseAsync();
        var itemId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM invoice_items WHERE invoice_id = @invoiceId LIMIT 1;", new { invoiceId });

        var response = await client.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId,
            refundMethod,
            items = new[] { new { invoiceItemId = itemId, quantity = 1 } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await DataAsync(response)).GetProperty("returnId").GetInt64();
    }

    private static string Today() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> MemberAsync(HttpClient admin, long userId) =>
        (await DataAsync(await admin.GetAsync($"/api/team?from={Today()}&to={Today()}")))
            .EnumerateArray().Single(member => member.GetProperty("userId").GetInt64() == userId);

    private static async Task<List<JsonElement>> ActivityAsync(HttpClient admin, long userId) =>
        (await DataAsync(await admin.GetAsync($"/api/team/{userId}/activity?from={Today()}&to={Today()}")))
            .EnumerateArray().ToList();

    private static async Task<List<JsonElement>> WatchListAsync(HttpClient admin) =>
        (await DataAsync(await admin.GetAsync($"/api/team/watchlist?from={Today()}&to={Today()}")))
            .EnumerateArray().ToList();

    // ================================================================
    //  Jobs
    // ================================================================

    [Fact]
    public async Task Each_member_of_staff_has_a_job_and_the_owner_can_change_it()
    {
        var admin = await AdminAsync();
        var (staffId, _) = await StaffAsync(admin, job: "FieldSales");

        var listed = (await DataAsync(await admin.GetAsync("/api/admin/users"))).EnumerateArray()
            .Single(user => user.GetProperty("id").GetInt64() == staffId);
        listed.GetProperty("job").GetString().Should().Be("FieldSales");

        (await admin.PutAsJsonAsync($"/api/admin/users/{staffId}/job", new { job = "Counter" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await DataAsync(await admin.GetAsync("/api/admin/users"))).EnumerateArray()
            .Single(user => user.GetProperty("id").GetInt64() == staffId)
            .GetProperty("job").GetString().Should().Be("Counter");
    }

    [Fact]
    public async Task Staff_created_without_a_job_work_the_counter()
    {
        var admin = await AdminAsync();
        var (staffId, _) = await StaffAsync(admin, job: null);

        (await MemberAsync(admin, staffId)).GetProperty("job").GetString().Should().Be("Counter");
    }

    [Fact]
    public async Task The_owner_has_no_job()
    {
        var admin = await AdminAsync();

        var created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = $"own{Guid.NewGuid():N}"[..12],
            fullName = "Second owner",
            password = "Owner@12345",
            role = "Admin",
            job = "Counter",
        });

        created.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ================================================================
    //  The overview
    // ================================================================

    [Fact]
    public async Task A_card_shows_what_each_person_did_today()
    {
        var admin = await AdminAsync();
        var (staffId, staff) = await StaffAsync(admin);

        var first = await SaleAsync(staff);
        await SaleAsync(staff, lineDiscount: 50m);
        await ReturnAsync(staff, first);

        // Udhaar the owner sold, recovered by the shopkeeper at the counter.
        var customer = await admin.PostAsJsonAsync("/api/customers", new { name = $"Cust {Guid.NewGuid():N}"[..14] });
        var customerId = (await DataAsync(customer)).GetProperty("id").GetInt64();
        await SaleAsync(admin, method: "Credit", customerId: customerId, amountPaid: 0m);
        (await staff.PostAsJsonAsync($"/api/customers/{customerId}/payments", new { amount = 300m, paymentMethod = "Cash" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var card = await MemberAsync(admin, staffId);

        card.GetProperty("invoiceCount").GetInt32().Should().Be(2);
        card.GetProperty("totalSales").GetDecimal().Should().Be(950m, "1,000 returned leaves 0; 950 after a 50 discount");
        card.GetProperty("discountGiven").GetDecimal().Should().Be(50m);
        card.GetProperty("returnCount").GetInt32().Should().Be(1);
        card.GetProperty("returnValue").GetDecimal().Should().Be(1000m);
        card.GetProperty("udhaarCollected").GetDecimal().Should().Be(300m);
        card.GetProperty("lastLoginUtc").ValueKind.Should().NotBe(JsonValueKind.Null, "the sign-in was recorded");
    }

    [Fact]
    public async Task The_team_screens_are_the_owners_alone()
    {
        var admin = await AdminAsync();
        var (staffId, staff) = await StaffAsync(admin);

        (await staff.GetAsync("/api/team")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.GetAsync($"/api/team/{staffId}/activity")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.GetAsync("/api/team/watchlist")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ================================================================
    //  One person's activity
    // ================================================================

    [Fact]
    public async Task The_timeline_lists_what_that_person_did_newest_first_and_nothing_of_anyone_else()
    {
        var admin = await AdminAsync();
        var (staffId, staff) = await StaffAsync(admin);
        var (_, other) = await StaffAsync(admin);

        var sale = await SaleAsync(staff);
        await ReturnAsync(staff, sale);
        var othersSale = await SaleAsync(other);

        var activity = await ActivityAsync(admin, staffId);

        activity.Select(row => row.GetProperty("kind").GetString())
            .Should().ContainInOrder("Return", "Sale", "SignIn");
        activity.Should().Contain(row => row.GetProperty("kind").GetString() == "Sale"
            && row.GetProperty("referenceId").GetInt64() == sale);
        activity.Should().NotContain(row => row.GetProperty("kind").GetString() == "Sale"
            && row.GetProperty("referenceId").GetInt64() == othersSale);
    }

    [Fact]
    public async Task A_sign_in_records_when_and_from_what()
    {
        var admin = await AdminAsync();
        var (staffId, _) = await StaffAsync(admin);

        var signIn = (await ActivityAsync(admin, staffId)).First(row => row.GetProperty("kind").GetString() == "SignIn");

        signIn.GetProperty("detail").GetString().Should().Contain("MoizPosTests");
    }

    // ================================================================
    //  Watch list
    // ================================================================

    [Fact]
    public async Task A_discount_of_a_tenth_or_more_is_flagged_and_a_small_one_is_not()
    {
        var admin = await AdminAsync();
        var (_, staff) = await StaffAsync(admin);

        var big = await SaleAsync(staff, lineDiscount: 150m);
        var small = await SaleAsync(staff, lineDiscount: 50m);

        var watch = await WatchListAsync(admin);

        watch.Should().Contain(item => item.GetProperty("kind").GetString() == "BigDiscount"
            && item.GetProperty("referenceId").GetInt64() == big);
        watch.Should().NotContain(item => item.GetProperty("referenceId").GetInt64() == small
            && item.GetProperty("kind").GetString() == "BigDiscount");
    }

    [Fact]
    public async Task A_transfer_sale_without_its_proof_is_flagged()
    {
        var admin = await AdminAsync();
        var (_, staff) = await StaffAsync(admin);

        var sale = await SaleAsync(staff, method: "JazzCash");

        (await WatchListAsync(admin)).Should().Contain(item =>
            item.GetProperty("kind").GetString() == "TransferWithoutProof"
            && item.GetProperty("referenceId").GetInt64() == sale);
    }

    [Fact]
    public async Task A_same_day_return_of_ones_own_sale_and_a_transfer_refund_are_flagged()
    {
        var admin = await AdminAsync();
        var (_, staff) = await StaffAsync(admin);

        var sale = await SaleAsync(staff);
        var returned = await ReturnAsync(staff, sale, refundMethod: "EasyPaisa");

        var watch = await WatchListAsync(admin);

        watch.Should().Contain(item => item.GetProperty("kind").GetString() == "SameDayReturn"
            && item.GetProperty("referenceId").GetInt64() == returned);
        watch.Should().Contain(item => item.GetProperty("kind").GetString() == "TransferRefund"
            && item.GetProperty("referenceId").GetInt64() == returned);
    }
}
