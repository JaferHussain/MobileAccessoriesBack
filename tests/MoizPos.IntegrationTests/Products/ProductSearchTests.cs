using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Products;

/// <summary>
/// T009–T013 — finding a product the way the shopkeeper types it (FR-074 … FR-081, FR-089).
///
/// <para>The integration database is shared with the rest of the suite, including a
/// 5,000-product performance catalogue that has its own "Chargers" category. So every test here
/// tags its products with a word unique to that test, includes it in searches that could otherwise
/// return hundreds of rows, and asserts on the ids it created — never on "the whole result".</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProductSearchTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ProductSearchTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private sealed record Catalogue(string Tag, long TypeC, long OppoCharger, long SamsungCharger, long GenericCable);

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

    /// <summary>
    /// The quickstart catalogue. The tag is letters only, so it survives normalisation as a single
    /// searchable word.
    /// </summary>
    private async Task<Catalogue> SeedAsync()
    {
        // Letters only, so the tag survives search normalisation as one searchable word — but
        // NOT by filtering a GUID for letters: hex letters are a-f, which is a six-symbol
        // alphabet. At six characters that is ~46k tags, and since the seeded barcode is unique
        // and these rows accumulate, seeds began colliding and failing a random test with
        // "Duplicate entry for key 'products.uq_products_barcode'". Mapping every hex digit onto
        // its own letter keeps it letters-only while restoring the full 16 symbols.
        var tag = "zq" + new string(
            Guid.NewGuid().ToString("N").Take(10).Select(HexToLetter).ToArray());

        var cables = await _api.EnsureCategoryAsync("Cables");
        var chargers = await _api.EnsureCategoryAsync("Chargers");
        var baseus = await _api.EnsureBrandAsync("Baseus");
        var oppo = await _api.EnsureBrandAsync("Oppo");
        var samsung = await _api.EnsureBrandAsync("Samsung");
        var local = await _api.EnsureBrandAsync("Local");

        return new Catalogue(
            tag,
            TypeC: await InsertAsync($"Type-C Braided Cable {tag}", cables, baseus, barcode: $"TC{tag}"),
            OppoCharger: await InsertAsync($"Charger 20W Fast {tag}", chargers, oppo),
            SamsungCharger: await InsertAsync($"Charger 18W {tag}", chargers, samsung),
            // Goods with no well-known maker are filed under the shop's general "Local" brand
            // since 0027 — there is no such thing as a product with no brand any more.
            GenericCable: await InsertAsync($"Micro USB Cable {tag}", cables, local));
    }

    /// <summary>'0'-'9' become 'g'-'p'; 'a'-'f' are already letters and pass through.</summary>
    private static char HexToLetter(char hex) =>
        char.IsDigit(hex) ? (char)('g' + (hex - '0')) : hex;

    private async Task<long> InsertAsync(string name, long categoryId, long brandId, string? barcode = null)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO products
                (name, category_id, brand_id, barcode, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, @categoryId, @brandId, @barcode, 800, 0, 1100, 10, 3, TRUE,
                    UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name, categoryId, brandId, barcode });
    }

    private static async Task<IReadOnlyList<long>> IdsAsync(HttpClient client, string search)
    {
        var response = await client.GetAsync(
            $"/api/products?pageSize=100&search={Uri.EscapeDataString(search)}");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "search '{0}' should run. Server said: {1}",
            search,
            await response.Content.ReadAsStringAsync());

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        return data.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt64())
            .ToList();
    }

    // ---------------------------------------------------------------- every spelling of Type-C

    [Theory]
    [InlineData("c type")]
    [InlineData("type c")]
    [InlineData("type-c")]
    [InlineData("typec")]
    [InlineData("Type C")]
    [InlineData("TYPE-C")]
    [InlineData("  type   c  ")]
    public async Task Every_way_of_typing_type_c_finds_the_cable(string typed)
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        // SC-022, FR-077: order, case, spaces and hyphens never stop a match.
        var ids = await IdsAsync(admin, $"{typed} {catalogue.Tag}");

        ids.Should().Contain(catalogue.TypeC);
    }

    // ---------------------------------------------------------------- words across fields

    [Fact]
    public async Task A_brand_name_finds_its_products()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        var ids = await IdsAsync(admin, $"oppo {catalogue.Tag}");

        ids.Should().Contain(catalogue.OppoCharger);
        ids.Should().NotContain(catalogue.SamsungCharger);
    }

    [Theory]
    [InlineData("charger")]
    [InlineData("chargers")]
    [InlineData("CHARGER")]
    public async Task A_category_word_finds_its_products_singular_or_plural(string typed)
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        // FR-078: "charger" finds category "Chargers", and "chargers" finds name "Charger 18W".
        var ids = await IdsAsync(admin, $"{typed} {catalogue.Tag}");

        ids.Should().Contain([catalogue.OppoCharger, catalogue.SamsungCharger]);
        ids.Should().NotContain(catalogue.TypeC);
    }

    [Theory]
    [InlineData("oppo charger")]
    [InlineData("charger oppo")]
    public async Task Words_matching_different_details_narrow_to_one_product(string typed)
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        // SC-023, FR-075, FR-076: "oppo" is the brand and "charger" the category — two different
        // details of one product, in either order — and the Samsung charger must not appear.
        var ids = await IdsAsync(admin, $"{typed} {catalogue.Tag}");

        ids.Should().Contain(catalogue.OppoCharger);
        ids.Should().NotContain(catalogue.SamsungCharger);
    }

    [Fact]
    public async Task A_brand_and_a_name_word_find_the_cable()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        (await IdsAsync(admin, $"baseus cable {catalogue.Tag}")).Should().Contain(catalogue.TypeC);
        (await IdsAsync(admin, $"cable baseus {catalogue.Tag}")).Should().Contain(catalogue.TypeC);
    }

    [Theory]
    [InlineData("20w")]
    [InlineData("20 w")]
    [InlineData("20-W")]
    public async Task Numbers_and_letters_match_however_they_are_spaced(string typed)
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        (await IdsAsync(admin, $"{typed} {catalogue.Tag}")).Should().Contain(catalogue.OppoCharger);
    }

    // ---------------------------------------------------------------- invariants

    [Fact]
    public async Task Adding_a_word_never_widens_the_results()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        var broad = await IdsAsync(admin, $"charger {catalogue.Tag}");
        var narrow = await IdsAsync(admin, $"charger samsung {catalogue.Tag}");

        // Data-model §6 invariant 1: every word must match, so more words means fewer results.
        narrow.Should().BeSubsetOf(broad);
        narrow.Should().Contain(catalogue.SamsungCharger);
        narrow.Should().NotContain(catalogue.OppoCharger);
    }

    [Fact]
    public async Task A_word_that_matches_nothing_returns_an_empty_list_not_an_error()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        // Every word must match; the unmatched one is not silently dropped.
        var ids = await IdsAsync(admin, $"charger xyzzyplugh {catalogue.Tag}");

        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task A_generic_product_is_still_found_by_name_and_category()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        // Its brand ("Local") is not the detail that matches here; name and category still are.
        (await IdsAsync(admin, $"micro usb {catalogue.Tag}")).Should().Contain(catalogue.GenericCable);
        (await IdsAsync(admin, $"cable {catalogue.Tag}")).Should().Contain(catalogue.GenericCable);
    }

    [Fact]
    public async Task A_full_barcode_still_finds_that_exact_product()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var catalogue = await SeedAsync();

        // FR-080: a scanner types the whole barcode; it must keep working exactly as before.
        var ids = await IdsAsync(admin, $"TC{catalogue.Tag}");

        ids.Should().Contain(catalogue.TypeC);
    }

    // ---------------------------------------------------------------- one-letter searches

    [Theory]
    [InlineData("c")]
    [InlineData("c t")]
    public async Task A_search_of_only_single_letters_is_refused(string typed)
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync($"/api/products?search={Uri.EscapeDataString(typed)}");

        // FR-079. Enforced on the server, not only as a screen hint.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("VALIDATION_FAILED");
        body.Should().Contain("Type at least 2 letters to search.");
    }

    [Fact]
    public async Task A_single_letter_beside_a_longer_word_is_allowed()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync("/api/products?search=c%20type");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------- both users

    [Fact]
    public async Task A_salesman_finds_the_same_products_and_sees_no_cost()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var staff = await ClientAsync(UserRole.Staff);
        var catalogue = await SeedAsync();

        var search = $"charger {catalogue.Tag}";

        // FR-089: search changes which products are listed, never which details a role may see.
        (await IdsAsync(staff, search)).Should().BeEquivalentTo(await IdsAsync(admin, search));

        var raw = await (await staff.GetAsync(
            $"/api/products?search={Uri.EscapeDataString(search)}")).Content.ReadAsStringAsync();

        raw.Should().NotContain("costPrice");
    }
}
