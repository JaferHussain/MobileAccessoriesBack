using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Products;

/// <summary>
/// Stock a field salesman carries out of the shop, and brings back — every unit tracked.
///
/// <para>quantity_on_hand stays what the shop OWNS: goods in his bag are still the shop's. What the
/// counter can sell is what is on the shelf — owned, less what salesmen carry — and what he can sell
/// is what is in his bag.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SalesmanStockTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public SalesmanStockTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _api.CreateClient();
        var data = await DataAsync(await client.PostAsJsonAsync("/api/auth/login", new { username, password }));

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var (_, username, password) = await _api.CreateUserAsync(UserRole.Admin);
        return await LoginAsync(username, password);
    }

    private async Task<(long Id, HttpClient Client)> StaffAsync(HttpClient admin, string job = "FieldSales")
    {
        var username = $"stk{Guid.NewGuid():N}"[..13];

        var created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username,
            fullName = $"Ali {username[^4..]}",
            password = "Staff@12345",
            role = "Staff",
            job,
        });

        return ((await DataAsync(created)).GetProperty("id").GetInt64(), await LoginAsync(username, "Staff@12345"));
    }

    private async Task<long> ProductAsync(int quantity = 10)
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
            new { name = $"Stk {Guid.NewGuid():N}"[..18], categoryId, brandId });

        await _api.StockProductAsync(productId, quantity: quantity, costPrice: 600m, salePrice: 1000m);

        return productId;
    }

    private static Task<HttpResponseMessage> IssueAsync(HttpClient admin, long userId, long productId, int quantity) =>
        admin.PostAsJsonAsync($"/api/salesman-stock/{userId}/issue", new
        {
            items = new[] { new { productId, quantity } },
            note = "Morning round",
        });

    private static Task<HttpResponseMessage> ReturnToShopAsync(HttpClient admin, long userId, long productId, int quantity) =>
        admin.PostAsJsonAsync($"/api/salesman-stock/{userId}/return", new
        {
            items = new[] { new { productId, quantity } },
        });

    private static Task<HttpResponseMessage> SellAsync(HttpClient client, long productId, int quantity) =>
        client.PostAsJsonAsync("/api/invoices", new
        {
            amountPaid = 1000m * quantity,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity, unitSalePrice = 1000m, lineDiscount = 0m } },
        });

    private static async Task<int> HoldingAsync(HttpClient client, long? userId, long productId)
    {
        var statement = await DataAsync(await client.GetAsync(userId is null ? "/api/salesman-stock/me" : $"/api/salesman-stock/{userId}"));

        return statement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("productId").GetInt64() == productId)
            .Select(item => item.GetProperty("quantity").GetInt32())
            .SingleOrDefault();
    }

    private async Task<int> OwnedAsync(long productId)
    {
        await using var connection = await _api.OpenDatabaseAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT quantity_on_hand FROM products WHERE id = @productId;", new { productId });
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
    }

    // ================================================================
    //  Issuing — the goods leave the shelf, never the shop's books
    // ================================================================

    [Fact]
    public async Task Issued_stock_leaves_the_shelf_but_the_shop_still_owns_it()
    {
        var admin = await AdminAsync();
        var (salesmanId, _) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);

        (await IssueAsync(admin, salesmanId, productId, 4)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await HoldingAsync(admin, salesmanId, productId)).Should().Be(4);
        (await OwnedAsync(productId)).Should().Be(10, "the goods in his bag are still the shop's");

        // The counter can sell only what is on the shelf: 10 owned, 4 in his bag.
        var tooMany = await SellAsync(admin, productId, 7);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(tooMany)).Should().Be("INSUFFICIENT_STOCK");

        (await SellAsync(admin, productId, 6)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_owner_cannot_issue_more_than_is_on_the_shelf()
    {
        var admin = await AdminAsync();
        var (salesmanId, _) = await StaffAsync(admin);
        var (otherId, _) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 5);

        await IssueAsync(admin, otherId, productId, 3);

        (await IssueAsync(admin, salesmanId, productId, 3)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await HoldingAsync(admin, salesmanId, productId)).Should().Be(0, "a refused issue moves nothing");
    }

    [Fact]
    public async Task Stock_is_issued_only_to_a_field_salesman()
    {
        var admin = await AdminAsync();
        var (counterId, _) = await StaffAsync(admin, job: "Counter");
        var productId = await ProductAsync();

        (await IssueAsync(admin, counterId, productId, 1)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ================================================================
    //  Selling from the bag
    // ================================================================

    [Fact]
    public async Task A_salesman_sells_out_of_his_own_bag()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);
        await IssueAsync(admin, salesmanId, productId, 3);

        (await SellAsync(salesman, productId, 2)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await HoldingAsync(salesman, null, productId)).Should().Be(1);
        (await OwnedAsync(productId)).Should().Be(8, "sold goods have left the shop for good");

        // Plenty on the shelf, but only one in his bag.
        var more = await SellAsync(salesman, productId, 2);
        more.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(more)).Should().Be("INSUFFICIENT_STOCK");
    }

    [Fact]
    public async Task A_salesman_cannot_sell_what_he_was_never_given()
    {
        var admin = await AdminAsync();
        var (_, salesman) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);

        var sale = await SellAsync(salesman, productId, 1);

        sale.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(sale)).Should().Be("INSUFFICIENT_STOCK");
        (await OwnedAsync(productId)).Should().Be(10);
    }

    [Fact]
    public async Task Goods_a_customer_hands_back_to_him_go_back_into_his_bag()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);
        await IssueAsync(admin, salesmanId, productId, 2);

        var sale = await SellAsync(salesman, productId, 2);
        var invoiceId = (await DataAsync(sale)).GetProperty("invoiceId").GetInt64();

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            var itemId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM invoice_items WHERE invoice_id = @invoiceId;", new { invoiceId });

            (await salesman.PostAsJsonAsync("/api/sale-returns", new
            {
                invoiceId,
                refundMethod = "Cash",
                items = new[] { new { invoiceItemId = itemId, quantity = 1 } },
            })).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        (await HoldingAsync(salesman, null, productId)).Should().Be(1);
        (await OwnedAsync(productId)).Should().Be(9);
    }

    // ================================================================
    //  Bringing it back
    // ================================================================

    [Fact]
    public async Task What_he_brings_back_returns_to_the_shelf()
    {
        var admin = await AdminAsync();
        var (salesmanId, _) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);
        await IssueAsync(admin, salesmanId, productId, 4);

        (await ReturnToShopAsync(admin, salesmanId, productId, 3)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await HoldingAsync(admin, salesmanId, productId)).Should().Be(1);
        (await SellAsync(admin, productId, 9)).StatusCode.Should().Be(HttpStatusCode.Created, "9 are on the shelf again");

        (await ReturnToShopAsync(admin, salesmanId, productId, 2)).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity, "he holds only one");
    }

    [Fact]
    public async Task Every_unit_in_and_out_of_his_bag_is_on_the_record()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);

        await IssueAsync(admin, salesmanId, productId, 5);
        await SellAsync(salesman, productId, 2);
        await ReturnToShopAsync(admin, salesmanId, productId, 1);

        var movements = (await DataAsync(await admin.GetAsync($"/api/salesman-stock/{salesmanId}")))
            .GetProperty("movements").EnumerateArray().ToList();

        movements.Select(m => m.GetProperty("reason").GetString()).Should().BeEquivalentTo("Issued", "Sold", "Returned");
        movements.Sum(m => m.GetProperty("changeQty").GetInt32()).Should().Be(2, "the movements add up to what he holds");
        (await HoldingAsync(admin, salesmanId, productId)).Should().Be(2);
    }

    // ================================================================
    //  Owned stock never falls below what salesmen carry
    // ================================================================

    [Fact]
    public async Task A_stock_correction_cannot_leave_the_shop_owning_less_than_salesmen_carry()
    {
        var admin = await AdminAsync();
        var (salesmanId, _) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);
        await IssueAsync(admin, salesmanId, productId, 4);

        (await admin.PostAsJsonAsync($"/api/products/{productId}/adjust-stock", new { newQuantity = 3, note = "Recount" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await admin.PostAsJsonAsync($"/api/products/{productId}/adjust-stock", new { newQuantity = 4, note = "Recount" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_team_card_shows_how_much_stock_he_is_carrying()
    {
        var admin = await AdminAsync();
        var (salesmanId, _) = await StaffAsync(admin);
        await IssueAsync(admin, salesmanId, await ProductAsync(), 3);
        await IssueAsync(admin, salesmanId, await ProductAsync(), 2);

        var card = (await DataAsync(await admin.GetAsync("/api/team")))
            .EnumerateArray().Single(member => member.GetProperty("userId").GetInt64() == salesmanId);

        card.GetProperty("stockUnits").GetInt32().Should().Be(5);
    }

    // ================================================================
    //  What each person reads on a product
    // ================================================================

    private static async Task<JsonElement> ProductAsync(HttpClient client, long productId) =>
        await DataAsync(await client.GetAsync($"/api/products/{productId}"));

    [Fact]
    public async Task The_counter_reads_what_is_on_the_shelf_and_how_many_are_out_with_salesmen()
    {
        var admin = await AdminAsync();
        var (salesmanId, _) = await StaffAsync(admin);
        var (_, shopkeeper) = await StaffAsync(admin, job: "Counter");
        var productId = await ProductAsync(quantity: 10);
        await IssueAsync(admin, salesmanId, productId, 4);

        foreach (var client in new[] { admin, shopkeeper })
        {
            var product = await ProductAsync(client, productId);

            product.GetProperty("quantityOnHand").GetInt32().Should().Be(10, "what the shop owns");
            product.GetProperty("atShop").GetInt32().Should().Be(6);
            product.GetProperty("withSalesmen").GetInt32().Should().Be(4);
            product.GetProperty("inYourBag").ValueKind.Should().Be(JsonValueKind.Null, "nobody at the counter carries a bag");
        }
    }

    [Fact]
    public async Task The_salesman_reads_what_is_in_his_own_bag_in_the_list_and_on_one_product()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin);
        var (otherId, _) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);
        await IssueAsync(admin, salesmanId, productId, 3);
        await IssueAsync(admin, otherId, productId, 2);

        (await ProductAsync(salesman, productId)).GetProperty("inYourBag").GetInt32().Should().Be(3, "his, not everyone's");

        await using var connection = await _api.OpenDatabaseAsync();
        var name = await connection.ExecuteScalarAsync<string>("SELECT name FROM products WHERE id = @productId;", new { productId });

        var listed = (await DataAsync(await salesman.GetAsync($"/api/products?search={Uri.EscapeDataString(name!)}")))
            .GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetInt64() == productId);

        listed.GetProperty("inYourBag").GetInt32().Should().Be(3);
        listed.GetProperty("withSalesmen").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task A_salesman_reads_zero_in_his_bag_for_anything_he_was_not_issued()
    {
        var admin = await AdminAsync();
        var (_, salesman) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);

        (await ProductAsync(salesman, productId)).GetProperty("inYourBag").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Low_stock_is_still_judged_on_what_the_shop_owns()
    {
        // Goods with the salesman will still be sold — reordering because they left the shelf
        // would raise "low stock" every morning the owner issues his round.
        var admin = await AdminAsync();
        var (salesmanId, _) = await StaffAsync(admin);
        var productId = await ProductAsync(quantity: 10);
        await IssueAsync(admin, salesmanId, productId, 9);

        (await ProductAsync(admin, productId)).GetProperty("isLowStock").GetBoolean().Should().BeFalse();
    }

    // ================================================================
    //  Who may do what
    // ================================================================

    [Fact]
    public async Task He_sees_his_own_bag_and_only_the_owner_moves_stock_in_or_out_of_it()
    {
        var admin = await AdminAsync();
        var (salesmanId, salesman) = await StaffAsync(admin);
        var (otherId, _) = await StaffAsync(admin);
        var productId = await ProductAsync();
        await IssueAsync(admin, salesmanId, productId, 2);

        (await HoldingAsync(salesman, null, productId)).Should().Be(2);

        (await salesman.GetAsync($"/api/salesman-stock/{otherId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await IssueAsync(salesman, salesmanId, productId, 1)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReturnToShopAsync(salesman, salesmanId, productId, 1)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
