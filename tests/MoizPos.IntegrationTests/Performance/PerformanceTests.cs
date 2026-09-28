using System.Diagnostics;
using Dapper;
using FluentAssertions;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Infrastructure.Data;
using MoizPos.Infrastructure.Repositories;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Performance;

/// <summary>
/// T174 / T175 — the timing promises in the spec, measured rather than assumed.
///
/// SC-002: a search returns in under 2 seconds with a catalogue of at least 5,000 items.
/// SC-008: the dashboard is readable within 10 seconds.
///
/// These run against real MySQL on developer hardware, so the thresholds are the spec's, not
/// tighter — the point is to catch a query that has become accidentally quadratic, not to
/// benchmark the machine.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PerformanceTests
{
    private const int CatalogueSize = 5_000;

    private readonly ApiFactory _api;

    public PerformanceTests(ApiFactory api) => _api = api;

    private MySqlConnectionFactory Factory() => new(_api.ConnectionString);

    /// <summary>Seeds the catalogue up to <see cref="CatalogueSize"/> once for the whole class.</summary>
    private async Task EnsureCatalogueAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var existing = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM products WHERE is_active = TRUE;");

        var missing = CatalogueSize - existing;

        if (missing <= 0)
        {
            return;
        }

        var brands = new[] { "Baseus", "Anker", "Remax", "Joyroom", "Ugreen" };
        var categories = new[] { "Cables", "Chargers", "Earbuds", "Covers", "Power Banks" };

        // Both are foreign keys now, so the rows they point at have to exist before the products.
        var categoryIds = new List<long>();
        foreach (var category in categories)
        {
            categoryIds.Add(await _api.EnsureCategoryAsync(category));
        }

        var brandIds = new List<long>();
        foreach (var brand in brands)
        {
            brandIds.Add(await _api.EnsureBrandAsync(brand));
        }

        var rows = Enumerable.Range(0, missing).Select(i => new
        {
            name = Truncate($"Perf {i} {Guid.NewGuid():N}", 40),
            categoryId = categoryIds[i % categoryIds.Count],
            brandId = brandIds[i % brandIds.Count],
            model = $"M-{i}",
            barcode = Truncate($"PERF{i}{Guid.NewGuid():N}", 20),
            costPrice = 500m + (i % 400),
            salePrice = 900m + (i % 600),
            quantity = i % 50,
            threshold = 3,
        });

        // One round trip per batch rather than per row; seeding is not what we are measuring.
        await connection.ExecuteAsync(
            """
            INSERT INTO products
                (name, category_id, brand_id, model, barcode, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES
                (@name, @categoryId, @brandId, @model, @barcode, @costPrice, 0, @salePrice, @quantity, @threshold, TRUE, UTC_TIMESTAMP(6));
            """,
            rows);
    }

    /// <summary>Trims to at most <paramref name="length"/>, tolerating shorter input.</summary>
    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private static async Task<TimeSpan> MeasureAsync(Func<Task> action)
    {
        // Warm the query plan and connection pool first; the first call of anything pays for
        // both, and that is not what the spec is promising.
        await action();

        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();

        return stopwatch.Elapsed;
    }

    [Fact]
    public async Task Searching_a_five_thousand_item_catalogue_returns_within_two_seconds()
    {
        await EnsureCatalogueAsync();

        var products = new ProductService(
            new ProductRepository(Factory()),
            new CategoryRepository(Factory()),
            new BrandRepository(Factory()));

        var elapsed = await MeasureAsync(async () =>
            await products.SearchAsync(
                new ProductQuery { Search = "Baseus", Page = 1, PageSize = 25 },
                UserRole.Staff));

        // SC-002.
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_multi_word_search_across_five_thousand_products_is_under_a_second()
    {
        await EnsureCatalogueAsync();

        var products = new ProductService(
            new ProductRepository(Factory()),
            new CategoryRepository(Factory()),
            new BrandRepository(Factory()));

        // SC-025. The heaviest ordinary shape: three words, each normalised against four fields,
        // every row scanned. If this fails, apply the stored-column fallback documented in
        // specs/003-product-filters-search/plan.md — do not loosen the bound.
        var elapsed = await MeasureAsync(async () =>
            await products.SearchAsync(
                new ProductQuery { Search = "baseus cable m", Page = 1, PageSize = 25 },
                UserRole.Staff));

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_barcode_lookup_at_the_counter_is_immediate()
    {
        await EnsureCatalogueAsync();

        await using var connection = await _api.OpenDatabaseAsync();

        var barcode = await connection.ExecuteScalarAsync<string>(
            "SELECT barcode FROM products WHERE barcode IS NOT NULL ORDER BY id DESC LIMIT 1;");

        var products = new ProductService(
            new ProductRepository(Factory()),
            new CategoryRepository(Factory()),
            new BrandRepository(Factory()));

        var elapsed = await MeasureAsync(async () =>
            await products.FindByBarcodeAsync(barcode!, UserRole.Staff));

        // A scanner fires several times a minute; anything slower is felt at the counter.
        elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task The_low_stock_list_is_fast_across_the_whole_catalogue()
    {
        await EnsureCatalogueAsync();

        var products = new ProductService(
            new ProductRepository(Factory()),
            new CategoryRepository(Factory()),
            new BrandRepository(Factory()));

        var elapsed = await MeasureAsync(async () =>
            await products.SearchAsync(
                new ProductQuery { LowStockOnly = true, Page = 1, PageSize = 100 },
                UserRole.Admin));

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task The_dashboard_is_ready_within_ten_seconds()
    {
        await EnsureCatalogueAsync();

        var reporting = new ReportingService(
            new ReportRepository(Factory()),
            new ExpenseRepository(Factory()),
            new PeriodResolver(),
            new SystemClock());

        foreach (var period in new[]
                 {
                     DashboardPeriod.Today, DashboardPeriod.ThisMonth, DashboardPeriod.ThisYear,
                 })
        {
            var elapsed = await MeasureAsync(async () => await reporting.DashboardAsync(period));

            // SC-008.
            elapsed.Should().BeLessThan(
                TimeSpan.FromSeconds(10), $"the {period} dashboard must open promptly");
        }
    }

    [Fact]
    public async Task Product_wise_profit_over_a_year_stays_responsive()
    {
        await EnsureCatalogueAsync();

        var reporting = new ReportingService(
            new ReportRepository(Factory()),
            new ExpenseRepository(Factory()),
            new PeriodResolver(),
            new SystemClock());

        var elapsed = await MeasureAsync(async () =>
            await reporting.ProfitByProductAsync(
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 500));

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}
