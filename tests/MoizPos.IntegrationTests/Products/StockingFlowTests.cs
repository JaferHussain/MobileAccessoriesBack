using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Products;

/// <summary>
/// The shop's stocking flow: <b>add the product, then stock it, then sell it.</b>
///
/// <para>A product is a catalogue entry — what the thing IS. It starts with nothing on the shelf
/// and no price. The first purchase supplies the cost, the selling prices and the quantity, all
/// in one transaction, and that is the moment it becomes sellable.</para>
///
/// <para><b>Why the prices are the purchase's and not the product's.</b> Two screens that can
/// both set a price is one more than the shop can keep straight: the moment someone uses the
/// wrong one, the counter quotes a figure the owner did not intend and nothing flags it. Putting
/// them on the delivery also makes them arrive WITH the stock they describe, so there is no
/// window in which a product has units on the shelf and no price against them.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StockingFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public StockingFlowTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private static string Unique(string prefix) =>
        prefix + new string(Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(8).ToArray());

    private async Task<HttpClient> ClientAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers
            .AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    /// <summary>Creates a catalogue entry — name and labels only, as the form now allows.</summary>
    private async Task<long> AddProductAsync(HttpClient admin, string? name = null)
    {
        var (categoryId, brandId) = await _api.EnsureCatalogueAsync();

        var response = await admin.PostAsJsonAsync("/api/products", new
        {
            name = name ?? Unique("Item"),
            categoryId,
            brandId,
            model = "M-1",
            minStockThreshold = 3,
        });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created, "Server said: {0}", await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();
    }

    private async Task<HttpResponseMessage> StockAsync(
        HttpClient admin,
        long productId,
        decimal unitCost,
        int quantity,
        decimal? retail = null,
        decimal? wholesale = null)
    {
        var supplierId = await _api.EnsureSupplierAsync();

        return await admin.PostAsJsonAsync("/api/purchases", new
        {
            supplierId,
            productId,
            unitCost,
            quantity,
            newRetailPrice = retail,
            newWholesalePrice = wholesale,
        });
    }

    private async Task<JsonElement> ReadProductAsync(HttpClient client, long id, string? saleType = null)
    {
        var url = saleType is null ? $"/api/products/{id}" : $"/api/products/{id}?saleType={saleType}";
        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
    }

    // ---------------------------------------------------------------- step 1: add the product

    [Fact]
    public async Task A_new_product_starts_with_no_stock_and_no_price()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        var product = await ReadProductAsync(admin, id);

        product.GetProperty("quantityOnHand").GetInt32().Should().Be(0);
        product.GetProperty("salePrice").GetDecimal().Should().Be(0m);
        product.GetProperty("costPrice").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task The_product_form_cannot_set_a_price_even_if_one_is_posted()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var (categoryId, brandId) = await _api.EnsureCatalogueAsync();

        // Someone posting the old shape directly. The fields are gone from the request, so they
        // are ignored rather than honoured — the product is still created, still unpriced.
        var response = await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("Sneaky"),
            categoryId,
            brandId,
            minStockThreshold = 1,
            costPrice = 500m,
            salePrice = 900m,
            quantityOnHand = 50,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var product = await ReadProductAsync(admin, id);

        product.GetProperty("salePrice").GetDecimal().Should().Be(0m, "prices come from a purchase");
        product.GetProperty("quantityOnHand").GetInt32().Should().Be(0, "stock comes from a purchase");
    }

    // ---------------------------------------------------------------- step 2: stock it

    [Fact]
    public async Task The_first_purchase_sets_the_cost_the_prices_and_the_quantity_at_once()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        var response = await StockAsync(admin, id, unitCost: 800m, quantity: 10,
            retail: 1100m, wholesale: 950m);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created, "Server said: {0}", await response.Content.ReadAsStringAsync());

        var product = await ReadProductAsync(admin, id);

        product.GetProperty("quantityOnHand").GetInt32().Should().Be(10);
        product.GetProperty("costPrice").GetDecimal().Should().Be(800m);
        product.GetProperty("salePrice").GetDecimal().Should().Be(1100m);
        product.GetProperty("wholesalePrice").GetDecimal().Should().Be(950m);
    }

    [Fact]
    public async Task A_first_purchase_without_a_retail_price_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        // Stocking a product without pricing it would put units on the shelf that the counter
        // would quote at zero. The one delivery that MUST state a price is the first.
        var response = await StockAsync(admin, id, unitCost: 800m, quantity: 10, retail: null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no selling price yet");
    }

    [Fact]
    public async Task A_refused_first_purchase_leaves_the_product_exactly_as_it_was()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        await StockAsync(admin, id, unitCost: 800m, quantity: 10, retail: null);

        // The check sits after the row lock and before the first write, so a refusal must leave
        // no stock, no cost and no purchase row behind.
        var product = await ReadProductAsync(admin, id);

        product.GetProperty("quantityOnHand").GetInt32().Should().Be(0);
        product.GetProperty("costPrice").GetDecimal().Should().Be(0m);

        await using var connection = await _api.OpenDatabaseAsync();
        var purchases = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM purchases WHERE product_id = @id;", new { id });

        purchases.Should().Be(0);
    }

    [Fact]
    public async Task Wholesale_is_optional_and_falls_back_to_the_retail_price()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        // A shop that does not sell wholesale should not be made to invent a second price.
        (await StockAsync(admin, id, unitCost: 800m, quantity: 5, retail: 1100m))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var quoted = await ReadProductAsync(admin, id, saleType: "Wholesale");

        quoted.GetProperty("salePrice").GetDecimal().Should().Be(1100m);
    }

    [Fact]
    public async Task A_repeat_purchase_keeps_the_prices_when_none_are_given()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        await StockAsync(admin, id, unitCost: 800m, quantity: 10, retail: 1100m, wholesale: 950m);

        // A second delivery at the same price should not make the shopkeeper retype what the
        // shop already knows.
        (await StockAsync(admin, id, unitCost: 850m, quantity: 10))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var product = await ReadProductAsync(admin, id);

        product.GetProperty("salePrice").GetDecimal().Should().Be(1100m);
        product.GetProperty("wholesalePrice").GetDecimal().Should().Be(950m);

        // The cost still follows the latest purchase — that rule is unchanged.
        product.GetProperty("costPrice").GetDecimal().Should().Be(850m);
        product.GetProperty("quantityOnHand").GetInt32().Should().Be(20);
    }

    [Fact]
    public async Task A_repeat_purchase_can_re_price_every_unit_on_hand()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        await StockAsync(admin, id, unitCost: 800m, quantity: 10, retail: 1100m);
        await StockAsync(admin, id, unitCost: 850m, quantity: 10, retail: 1200m);

        // Old stock sells at today's price — the shop's second business rule. All 20 units are
        // now 1,200, including the ten bought when it was 1,100.
        var product = await ReadProductAsync(admin, id);

        product.GetProperty("salePrice").GetDecimal().Should().Be(1200m);
        product.GetProperty("quantityOnHand").GetInt32().Should().Be(20);
    }

    // ---------------------------------------------------------------- step 3: sell it

    [Fact]
    public async Task A_product_that_has_never_been_stocked_cannot_be_sold()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        // The client supplies the unit price, so without this check an unpriced product would
        // sell at whatever the caller claimed. Refused from the product's own state instead.
        var response = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 900m,
            paymentMethod = "Cash",
            items = new[] { new { productId = id, quantity = 1, unitSalePrice = 900m, lineDiscount = 0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no selling price yet");
    }

    [Fact]
    public async Task Once_stocked_the_product_sells()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        await StockAsync(admin, id, unitCost: 800m, quantity: 10, retail: 1100m);

        var response = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[] { new { productId = id, quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m } },
        });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created, "Server said: {0}", await response.Content.ReadAsStringAsync());

        // Add product → stock it → sell it. The whole flow, in one test.
        var product = await ReadProductAsync(admin, id);
        product.GetProperty("quantityOnHand").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task The_counter_is_quoted_the_price_the_purchase_set()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var staff = await ClientAsync(UserRole.Staff);
        var id = await AddProductAsync(admin);

        await StockAsync(admin, id, unitCost: 800m, quantity: 10, retail: 1100m, wholesale: 950m);

        (await ReadProductAsync(staff, id, saleType: "Retail"))
            .GetProperty("salePrice").GetDecimal().Should().Be(1100m);

        (await ReadProductAsync(staff, id, saleType: "Wholesale"))
            .GetProperty("salePrice").GetDecimal().Should().Be(950m);
    }

    // ---------------------------------------------------------------- editing afterwards

    [Fact]
    public async Task Editing_a_product_never_disturbs_its_price_or_its_stock()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var id = await AddProductAsync(admin);

        await StockAsync(admin, id, unitCost: 800m, quantity: 10, retail: 1100m, wholesale: 950m);

        var (categoryId, brandId) = await _api.EnsureCatalogueAsync();

        // Fixing a typo in the name must not re-price the shelf. This is why the UPDATE statement
        // does not mention a price column at all.
        var renamed = await admin.PutAsJsonAsync($"/api/products/{id}", new
        {
            name = Unique("Renamed"),
            categoryId,
            brandId,
            minStockThreshold = 5,
        });

        renamed.StatusCode.Should().Be(
            HttpStatusCode.OK, "Server said: {0}", await renamed.Content.ReadAsStringAsync());

        var product = await ReadProductAsync(admin, id);

        product.GetProperty("salePrice").GetDecimal().Should().Be(1100m);
        product.GetProperty("wholesalePrice").GetDecimal().Should().Be(950m);
        product.GetProperty("costPrice").GetDecimal().Should().Be(800m);
        product.GetProperty("quantityOnHand").GetInt32().Should().Be(10);
    }
}
