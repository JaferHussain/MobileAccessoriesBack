using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Products;

/// <summary>
/// T051 / T056 / T061 — the catalogue over HTTP, including the check that matters most:
/// a Staff token must never receive a cost price (FR-040).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProductEndpointsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ProductEndpointsTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message);

    private sealed record Paged<T>(T[] Items, int Page, int PageSize, int TotalItems, int TotalPages);

    private sealed record ProductBody(
        long Id,
        string Name,
        string Category,
        string? Brand,
        string? Barcode,
        decimal SalePrice,
        int QuantityOnHand,
        bool IsLowStock);

    private async Task<HttpClient> ClientAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var body = await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);
        var token = body!.Data!.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// <summary>
    /// A valid create/update payload. Category and brand are ids now, so this resolves them
    /// through the taxonomy modules rather than posting free text.
    /// </summary>
    private async Task<object> NewProductAsync(
        string name, string? barcode = null)
    {
        var (categoryId, brandId) = await _api.EnsureCatalogueAsync("Cables", "Baseus");

        // Identity only. Prices and quantity are a purchase's to set, so they are not on this
        // request at all — tests that need something sellable stock it afterwards.
        return new
        {
            name,
            categoryId,
            brandId,
            model = "CATZ-01",
            barcode,
            minStockThreshold = 3,
        };
    }

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..24];

    // ------------------------------------------------------------ creation

    [Fact]
    public async Task Admin_can_create_a_product()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable")));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Staff_cannot_create_a_product()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable")));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rejects_a_product_with_a_negative_price()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("Cable"), categoryId = await _api.EnsureCategoryAsync("Cables"),
            costPrice = -1m, wholesalePrice = 0m, retailPrice = 0m, salePrice = 0m,
            quantityOnHand = 0, minStockThreshold = 0,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<Envelope<object>>(Json);
        body!.Error!.Code.Should().Be("VALIDATION_FAILED");
    }

    [Fact]
    public async Task Rejects_a_duplicate_barcode()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var barcode = Guid.NewGuid().ToString("N")[..12];

        await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable"), barcode));

        var second = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable"), barcode));

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // -------------------------------------------------------------- search

    [Fact]
    public async Task Search_matches_by_name_brand_category_and_barcode()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = Unique("Braided");
        var barcode = Guid.NewGuid().ToString("N")[..12];

        await admin.PostAsJsonAsync("/api/products", await NewProductAsync(name, barcode));

        foreach (var term in new[] { name[..8], "Baseus", "Cables", barcode })
        {
            var response = await admin.GetAsync($"/api/products?search={Uri.EscapeDataString(term)}");
            var body = await response.Content.ReadFromJsonAsync<Envelope<Paged<ProductBody>>>(Json);

            body!.Data!.Items.Should().NotBeEmpty($"search term '{term}' should match (FR-003)");
        }
    }

    [Fact]
    public async Task Search_is_paged()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync("/api/products?page=1&pageSize=2");
        var body = await response.Content.ReadFromJsonAsync<Envelope<Paged<ProductBody>>>(Json);

        body!.Data!.PageSize.Should().Be(2);
        body.Data.Items.Length.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Page_size_is_capped()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync("/api/products?pageSize=5000");
        var body = await response.Content.ReadFromJsonAsync<Envelope<Paged<ProductBody>>>(Json);

        body!.Data!.PageSize.Should().Be(100);
    }

    // ----------------------------------------------------------- low stock

    [Fact]
    public async Task Flags_a_product_at_or_below_its_threshold_as_low_stock()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = Unique("LowCable");

        var created = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(name));
        var id = (await created.Content.ReadFromJsonAsync<Envelope<ProductBody>>(Json))!.Data!.Id;

        // Threshold 3, quantity 3 -> at the threshold, which counts as low (FR-004). Stocked
        // separately now, because a product is created empty.
        await _api.StockProductAsync(id, quantity: 3);

        var refetched = await admin.GetFromJsonAsync<Envelope<ProductBody>>($"/api/products/{id}", Json);

        refetched!.Data!.IsLowStock.Should().BeTrue();
    }

    [Fact]
    public async Task Low_stock_filter_returns_only_products_needing_reorder()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var low = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("LowCable")));
        var full = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("FullCable")));

        await _api.StockProductAsync(
            (await low.Content.ReadFromJsonAsync<Envelope<ProductBody>>(Json))!.Data!.Id, quantity: 2);
        await _api.StockProductAsync(
            (await full.Content.ReadFromJsonAsync<Envelope<ProductBody>>(Json))!.Data!.Id, quantity: 500);

        var response = await admin.GetAsync("/api/products?lowStockOnly=true&pageSize=100");
        var body = await response.Content.ReadFromJsonAsync<Envelope<Paged<ProductBody>>>(Json);

        body!.Data!.Items.Should().OnlyContain(p => p.IsLowStock);
    }

    // ------------------------------------------ THE cost confidentiality check

    [Fact]
    public async Task Staff_response_contains_no_cost_price_key_at_all()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var created = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable")));
        var createdBody = await created.Content.ReadFromJsonAsync<Envelope<ProductBody>>(Json);
        var id = createdBody!.Data!.Id;

        var staff = await ClientAsync(UserRole.Staff);
        var response = await staff.GetAsync($"/api/products/{id}");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Absent, not null. FR-040 and quickstart V7.
        raw.Should().NotContain("costPrice");
        raw.Should().NotContain("wholesalePrice");
        raw.Should().NotContain("800");
    }

    [Fact]
    public async Task Staff_list_response_contains_no_cost_price()
    {
        var admin = await ClientAsync(UserRole.Admin);
        await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable")));

        var staff = await ClientAsync(UserRole.Staff);
        var raw = await (await staff.GetAsync("/api/products?pageSize=100")).Content.ReadAsStringAsync();

        raw.Should().NotContain("costPrice");
    }

    [Fact]
    public async Task Admin_response_does_include_the_cost_price()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var created = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable")));
        var body = await created.Content.ReadFromJsonAsync<Envelope<ProductBody>>(Json);

        var raw = await (await admin.GetAsync($"/api/products/{body!.Data!.Id}")).Content.ReadAsStringAsync();

        raw.Should().Contain("costPrice");
    }

    [Fact]
    public async Task Staff_can_still_search_products_to_make_a_sale()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync("/api/products?search=cable");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------- updates

    [Fact]
    public async Task Updating_a_product_does_not_change_its_stock_or_cost()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var created = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable")));
        var id = (await created.Content.ReadFromJsonAsync<Envelope<ProductBody>>(Json))!.Data!.Id;

        // Priced and stocked by its first delivery, which is the only thing that can do either.
        await _api.StockProductAsync(id, quantity: 10, costPrice: 800m, salePrice: 1100m);

        var (categoryId, brandId) = await _api.EnsureCatalogueAsync("Cables", "Baseus");

        // Attempt to smuggle a new quantity and cost through the edit form. The fields are not
        // even on the request any more, so they are ignored rather than applied.
        await admin.PutAsJsonAsync($"/api/products/{id}", new
        {
            name = "Renamed Cable", categoryId, brandId,
            costPrice = 1m, wholesalePrice = 10m, retailPrice = 20m, salePrice = 30m,
            quantityOnHand = 9999, minStockThreshold = 5,
        });

        var raw = await (await admin.GetAsync($"/api/products/{id}")).Content.ReadAsStringAsync();

        raw.Should().Contain("Renamed Cable");
        // Stock and cost move only through purchases, sales, returns and audited adjustments.
        raw.Should().NotContain("9999");
        raw.Should().Contain("800");
    }

    [Fact]
    public async Task Deactivating_hides_a_product_without_deleting_it()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = Unique("Retired");
        var created = await admin.PostAsJsonAsync("/api/products", await NewProductAsync(name));
        var id = (await created.Content.ReadFromJsonAsync<Envelope<ProductBody>>(Json))!.Data!.Id;

        var deleted = await admin.DeleteAsync($"/api/products/{id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Gone from the default listing...
        var listRaw = await (await admin.GetAsync($"/api/products?search={Uri.EscapeDataString(name[..8])}"))
            .Content.ReadAsStringAsync();
        listRaw.Should().NotContain(name);

        // ...but the row still exists, so old invoices remain readable (FR-002).
        var fetched = await admin.GetAsync($"/api/products/{id}");
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Fetching_an_unknown_product_returns_not_found()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync("/api/products/999999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------ barcode

    [Fact]
    public async Task Barcode_lookup_finds_the_product()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var barcode = Guid.NewGuid().ToString("N")[..12];
        await admin.PostAsJsonAsync("/api/products", await NewProductAsync(Unique("Cable"), barcode));

        var response = await admin.GetAsync($"/api/products/by-barcode/{barcode}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_barcode_is_reported_clearly()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync("/api/products/by-barcode/nosuchbarcode");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------- supplier access

    [Fact]
    public async Task Staff_cannot_reach_any_supplier_route()
    {
        var staff = await ClientAsync(UserRole.Staff);

        foreach (var url in new[] { "/api/suppliers", "/api/suppliers/1", "/api/purchases" })
        {
            (await staff.GetAsync(url)).StatusCode.Should().Be(
                HttpStatusCode.Forbidden, $"{url} exposes financial data");
        }
    }

    [Fact]
    public async Task Admin_can_create_and_list_suppliers()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var created = await admin.PostAsJsonAsync("/api/suppliers", new
        {
            name = Unique("Ali Traders"), contactNumber = "03001234567", address = "Lodhran",
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        (await admin.GetAsync("/api/suppliers")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_refused()
    {
        var anonymous = _api.CreateClient();

        (await anonymous.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
