using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Taxonomy;

/// <summary>
/// The Categories and Brands modules.
///
/// These exist so the owner maintains one list instead of retyping "Samsung" on every product.
/// The rules worth proving are the ones that protect that: names are unique, a rename reaches
/// every product at once, and a list still in use cannot be deleted out from under the stock.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CategoryAndBrandTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public CategoryAndBrandTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

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

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    // ---------------------------------------------------------------- categories

    [Fact]
    public async Task An_admin_can_create_a_category()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = Unique("Cat");

        var response = await admin.PostAsJsonAsync(
            "/api/categories", new { name, description = "Charging cables" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        created.GetProperty("name").GetString().Should().Be(name);
        created.GetProperty("isActive").GetBoolean().Should().BeTrue();
        created.GetProperty("productCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_duplicate_category_name_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = Unique("Dup");

        await admin.PostAsJsonAsync("/api/categories", new { name });

        // The whole point of the module is one row per category. Two "Cables" rows would put
        // half the stock under each and no report would agree with the other.
        var second = await admin.PostAsJsonAsync("/api/categories", new { name });

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_category_name_is_required()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/api/categories", new { name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Staff_can_read_categories_but_not_change_them()
    {
        var staff = await ClientAsync(UserRole.Staff);

        // The salesman's product list shows the category, so reading is allowed...
        (await staff.GetAsync("/api/categories")).StatusCode.Should().Be(HttpStatusCode.OK);

        // ...but the list itself is the owner's to maintain.
        var create = await staff.PostAsJsonAsync("/api/categories", new { name = Unique("Nope") });

        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Renaming_a_category_renames_it_on_every_product()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var original = Unique("Old");

        var created = await admin.PostAsJsonAsync("/api/categories", new { name = original });
        var categoryId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var brandId = await _api.EnsureBrandAsync(Unique("RenameBrand"));

        var product = await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("Prod"),
            categoryId,
            brandId,
            minStockThreshold = 1,
        });

        product.StatusCode.Should().Be(HttpStatusCode.Created);

        var productId = (await product.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Prices and stock arrive with the first delivery, not with the product.
        await _api.StockProductAsync(productId, quantity: 5, salePrice: 1000m);

        var renamed = Unique("New");
        await admin.PutAsJsonAsync($"/api/categories/{categoryId}", new { name = renamed });

        // This is what a shared list buys: one edit, and the whole catalogue follows.
        var after = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products/{productId}", Json);

        after!.Data!.GetProperty("category").GetString().Should().Be(renamed);
    }

    [Fact]
    public async Task A_category_reports_how_many_products_use_it()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var created = await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Count") });
        var categoryId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var brandId = await _api.EnsureBrandAsync(Unique("CountBrand"));

        await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("Prod"),
            categoryId,
            brandId,
            minStockThreshold = 1,
        });

        var fetched = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/categories/{categoryId}", Json);

        // Drives the warning shown before the owner retires a category.
        fetched!.Data!.GetProperty("productCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_retired_category_is_hidden_but_its_products_survive()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var created = await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Retire") });
        var categoryId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var brandId = await _api.EnsureBrandAsync(Unique("RetireBrand"));

        var product = await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("Prod"),
            categoryId,
            brandId,
            minStockThreshold = 1,
        });

        var productId = (await product.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Four on the shelf — the point of the test is that retiring a label does not destroy
        // stock the shop is holding, so it has to be holding some.
        await _api.StockProductAsync(productId, quantity: 4, costPrice: 1m, salePrice: 2m);

        var deleted = await admin.DeleteAsync($"/api/categories/{categoryId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The shop still holds this stock. Retiring a label must never destroy it.
        var survivor = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products/{productId}", Json);

        survivor!.Data!.GetProperty("quantityOnHand").GetInt32().Should().Be(4);

        // But it is no longer offered for new stock.
        var newProduct = await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("Prod"),
            categoryId,
            brandId,
            minStockThreshold = 1,
        });

        newProduct.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_product_pointed_at_a_category_that_does_not_exist_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("Orphan"),
            categoryId = 999_999_999L,
            brandId = await _api.EnsureBrandAsync(Unique("OrphanBrand")),
            minStockThreshold = 1,
        });

        // A clear 404, not the 500 the raw foreign key would produce.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_product_must_name_a_category()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("NoCat"),
            minStockThreshold = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- brands

    [Fact]
    public async Task An_admin_can_create_a_brand()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = Unique("Brand");

        var response = await admin.PostAsJsonAsync("/api/brands", new { name });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
        created.GetProperty("name").GetString().Should().Be(name);
    }

    [Fact]
    public async Task A_duplicate_brand_name_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = Unique("BDup");

        await admin.PostAsJsonAsync("/api/brands", new { name });

        (await admin.PostAsJsonAsync("/api/brands", new { name }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Staff_cannot_change_the_brand_list()
    {
        var staff = await ClientAsync(UserRole.Staff);

        (await staff.GetAsync("/api/brands")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await staff.PostAsJsonAsync("/api/brands", new { name = Unique("Nope") }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- local brands (FR-087a)

    [Fact]
    public async Task A_new_brand_is_imported_unless_marked_local()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/api/brands", new { name = Unique("Imp") });
        var created = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        // Nothing is local until the owner says so; guessing would misreport stock.
        created.GetProperty("isLocal").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_brand_can_be_created_as_local()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync(
            "/api/brands", new { name = Unique("Loc"), isLocal = true });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;
        created.GetProperty("isLocal").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Marking_a_brand_local_takes_effect_immediately()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = Unique("Flip");

        var created = await admin.PostAsJsonAsync("/api/brands", new { name });
        var id = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        await admin.PutAsJsonAsync($"/api/brands/{id}", new { name, isLocal = true });

        // SC-027: no delay, no cache to wait out.
        var afterLocal = await admin.GetFromJsonAsync<Envelope<JsonElement>>($"/api/brands/{id}", Json);
        afterLocal!.Data!.GetProperty("isLocal").GetBoolean().Should().BeTrue();

        await admin.PutAsJsonAsync($"/api/brands/{id}", new { name, isLocal = false });

        var afterImported = await admin.GetFromJsonAsync<Envelope<JsonElement>>($"/api/brands/{id}", Json);
        afterImported!.Data!.GetProperty("isLocal").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_salesman_cannot_mark_a_brand_local()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var staff = await ClientAsync(UserRole.Staff);
        var name = Unique("Guard");

        var created = await admin.PostAsJsonAsync("/api/brands", new { name });
        var id = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        (await staff.PutAsJsonAsync($"/api/brands/{id}", new { name, isLocal = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_product_must_name_a_brand()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var categoryId = await _api.EnsureCategoryAsync("Cables");

        // Reverses A_product_may_have_no_brand_at_all, which held until 0027. The owner's rule
        // is now that anything without a well-known maker is sold under the shop's own general
        // "Local" brand — so unbranded stock is filed, not exempt. That is also what makes the
        // product form able to ask for the brand first and offer only its categories.
        var response = await admin.PostAsJsonAsync("/api/products", new
        {
            name = Unique("Generic"),
            categoryId,
            minStockThreshold = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Products_can_be_filtered_by_category_and_by_brand()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var categoryId = await _api.EnsureCategoryAsync(Unique("FilterCat"));
        var brandId = await _api.EnsureBrandAsync(Unique("FilterBrand"));
        var name = Unique("Filtered");

        await admin.PostAsJsonAsync("/api/products", new
        {
            name,
            categoryId,
            brandId,
            minStockThreshold = 1,
        });

        var byCategory = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products?categoryId={categoryId}", Json);

        byCategory!.Data!.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Should().ContainSingle().Which.Should().Be(name);

        var byBrand = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products?brandId={brandId}", Json);

        byBrand!.Data!.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Should().ContainSingle().Which.Should().Be(name);
    }
}
