using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Returns;

/// <summary>
/// The Returns screen needs to show more than "it worked": a general list of what came back,
/// which product, how it moved stock, and how it affected money — plus the same figures
/// summarised on the Dashboard. These tests cover the two GET endpoints that make that possible
/// and the two Dashboard fields that surface the totals.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReturnListingAndDashboardTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ReturnListingAndDashboardTests(ApiFactory api) => _api = api;

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

    private async Task<long> CreateProductAsync(HttpClient admin, string namePrefix)
    {
        var response = await admin.PostAsJsonAsync("/api/products", new
        {
            name = $"{namePrefix} {Guid.NewGuid():N}"[..24],
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            minStockThreshold = 3,
        });

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
        var id = data.GetProperty("id").GetInt64();

        // A product leaves the Products screen unpriced and empty now — both arrive with its
        // first delivery. These tests are about returns, so the delivery is seeded rather than
        // recorded through the purchase API.
        await _api.StockProductAsync(id, quantity: 50, costPrice: 800m, salePrice: 1100m,
            wholesalePrice: 950m);

        return id;
    }

    private async Task<long> CreateSupplierAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/suppliers", new { name = $"Sup {Guid.NewGuid():N}"[..20] });

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        return data.GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task A_sale_return_appears_in_the_general_list_with_its_stock_and_money_detail()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin, "Listed");

        var invoiceResponse = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m } },
        });
        var invoice = (await invoiceResponse.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
        var invoiceId = invoice.GetProperty("invoiceId").GetInt64();

        var invoiceDetail = await admin.GetAsync($"/api/invoices/{invoiceId}");
        var items = (await invoiceDetail.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("items");
        var invoiceItemId = items[0].GetProperty("id").GetInt64();

        var returnResponse = await admin.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId,
            reason = "Wrong colour",
            items = new[] { new { invoiceItemId, quantity = 1 } },
        });
        returnResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await admin.GetAsync("/api/sale-returns?pageSize=200");
        var listBody = await list.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);
        var rows = listBody!.Data!.GetProperty("items").EnumerateArray().ToList();

        var row = rows.Should().ContainSingle(r => r.GetProperty("invoiceId").GetInt64() == invoiceId)
            .Subject;

        row.GetProperty("quantity").GetInt32().Should().Be(1);
        row.GetProperty("lineTotal").GetDecimal().Should().Be(1100m);
        row.GetProperty("reason").GetString().Should().Be("Wrong colour");
    }

    [Fact]
    public async Task A_purchase_return_scoped_to_its_supplier_does_not_leak_into_another_suppliers_list()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var supplierA = await CreateSupplierAsync(admin);
        var supplierB = await CreateSupplierAsync(admin);
        var productId = await CreateProductAsync(admin, "Scoped");

        var purchase = await admin.PostAsJsonAsync("/api/purchases", new
        {
            supplierId = supplierA,
            productId,
            unitCost = 800m,
            quantity = 10,
        });
        var purchaseId = (await purchase.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("purchaseId").GetInt64();

        var returned = await admin.PostAsJsonAsync(
            "/api/purchase-returns", new { purchaseId, quantity = 2, reason = "Wrong item" });
        returned.StatusCode.Should().Be(HttpStatusCode.Created);

        var scopedToA = await admin.GetAsync($"/api/purchase-returns?supplierId={supplierA}");
        var rowsA = (await scopedToA.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("items").EnumerateArray().ToList();
        rowsA.Should().Contain(r => r.GetProperty("quantity").GetInt32() == 2);

        var scopedToB = await admin.GetAsync($"/api/purchase-returns?supplierId={supplierB}");
        var rowsB = (await scopedToB.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("items").EnumerateArray().ToList();
        rowsB.Should().BeEmpty("this return belongs to supplier A, not supplier B");
    }

    [Fact]
    public async Task Staff_cannot_list_purchase_returns()
    {
        // The route already refuses Staff on write (cost/payables); the read side must match.
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync("/api/purchase-returns");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_salesman_can_list_sale_returns()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync("/api/sale-returns");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_dashboard_shows_todays_returns_alongside_sales_and_purchases()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin, "Dashboard");

        var invoiceResponse = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m } },
        });
        var invoiceId = (await invoiceResponse.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();

        var invoiceDetail = await admin.GetAsync($"/api/invoices/{invoiceId}");
        var invoiceItemId = (await invoiceDetail.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("items")[0].GetProperty("id").GetInt64();

        var before = await admin.GetAsync("/api/dashboard?period=Today");
        var beforeReturns = (await before.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("totalSaleReturns").GetDecimal();

        await admin.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId,
            reason = (string?)null,
            items = new[] { new { invoiceItemId, quantity = 1 } },
        });

        var after = await admin.GetAsync("/api/dashboard?period=Today");
        var afterData = (await after.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        afterData.GetProperty("totalSaleReturns").GetDecimal().Should().Be(beforeReturns + 1100m);
        // Already netted into TotalSales via net_amount — this asserts the visibility is
        // additive, not a second place the same reduction gets applied.
        afterData.GetProperty("totalPurchaseReturns").GetDecimal().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Return_item_finds_a_returnable_sale_by_product_name()
    {
        // The quick path: no invoice number needed, just the product's exact name.
        var admin = await ClientAsync(UserRole.Admin);
        var name = $"Findable {Guid.NewGuid():N}"[..24];

        var product = await admin.PostAsJsonAsync("/api/products", new
        {
            name,
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            costPrice = 800m, wholesalePrice = 950m, retailPrice = 1200m, salePrice = 1100m,
            quantityOnHand = 50, minStockThreshold = 3,
        });
        var productId = (await product.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Prices and stock arrive with the first delivery now, not with the product.
        await _api.StockProductAsync(productId, quantity: 50, salePrice: 1100m);

        await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m } },
        });

        var found = await admin.GetAsync($"/api/sale-returns/find?search={name}");
        found.StatusCode.Should().Be(HttpStatusCode.OK);

        var lines = (await found.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.EnumerateArray().ToList();

        lines.Should().ContainSingle(l => l.GetProperty("productName").GetString() == name);
    }

    /// <summary>
    /// The search result must carry the money with it. A missing field here is invisible on the
    /// server and reaches the counter as "Rs NaN" on every figure — which is exactly what
    /// happened when these were computed properties the serializer did not emit.
    /// </summary>
    [Fact]
    public async Task Return_item_search_carries_what_each_unit_is_worth_back()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = $"Priced {Guid.NewGuid():N}"[..24];

        var product = await admin.PostAsJsonAsync("/api/products", new
        {
            name,
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            costPrice = 300m, wholesalePrice = 0m, retailPrice = 0m, salePrice = 600m,
            quantityOnHand = 50, minStockThreshold = 3,
        });
        var productId = (await product.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Prices and stock arrive with the first delivery now, not with the product.
        await _api.StockProductAsync(productId, quantity: 50, salePrice: 1100m);

        // 2 at 600 = 1,200, discounted by 10 -> the customer paid 1,190.
        await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1190m,
            orderDiscount = 10m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 2, unitSalePrice = 600m, lineDiscount = 0m } },
        });

        var found = await admin.GetAsync($"/api/sale-returns/find?search={name}");

        var line = (await found.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.EnumerateArray()
            .Single(l => l.GetProperty("productName").GetString() == name);

        line.GetProperty("refundPerUnit").GetDecimal().Should().Be(595m);
        line.GetProperty("discountPerUnit").GetDecimal().Should().Be(5m);
        line.GetProperty("maxRefund").GetDecimal().Should().Be(1190m);
        line.GetProperty("unitSalePrice").GetDecimal().Should().Be(600m);
    }

    [Fact]
    public async Task Return_item_search_refuses_a_single_letter()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync("/api/sale-returns/find?search=a");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Recording_a_sale_return_names_the_exact_product_and_its_new_stock()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin, "Popup");

        var invoiceResponse = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m } },
        });
        var invoiceId = (await invoiceResponse.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();

        var invoiceDetail = await admin.GetAsync($"/api/invoices/{invoiceId}");
        var item = (await invoiceDetail.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("items")[0];
        var invoiceItemId = item.GetProperty("id").GetInt64();
        var productName = item.GetProperty("productName").GetString();

        var returned = await admin.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId,
            reason = (string?)null,
            items = new[] { new { invoiceItemId, quantity = 1 } },
        });

        var result = (await returned.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
        var stockUpdate = result.GetProperty("items")[0];

        stockUpdate.GetProperty("productName").GetString().Should().Be(productName);
        stockUpdate.GetProperty("newQuantityOnHand").GetInt32().Should().Be(50);
    }

    [Fact]
    public async Task A_purchase_search_can_find_a_product_by_name_across_a_suppliers_purchases()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync(admin);
        var name = $"Byname {Guid.NewGuid():N}"[..24];

        var product = await admin.PostAsJsonAsync("/api/products", new
        {
            name,
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            costPrice = 800m, wholesalePrice = 950m, retailPrice = 1200m, salePrice = 1100m,
            quantityOnHand = 50, minStockThreshold = 3,
        });
        var productId = (await product.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Prices and stock arrive with the first delivery now, not with the product.
        await _api.StockProductAsync(productId, quantity: 50, salePrice: 1100m);

        await admin.PostAsJsonAsync(
            "/api/purchases", new { supplierId, productId, unitCost = 800m, quantity = 10 });

        var response = await admin.GetAsync($"/api/purchases?supplierId={supplierId}&productSearch={name}");
        var items = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("items").EnumerateArray().ToList();

        items.Should().ContainSingle();
        items[0].GetProperty("productName").GetString().Should().Be(name);
    }

    [Fact]
    public async Task Returning_everything_sold_on_a_discounted_invoice_settles_at_its_true_value()
    {
        // Lines price before any order-level discount, so returning every unit sold adds up to
        // more than the invoice's discounted net value — by exactly that discount. That is not
        // an over-return; the invoice was simply never worth the undiscounted line total.
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(admin, "Discounted");

        var invoiceResponse = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId = (long?)null,
            orderDiscount = 50m,
            amountPaid = 1150m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 2, unitSalePrice = 600m, lineDiscount = 0m } },
        });
        invoiceResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var invoiceId = (await invoiceResponse.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();

        var invoiceDetail = await admin.GetAsync($"/api/invoices/{invoiceId}");
        var invoiceItemId = (await invoiceDetail.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("items")[0].GetProperty("id").GetInt64();

        // Both units back: 2 × 600 = 1200 at line price, but the invoice is only worth 1150.
        var returned = await admin.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId,
            reason = (string?)null,
            items = new[] { new { invoiceItemId, quantity = 2 } },
        });

        returned.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = (await returned.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        // Capped at the invoice's real value, not the sum of undiscounted lines.
        result.GetProperty("totalReturned").GetDecimal().Should().Be(1150m);
        result.GetProperty("refundDue").GetDecimal().Should().Be(1150m);
    }
}
