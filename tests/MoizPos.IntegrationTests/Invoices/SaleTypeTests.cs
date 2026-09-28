using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Invoices;

/// <summary>
/// Retail and wholesale sales.
///
/// The shop sells both over the counter and in bulk to other shopkeepers, and the owner reads the
/// day's takings split between the two. What matters is that the split is recorded rather than
/// guessed from the price: a discounted retail sale and a wholesale sale can reach the same
/// figure, so only the label on the invoice can tell them apart afterwards.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SaleTypeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public SaleTypeTests(ApiFactory api) => _api = api;

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

    /// <summary>A product with distinct counter and wholesale prices, so the two cannot be confused.</summary>
    private async Task<long> CreateProductAsync(
        decimal salePrice = 1100m, decimal wholesalePrice = 950m, int quantity = 100)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 800, @wholesalePrice, @salePrice, @quantity, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"ST {Guid.NewGuid():N}"[..20], salePrice, wholesalePrice, quantity });
    }

    private static object Sale(long productId, string saleType, decimal unitPrice, int quantity = 1) =>
        new
        {
            saleType,
            amountPaid = unitPrice * quantity,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity, unitSalePrice = unitPrice, lineDiscount = 0m } },
        };

    private static DateOnly Today => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "PKT", "PKT")));

    // ---------------------------------------------------------------- recording

    [Fact]
    public async Task A_sale_is_retail_unless_it_says_otherwise()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync();

        // No saleType in the payload at all — the counter is the normal case, and an unset field
        // must not silently reclassify the day's takings.
        var response = await admin.PostAsJsonAsync("/api/invoices", new
        {
            amountPaid = 1100m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1100m, lineDiscount = 0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var invoiceId = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();

        await using var connection = await _api.OpenDatabaseAsync();

        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT sale_type FROM invoices WHERE id = @invoiceId;", new { invoiceId });

        stored.Should().Be("Retail");
    }

    [Theory]
    [InlineData("Retail")]
    [InlineData("Wholesale")]
    public async Task The_sale_type_is_recorded_on_the_invoice(string saleType)
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync();

        var response = await admin.PostAsJsonAsync("/api/invoices", Sale(productId, saleType, 1000m));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var invoiceId = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceId").GetInt64();

        await using var connection = await _api.OpenDatabaseAsync();

        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT sale_type FROM invoices WHERE id = @invoiceId;", new { invoiceId });

        stored.Should().Be(saleType);
    }

    [Fact]
    public async Task An_unknown_sale_type_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync();

        var response = await admin.PostAsJsonAsync("/api/invoices", Sale(productId, "Smuggled", 1000m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_salesman_can_make_a_wholesale_sale()
    {
        // The owner asked for both kinds of sale at the counter, so this is not admin-only.
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync();

        var response = await staff.PostAsJsonAsync("/api/invoices", Sale(productId, "Wholesale", 950m));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---------------------------------------------------------------- pricing

    [Fact]
    public async Task A_wholesale_sale_is_quoted_the_wholesale_price()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync(salePrice: 1100m, wholesalePrice: 950m);

        var retail = await staff.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products/{productId}", Json);

        retail!.Data!.GetProperty("salePrice").GetDecimal().Should().Be(1100m);

        var wholesale = await staff.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products?saleType=Wholesale&search={retail.Data.GetProperty("name").GetString()}",
            Json);

        wholesale!.Data!.GetProperty("items")[0].GetProperty("salePrice").GetDecimal()
            .Should().Be(950m);
    }

    [Fact]
    public async Task Reading_one_product_honours_the_sale_type()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync(salePrice: 1100m, wholesalePrice: 950m);

        // This is the call the POS makes when the salesman switches sale type with items already
        // in the cart. It silently ignored saleType and re-quoted the counter price.
        var wholesale = await staff.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products/{productId}?saleType=Wholesale", Json);

        wholesale!.Data!.GetProperty("salePrice").GetDecimal().Should().Be(950m);

        var retail = await staff.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products/{productId}?saleType=Retail", Json);

        retail!.Data!.GetProperty("salePrice").GetDecimal().Should().Be(1100m);
    }

    [Fact]
    public async Task A_scanned_barcode_is_priced_for_the_sale_being_made()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var barcode = $"{Random.Shared.NextInt64(1_000_000_000_000, 8_999_999_999_999)}";
        var (categoryId, brandId) = await _api.EnsureCatalogueAsync();

        var created = await admin.PostAsJsonAsync("/api/products", new
        {
            name = $"Scan {Guid.NewGuid():N}"[..20],
            categoryId,
            brandId,
            barcode,
            minStockThreshold = 3,
        });

        var scannedId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Prices and stock arrive with the first delivery, not with the product.
        await _api.StockProductAsync(
            scannedId, quantity: 10, salePrice: 1100m, wholesalePrice: 950m);

        var scanned = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products/by-barcode/{barcode}?saleType=Wholesale", Json);

        scanned!.Data!.GetProperty("salePrice").GetDecimal().Should().Be(950m);
    }

    [Fact]
    public async Task A_product_with_no_wholesale_price_falls_back_to_the_counter_price()
    {
        var staff = await ClientAsync(UserRole.Staff);

        // Not every product has been priced for bulk yet. Quoting zero would be worse than
        // quoting the counter price.
        var productId = await CreateProductAsync(salePrice: 1100m, wholesalePrice: 0m);

        var product = await staff.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products/{productId}", Json);

        var wholesale = await staff.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/products?saleType=Wholesale&search={product!.Data!.GetProperty("name").GetString()}",
            Json);

        wholesale!.Data!.GetProperty("items")[0].GetProperty("salePrice").GetDecimal()
            .Should().Be(1100m);
    }

    [Fact]
    public async Task The_wholesale_price_never_reaches_staff_as_its_own_field()
    {
        var staff = await ClientAsync(UserRole.Staff);
        await CreateProductAsync();

        var raw = await (await staff.GetAsync("/api/products?saleType=Wholesale"))
            .Content.ReadAsStringAsync();

        // Staff are quoted the price to charge, never handed the price list to compare against
        // cost (FR-040). An architecture test enforces the same thing structurally.
        raw.Should().NotContain("wholesalePrice");
        raw.Should().NotContain("costPrice");
    }

    // ---------------------------------------------------------------- the day-end split

    [Fact]
    public async Task The_day_is_split_into_retail_and_wholesale()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 500);

        var before = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/reports/sales-by-type?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}", Json);

        var baseline = Totals(before!.Data!);

        await admin.PostAsJsonAsync("/api/invoices", Sale(productId, "Retail", 1000m, quantity: 2));
        await admin.PostAsJsonAsync("/api/invoices", Sale(productId, "Wholesale", 900m, quantity: 3));

        var after = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/reports/sales-by-type?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}", Json);

        var totals = Totals(after!.Data!);

        totals["Retail"].Should().Be(baseline["Retail"] + 2000m);
        totals["Wholesale"].Should().Be(baseline["Wholesale"] + 2700m);
    }

    [Fact]
    public async Task Both_halves_are_reported_even_when_one_is_empty()
    {
        var admin = await ClientAsync(UserRole.Admin);

        // A day with no wholesale trade must read "Wholesale 0", not omit the row and leave the
        // owner wondering whether it was recorded at all.
        var tomorrow = Today.AddDays(1);

        var response = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/reports/sales-by-type?from={tomorrow:yyyy-MM-dd}&to={tomorrow:yyyy-MM-dd}", Json);

        var totals = Totals(response!.Data!);

        totals.Should().ContainKeys("Retail", "Wholesale");
        totals["Retail"].Should().Be(0m);
        totals["Wholesale"].Should().Be(0m);
    }

    [Fact]
    public async Task Staff_cannot_read_the_day_end_split()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync(
            $"/api/reports/sales-by-type?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static Dictionary<string, decimal> Totals(JsonElement rows) =>
        rows.EnumerateArray().ToDictionary(
            row => row.GetProperty("saleType").GetString()!,
            row => row.GetProperty("totalSales").GetDecimal());

    // ---------------------------------------------------------------- the drill-down

    [Fact]
    public async Task Clicking_a_total_lists_that_half_of_the_day_only()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 500);

        var retail = await admin.PostAsJsonAsync("/api/invoices", Sale(productId, "Retail", 1000m));
        var wholesale = await admin.PostAsJsonAsync("/api/invoices", Sale(productId, "Wholesale", 900m));

        var retailNumber = await InvoiceNumberAsync(retail);
        var wholesaleNumber = await InvoiceNumberAsync(wholesale);

        var listed = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/reports/sales-list?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&saleType=Wholesale",
            Json);

        var numbers = listed!.Data!.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("invoiceNumber").GetString())
            .ToList();

        numbers.Should().Contain(wholesaleNumber);
        numbers.Should().NotContain(retailNumber);
    }

    [Fact]
    public async Task The_drill_down_says_who_sold_it_and_who_bought_it()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync();

        var customer = await admin.PostAsJsonAsync(
            "/api/customers", new { name = $"Shop {Guid.NewGuid():N}"[..18], mobileNumber = "03001234567" });

        var customerId = (await customer.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            saleType = "Wholesale",
            customerId,
            amountPaid = 900m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 900m, lineDiscount = 0m } },
        });

        var invoiceNumber = await InvoiceNumberAsync(sale);

        var listed = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/reports/sales-list?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&saleType=Wholesale",
            Json);

        var row = listed!.Data!.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("invoiceNumber").GetString() == invoiceNumber);

        // Who bought, and which salesman rang it up — the two questions the owner asks when
        // opening the day's wholesale figure.
        row.GetProperty("customerId").GetInt64().Should().Be(customerId);
        row.GetProperty("customerName").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("userName").GetString().Should().NotBeNullOrWhiteSpace();
        row.GetProperty("total").GetDecimal().Should().Be(900m);
        row.GetProperty("saleType").GetString().Should().Be("Wholesale");
    }

    [Fact]
    public async Task A_walk_in_sale_lists_with_no_customer_rather_than_being_dropped()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync();

        var sale = await admin.PostAsJsonAsync("/api/invoices", Sale(productId, "Retail", 1000m));
        var invoiceNumber = await InvoiceNumberAsync(sale);

        var listed = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/reports/sales-list?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&saleType=Retail",
            Json);

        var row = listed!.Data!.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("invoiceNumber").GetString() == invoiceNumber);

        // A cash walk-in has no customer record; the sale still has to appear in the day's list.
        row.GetProperty("customerName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Omitting_the_sale_type_lists_the_whole_day()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 500);

        var retail = await InvoiceNumberAsync(
            await admin.PostAsJsonAsync("/api/invoices", Sale(productId, "Retail", 1000m)));
        var wholesale = await InvoiceNumberAsync(
            await admin.PostAsJsonAsync("/api/invoices", Sale(productId, "Wholesale", 900m)));

        var listed = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            $"/api/reports/sales-list?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}", Json);

        var numbers = listed!.Data!.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("invoiceNumber").GetString())
            .ToList();

        numbers.Should().Contain(new[] { retail, wholesale });
    }

    [Fact]
    public async Task Staff_cannot_read_the_drill_down()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync(
            $"/api/reports/sales-list?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<string> InvoiceNumberAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("invoiceNumber").GetString()!;
    }
}
