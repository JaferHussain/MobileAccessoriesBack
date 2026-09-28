using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Products;

/// <summary>
/// T021–T022, T029–T030 — narrowing the catalogue by brand, category and local brands
/// (FR-082 … FR-090).
///
/// <para>The brand and category filters already existed on the API before this feature; the
/// Products screen simply never offered them. Those tests are therefore expected to pass on first
/// run. If one fails, that is a real defect to fix — not a test to adjust.</para>
///
/// <para>Every test creates its own uniquely named brand and category so its results are its own,
/// independent of the rest of the shared integration database.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProductFilterTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ProductFilterTests(ApiFactory api) => _api = api;

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

    private static string Unique(string prefix) =>
        prefix + new string(Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(6).ToArray());

    /// <summary>
    /// A plain imported brand, for tests whose subject is something other than the brand. Every
    /// product needs one since 0027, so "no brand" is no longer an option a test can pass.
    /// </summary>
    private Task<long> AnyBrandAsync() => _api.EnsureBrandAsync(Unique("Brand"));

    private async Task<long> InsertAsync(
        string name, long categoryId, long brandId, decimal salePrice = 1100m, decimal wholesale = 950m)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, @categoryId, @brandId, 800, @wholesale, @salePrice, 10, 3, TRUE,
                    UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name, categoryId, brandId, salePrice, wholesale });
    }

    private static async Task<IReadOnlyList<JsonElement>> ItemsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/products?pageSize=100&{query}");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "Server said: {0}", await response.Content.ReadAsStringAsync());

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        return data.GetProperty("items").EnumerateArray().ToList();
    }

    private static async Task<IReadOnlyList<long>> IdsAsync(HttpClient client, string query) =>
        (await ItemsAsync(client, query)).Select(i => i.GetProperty("id").GetInt64()).ToList();

    // ---------------------------------------------------------------- brand and category (US2)

    [Fact]
    public async Task A_brand_filter_on_its_own_lists_only_that_brand()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var category = await _api.EnsureCategoryAsync(Unique("Cat"));
        var oppo = await _api.EnsureBrandAsync(Unique("Oppo"));
        var samsung = await _api.EnsureBrandAsync(Unique("Samsung"));

        var oppoProduct = await InsertAsync(Unique("P"), category, oppo);
        var samsungProduct = await InsertAsync(Unique("P"), category, samsung);

        var ids = await IdsAsync(admin, $"brandId={oppo}");

        ids.Should().Contain(oppoProduct);
        ids.Should().NotContain(samsungProduct);
    }

    [Fact]
    public async Task A_category_filter_on_its_own_lists_only_that_category()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var chargers = await _api.EnsureCategoryAsync(Unique("Chargers"));
        var cables = await _api.EnsureCategoryAsync(Unique("Cables"));

        var brand = await AnyBrandAsync();

        var charger = await InsertAsync(Unique("P"), chargers, brand);
        var cable = await InsertAsync(Unique("P"), cables, brand);

        var ids = await IdsAsync(admin, $"categoryId={chargers}");

        ids.Should().Contain(charger);
        ids.Should().NotContain(cable);
    }

    [Fact]
    public async Task Brand_and_category_together_must_both_match()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var chargers = await _api.EnsureCategoryAsync(Unique("Chargers"));
        var cables = await _api.EnsureCategoryAsync(Unique("Cables"));
        var oppo = await _api.EnsureBrandAsync(Unique("Oppo"));
        var samsung = await _api.EnsureBrandAsync(Unique("Samsung"));

        var oppoCharger = await InsertAsync(Unique("P"), chargers, oppo);
        var oppoCable = await InsertAsync(Unique("P"), cables, oppo);
        var samsungCharger = await InsertAsync(Unique("P"), chargers, samsung);

        // FR-083.
        var ids = await IdsAsync(admin, $"brandId={oppo}&categoryId={chargers}");

        ids.Should().Equal(oppoCharger);
        ids.Should().NotContain([oppoCable, samsungCharger]);
    }

    [Fact]
    public async Task A_filter_combines_with_search()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var chargers = await _api.EnsureCategoryAsync(Unique("Chargers"));
        var tag = Unique("zq");

        var brand = await AnyBrandAsync();

        var fast = await InsertAsync($"Charger 20W Fast {tag}", chargers, brand);
        var slow = await InsertAsync($"Charger 5W Basic {tag}", chargers, brand);

        var ids = await IdsAsync(admin, $"categoryId={chargers}&search={Uri.EscapeDataString($"fast {tag}")}");

        ids.Should().Contain(fast);
        ids.Should().NotContain(slow);
    }

    [Fact]
    public async Task A_combination_that_matches_nothing_is_an_empty_list_not_an_error()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var chargers = await _api.EnsureCategoryAsync(Unique("Chargers"));
        var oppo = await _api.EnsureBrandAsync(Unique("Oppo"));

        // An Oppo brand with nothing in this category.
        var ids = await IdsAsync(admin, $"brandId={oppo}&categoryId={chargers}");

        // The screen turns this into "No products match the chosen filters" (FR-086).
        ids.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- price is untouched (FR-090)

    [Fact]
    public async Task Filters_never_change_the_price_only_the_sale_type_does()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var chargers = await _api.EnsureCategoryAsync(Unique("Chargers"));
        var oppo = await _api.EnsureBrandAsync(Unique("Oppo"));
        var id = await InsertAsync(Unique("P"), chargers, oppo, salePrice: 1100m, wholesale: 950m);

        decimal PriceOf(IReadOnlyList<JsonElement> items) =>
            items.Single(i => i.GetProperty("id").GetInt64() == id).GetProperty("salePrice").GetDecimal();

        var unfiltered = PriceOf(await ItemsAsync(staff, $"categoryId={chargers}"));
        var byBrand = PriceOf(await ItemsAsync(staff, $"brandId={oppo}"));
        var byBoth = PriceOf(await ItemsAsync(staff, $"brandId={oppo}&categoryId={chargers}"));
        var wholesale = PriceOf(await ItemsAsync(staff, $"brandId={oppo}&saleType=Wholesale"));

        unfiltered.Should().Be(1100m);
        byBrand.Should().Be(1100m);
        byBoth.Should().Be(1100m);
        wholesale.Should().Be(950m, "the sale type decides the price; a filter never does");
    }

    // ---------------------------------------------------------------- local brands (US3)

    private async Task<long> BrandAsync(string prefix, bool isLocal)
    {
        var id = await _api.EnsureBrandAsync(Unique(prefix));

        await using var connection = await _api.OpenDatabaseAsync();
        await connection.ExecuteAsync(
            "UPDATE brands SET is_local = @isLocal WHERE id = @id;", new { id, isLocal });

        return id;
    }

    [Fact]
    public async Task Local_only_lists_products_of_local_brands()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var category = await _api.EnsureCategoryAsync(Unique("Earbuds"));
        var faster = await BrandAsync("Faster", isLocal: true);
        var samsung = await BrandAsync("Samsung", isLocal: false);

        var local = await InsertAsync(Unique("P"), category, faster);
        var imported = await InsertAsync(Unique("P"), category, samsung);

        // FR-087.
        var ids = await IdsAsync(admin, $"localOnly=true&categoryId={category}");

        ids.Should().Contain(local);
        ids.Should().NotContain(imported);
    }

    [Fact]
    public async Task A_product_cannot_be_stored_without_a_brand()
    {
        var category = await _api.EnsureCategoryAsync(Unique("Cables"));

        await using var connection = await _api.OpenDatabaseAsync();

        // Replaces An_unbranded_product_is_never_counted_as_local, which asserted behaviour that
        // can no longer arise: since 0027 every product carries a brand, and goods with no
        // well-known maker are filed under the shop's general "Local" brand instead.
        //
        // The guarantee that replaces it is stronger — the DATABASE refuses the row, so a product
        // cannot slip past the brand system through a repository, a script or a fix-up query,
        // which is exactly how the old unbranded rows arrived.
        var insert = async () => await connection.ExecuteAsync(
            """
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, @categoryId, NULL, 0, 0, 100, 1, 3, TRUE, UTC_TIMESTAMP(6));
            """,
            new { name = Unique("P"), categoryId = category });

        await insert.Should().ThrowAsync<MySqlConnector.MySqlException>(
            "brand_id is NOT NULL, so the column itself is the guard");
    }

    [Fact]
    public async Task Local_only_combines_with_category_and_search()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var earbuds = await _api.EnsureCategoryAsync(Unique("Earbuds"));
        var chargers = await _api.EnsureCategoryAsync(Unique("Chargers"));
        var faster = await BrandAsync("Faster", isLocal: true);
        var tag = Unique("zq");

        var localEarbuds = await InsertAsync($"Earbuds Basic {tag}", earbuds, faster);
        var localCharger = await InsertAsync($"Charger Basic {tag}", chargers, faster);

        // FR-088: AND with category...
        var byCategory = await IdsAsync(admin, $"localOnly=true&categoryId={earbuds}");
        byCategory.Should().Contain(localEarbuds);
        byCategory.Should().NotContain(localCharger);

        // ...and AND with search.
        var bySearch = await IdsAsync(
            admin, $"localOnly=true&search={Uri.EscapeDataString($"charger {tag}")}");
        bySearch.Should().Contain(localCharger);
        bySearch.Should().NotContain(localEarbuds);
    }

    [Fact]
    public async Task Local_only_with_an_imported_brand_is_empty_not_an_error()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var category = await _api.EnsureCategoryAsync(Unique("Chargers"));
        var oppo = await BrandAsync("Oppo", isLocal: false);
        await InsertAsync(Unique("P"), category, oppo);

        // Both filters apply as chosen; the screen explains the empty result.
        (await IdsAsync(admin, $"localOnly=true&brandId={oppo}")).Should().BeEmpty();
    }

    [Fact]
    public async Task The_list_says_whether_a_product_is_local_to_both_users()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var staff = await ClientAsync(UserRole.Staff);
        var category = await _api.EnsureCategoryAsync(Unique("Mixed"));
        var faster = await BrandAsync("Faster", isLocal: true);
        var samsung = await BrandAsync("Samsung", isLocal: false);

        var local = await InsertAsync(Unique("P"), category, faster);
        var imported = await InsertAsync(Unique("P"), category, samsung);

        foreach (var client in new[] { admin, staff })
        {
            var items = (await ItemsAsync(client, $"categoryId={category}"))
                .ToDictionary(
                    i => i.GetProperty("id").GetInt64(),
                    i => i.GetProperty("brandIsLocal").GetBoolean());

            items[local].Should().BeTrue();
            items[imported].Should().BeFalse();
        }

        // Not cost data, so it may reach Staff; it must not have brought cost with it.
        var raw = await (await staff.GetAsync($"/api/products?categoryId={category}"))
            .Content.ReadAsStringAsync();

        raw.Should().NotContain("costPrice");
    }
}
