using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Ledger;

/// <summary>
/// Feature 004 — finding customers by sale type.
///
/// <para>The type is a standing label the owner sets (FR-101), never derived from invoices: a
/// wholesale party who buys one item at the counter must not be silently reclassified, and a
/// customer with no sales yet must not have a type invented for them.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CustomerSaleTypeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public CustomerSaleTypeTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message);

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

    private async Task<(long Id, string Name)> CreateNamedCustomerAsync(
        HttpClient client, string? saleType = null)
    {
        var name = $"Cust {Guid.NewGuid():N}"[..20];

        var response = await client.PostAsJsonAsync("/api/customers", new { name, saleType });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        return (data.GetProperty("id").GetInt64(), name);
    }

    private async Task<long> CreateCustomerAsync(HttpClient client, string? saleType = null) =>
        (await CreateNamedCustomerAsync(client, saleType)).Id;

    [Fact]
    public async Task A_new_customer_defaults_to_retail()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await CreateCustomerAsync(admin);

        var read = await admin.GetAsync($"/api/customers/{id}");
        var customer = (await read.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        customer.GetProperty("saleType").GetString().Should().Be("Retail");
    }

    [Fact]
    public async Task An_admin_can_mark_a_customer_wholesale()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await CreateCustomerAsync(admin, "Wholesale");

        var read = await admin.GetAsync($"/api/customers/{id}");
        var customer = (await read.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        customer.GetProperty("saleType").GetString().Should().Be("Wholesale");
    }

    [Fact]
    public async Task Filtering_by_wholesale_lists_only_wholesale_customers()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (wholesaleId, wholesaleName) = await CreateNamedCustomerAsync(admin, "Wholesale");
        var (_, retailName) = await CreateNamedCustomerAsync(admin, "Retail");

        // Narrowed by each customer's own unique name: the shared test database accumulates
        // customers across the whole suite, so a plain "list everything" would not reliably
        // see either of these two among however many already exist.
        var wholesaleSearch = await admin.GetAsync($"/api/customers?search={wholesaleName}&saleType=Wholesale");
        var wholesaleBody = await wholesaleSearch.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);
        var wholesaleIds = wholesaleBody!.Data!.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetInt64()).ToList();

        wholesaleIds.Should().Contain(wholesaleId);

        var retailSearch = await admin.GetAsync($"/api/customers?search={retailName}&saleType=Wholesale");
        var retailBody = await retailSearch.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);

        retailBody!.Data!.GetProperty("items").EnumerateArray().Should().BeEmpty(
            "the Retail customer must not appear under a Wholesale filter");
    }

    [Fact]
    public async Task No_filter_lists_every_type()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var prefix = $"Both {Guid.NewGuid():N}"[..15];

        var retailResponse = await admin.PostAsJsonAsync(
            "/api/customers", new { name = $"{prefix} Retail", saleType = "Retail" });
        var wholesaleResponse = await admin.PostAsJsonAsync(
            "/api/customers", new { name = $"{prefix} Wholesale", saleType = "Wholesale" });

        var retailId = (await retailResponse.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();
        var wholesaleId = (await wholesaleResponse.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var response = await admin.GetAsync($"/api/customers?search={prefix}");
        var body = await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);
        var ids = body!.Data!.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetInt64()).ToList();

        ids.Should().Contain([retailId, wholesaleId]);
    }

    [Fact]
    public async Task Changing_sale_type_does_not_touch_the_balance()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await CreateCustomerAsync(admin, "Retail");

        var before = await admin.GetAsync($"/api/customers/{id}");
        var beforeBalance = (await before.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("outstandingBalance").GetDecimal();

        var update = await admin.PutAsJsonAsync($"/api/customers/{id}", new
        {
            name = "Renamed Party",
            saleType = "Wholesale",
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await admin.GetAsync($"/api/customers/{id}");
        var afterData = (await after.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        afterData.GetProperty("saleType").GetString().Should().Be("Wholesale");
        afterData.GetProperty("outstandingBalance").GetDecimal().Should().Be(beforeBalance);
    }

    [Fact]
    public async Task A_salesman_creating_a_customer_cannot_set_the_sale_type()
    {
        // FR-105: only the owner may set it. The salesman's quick-create during a sale (FR-017)
        // must keep working — it just cannot smuggle a Wholesale label in.
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.PostAsJsonAsync("/api/customers", new
        {
            name = $"Staff-made {Guid.NewGuid():N}"[..20],
            saleType = "Wholesale",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
        data.GetProperty("saleType").GetString().Should().Be(
            "Retail",
            "the request came from Staff, so the attempted Wholesale label is ignored, not honoured");
    }

    [Fact]
    public async Task A_salesman_may_still_read_the_sale_type_and_filter_by_it()
    {
        // The screen the salesman uses to take payments (FR-104) — reading is unaffected.
        var admin = await ClientAsync(UserRole.Admin);
        await CreateCustomerAsync(admin, "Wholesale");

        var staff = await ClientAsync(UserRole.Staff);
        var response = await staff.GetAsync("/api/customers?saleType=Wholesale");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
