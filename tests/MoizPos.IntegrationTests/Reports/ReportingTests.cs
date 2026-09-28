using Dapper;
using FluentAssertions;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Infrastructure.Data;
using MoizPos.Infrastructure.Repositories;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Reports;

/// <summary>
/// T131 / T133 — the profit engine and dashboard.
///
/// Every figure is checked against hand-calculated values, because SC-005 requires reports to
/// reconcile with zero discrepancy. The tests use a fixed clock so period boundaries are
/// deterministic rather than depending on when the suite runs.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReportingTests
{
    private readonly ApiFactory _api;

    public ReportingTests(ApiFactory api) => _api = api;

    /// <summary>13:30 on 9 September 2026 Karachi time, expressed as UTC.</summary>
    private static readonly DateTime NowUtc = new(2026, 9, 9, 8, 30, 0, DateTimeKind.Utc);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; set; } = NowUtc;
    }

    private MySqlConnectionFactory Factory() => new(_api.ConnectionString);

    private (ReportingService Service, FixedClock Clock) Reporting()
    {
        var clock = new FixedClock();

        var service = new ReportingService(
            new ReportRepository(Factory()),
            new ExpenseRepository(Factory()),
            new PeriodResolver(),
            clock);

        return (service, clock);
    }

    private InvoiceService Sales(IClock clock) =>
        new(
            new UnitOfWorkFactory(Factory()),
            new InvoiceWriteRepository(),
            new CustomerRepository(Factory()),
            new StockMovementWriter(),
            new AuditWriter(),
            clock);

    private ReturnService Returns(IClock clock) =>
        new(
            new UnitOfWorkFactory(Factory()),
            new ReturnWriteRepository(),
            new InvoiceWriteRepository(),
            new PurchaseWriteRepository(),
            new StockWriteRepository(),
            new StockMovementWriter(),
            new AuditWriter(),
            clock);

    private async Task<long> CreateProductAsync(int quantity, decimal cost, decimal salePrice)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            -- Products carry a category foreign key now, so the category has to exist first.
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), @cost, 0, @salePrice, @quantity, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Rpt {Guid.NewGuid():N}"[..20], cost, salePrice, quantity });
    }

    /// <summary>
    /// Clears transactional data so a report's totals are attributable to this test.
    ///
    /// Derived running balances are recomputed afterwards. Deleting source rows while leaving
    /// suppliers.payable_balance and customers.outstanding_balance as they were would put the
    /// database into a state production can never reach, and the invariant tests would
    /// (correctly) fail on it.
    /// </summary>
    private async Task ResetTransactionsAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        foreach (var statement in new[]
        {
            "DELETE FROM sale_return_items;",
            "DELETE FROM sale_returns;",
            "DELETE FROM purchase_returns;",
            "DELETE FROM ledger_entries;",
            "DELETE FROM customer_payments;",
            "DELETE FROM invoice_items;",
            "DELETE FROM invoices;",
            "DELETE FROM expenses;",

            // Rebuild the derived balances from what actually remains. Purchases and supplier
            // payments both survive the reset; only purchase_returns were deleted.
            """
            UPDATE suppliers s
            SET s.payable_balance =
                COALESCE((SELECT SUM(total) FROM purchases WHERE supplier_id = s.id), 0)
              - COALESCE((SELECT SUM(amount) FROM supplier_payments WHERE supplier_id = s.id), 0);
            """,

            "UPDATE customers SET outstanding_balance = 0;",
        })
        {
            await connection.ExecuteAsync(statement);
        }
    }

    private async Task<CreateInvoiceResult> SellAsync(
        IClock clock, long productId, int quantity, decimal price, long userId) =>
        await Sales(clock).CreateAsync(
            new CreateInvoiceRequest
            {
                AmountPaid = price * quantity,
                PaymentMethod = PaymentMethod.Cash,
                Items = [new CreateInvoiceLine { ProductId = productId, Quantity = quantity, UnitSalePrice = price }],
            }, userId, UserRole.Admin);

    private async Task AddExpenseAsync(decimal amount, DateTime whenUtc, long userId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var categoryId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM expense_categories WHERE name = 'Electricity' LIMIT 1;");

        await connection.ExecuteAsync(
            """
            INSERT INTO expenses (category_id, amount, expense_date_utc, note, user_id, created_at_utc)
            VALUES (@categoryId, @amount, @whenUtc, 'Test', @userId, UTC_TIMESTAMP(6));
            """,
            new { categoryId, amount, whenUtc, userId });
    }

    // ================================================================
    //  Gross profit — spec US4 scenario 1
    // ================================================================

    [Fact]
    public async Task Gross_profit_is_sale_less_the_cost_recorded_on_the_line()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, cost: 800m, salePrice: 1100m);

        await SellAsync(clock, productId, 1, 1100m, userId);

        var dashboard = await reporting.DashboardAsync(DashboardPeriod.Today);

        // 1,100 - 800 = 300.
        dashboard.GrossProfit.Should().Be(300m);
    }

    [Fact]
    public async Task Gross_profit_scales_with_quantity()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(20, 800m, 1100m);

        await SellAsync(clock, productId, 4, 1100m, userId);

        (await reporting.DashboardAsync(DashboardPeriod.Today)).GrossProfit.Should().Be(1200m);
    }

    [Fact]
    public async Task Profit_uses_the_cost_at_sale_time_not_the_products_current_cost()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        await SellAsync(clock, productId, 1, 1100m, userId);

        // The latest-cost rule moves the product on; the recorded sale must not follow it.
        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE products SET cost_price = 850 WHERE id = @productId;", new { productId });
        }

        (await reporting.DashboardAsync(DashboardPeriod.Today)).GrossProfit.Should().Be(300m);
    }

    [Fact]
    public async Task Returned_units_contribute_no_profit()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        var sale = await SellAsync(clock, productId, 3, 1100m, userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var itemId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM invoice_items WHERE invoice_id = @id LIMIT 1;", new { id = sale.InvoiceId });

        await Returns(clock).RecordSaleReturnAsync(
            new RecordSaleReturnRequest
            { InvoiceId = sale.InvoiceId, Items = [new SaleReturnLine { InvoiceItemId = itemId, Quantity = 1 }] },
            userId);

        // FR-027: only 2 units remain sold, so 600 not 900.
        (await reporting.DashboardAsync(DashboardPeriod.Today)).GrossProfit.Should().Be(600m);
    }

    // ================================================================
    //  Net profit — spec US4 scenario 2
    // ================================================================

    [Fact]
    public async Task Net_profit_is_gross_profit_less_expenses_in_the_same_period()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(1000, 800m, 1300m);

        // 100 units x 500 profit = 50,000 gross.
        await SellAsync(clock, productId, 100, 1300m, userId);
        await AddExpenseAsync(12_000m, NowUtc, userId);

        var dashboard = await reporting.DashboardAsync(DashboardPeriod.Today);

        dashboard.GrossProfit.Should().Be(50_000m);
        dashboard.TotalExpenses.Should().Be(12_000m);
        dashboard.NetProfit.Should().Be(38_000m);
    }

    [Fact]
    public async Task Net_profit_can_be_a_loss()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        await SellAsync(clock, productId, 1, 1100m, userId);
        await AddExpenseAsync(5_000m, NowUtc, userId);

        (await reporting.DashboardAsync(DashboardPeriod.Today)).NetProfit.Should().Be(-4_700m);
    }

    [Fact]
    public async Task An_expense_outside_the_period_is_excluded()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        await SellAsync(clock, productId, 1, 1100m, userId);
        await AddExpenseAsync(5_000m, NowUtc.AddDays(-10), userId);

        var dashboard = await reporting.DashboardAsync(DashboardPeriod.Today);

        dashboard.TotalExpenses.Should().Be(0m);
        dashboard.NetProfit.Should().Be(300m);
    }

    // ================================================================
    //  Period boundaries — FR-034
    // ================================================================

    [Fact]
    public async Task A_sale_just_before_local_midnight_counts_today()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        // 23:59 Karachi on 9 Sep == 18:59 UTC.
        clock.UtcNow = new DateTime(2026, 9, 9, 18, 59, 0, DateTimeKind.Utc);
        await SellAsync(clock, productId, 1, 1100m, userId);

        clock.UtcNow = NowUtc;
        (await reporting.DashboardAsync(DashboardPeriod.Today)).GrossProfit.Should().Be(300m);
    }

    [Fact]
    public async Task A_sale_just_after_local_midnight_counts_tomorrow_not_today()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        // 00:01 Karachi on 10 Sep == 19:01 UTC on 9 Sep — still "today" in UTC, but not locally.
        clock.UtcNow = new DateTime(2026, 9, 9, 19, 1, 0, DateTimeKind.Utc);
        await SellAsync(clock, productId, 1, 1100m, userId);

        clock.UtcNow = NowUtc;
        (await reporting.DashboardAsync(DashboardPeriod.Today)).GrossProfit.Should().Be(0m);
    }

    [Fact]
    public async Task A_sale_lands_in_exactly_one_of_today_and_tomorrow()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        clock.UtcNow = new DateTime(2026, 9, 9, 19, 1, 0, DateTimeKind.Utc);
        await SellAsync(clock, productId, 1, 1100m, userId);

        // Read each day from a clock inside that day: at 19:01 UTC the shop is already on the
        // 10th, so asking for "Today" then would return the 10th twice.
        clock.UtcNow = NowUtc;
        var today = await reporting.DashboardAsync(DashboardPeriod.Today);

        clock.UtcNow = NowUtc.AddDays(1);
        var tomorrow = await reporting.DashboardAsync(DashboardPeriod.Today);

        (today.GrossProfit + tomorrow.GrossProfit).Should().Be(300m);
        today.GrossProfit.Should().Be(0m);
        tomorrow.GrossProfit.Should().Be(300m);
    }

    [Fact]
    public async Task The_month_view_includes_a_sale_earlier_in_the_month()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        clock.UtcNow = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
        await SellAsync(clock, productId, 1, 1100m, userId);

        clock.UtcNow = NowUtc;

        (await reporting.DashboardAsync(DashboardPeriod.Today)).GrossProfit.Should().Be(0m);
        (await reporting.DashboardAsync(DashboardPeriod.ThisMonth)).GrossProfit.Should().Be(300m);
        (await reporting.DashboardAsync(DashboardPeriod.ThisYear)).GrossProfit.Should().Be(300m);
    }

    // ================================================================
    //  Dashboard KPIs — FR-035
    // ================================================================

    [Fact]
    public async Task The_dashboard_reports_cash_and_credit_separately()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(100, 800m, 1000m);

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            var customerId = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO customers (name, outstanding_balance, is_active, created_at_utc)
                VALUES (@name, 0, TRUE, UTC_TIMESTAMP(6));
                SELECT LAST_INSERT_ID();
                """,
                new { name = $"C {Guid.NewGuid():N}"[..18] });

            await Sales(clock).CreateAsync(
                new CreateInvoiceRequest
                {
                    CustomerId = customerId,
                    AmountPaid = 400m,
                    PaymentMethod = PaymentMethod.Partial,
                    Items = [new CreateInvoiceLine { ProductId = productId, Quantity = 1, UnitSalePrice = 1000m }],
                }, userId, UserRole.Admin);
        }

        var dashboard = await reporting.DashboardAsync(DashboardPeriod.Today);

        // A partial payment is genuinely both, so it contributes to each half.
        dashboard.CashSales.Should().Be(400m);
        dashboard.CreditSales.Should().Be(600m);
    }

    [Fact]
    public async Task The_dashboard_counts_items_sold()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(100, 800m, 1100m);

        await SellAsync(clock, productId, 7, 1100m, userId);

        (await reporting.DashboardAsync(DashboardPeriod.Today)).ItemsSoldCount.Should().Be(7);
    }

    [Fact]
    public async Task The_dashboard_lists_products_needing_reorder()
    {
        var (reporting, _) = Reporting();
        await CreateProductAsync(quantity: 1, cost: 800m, salePrice: 1100m);

        var dashboard = await reporting.DashboardAsync(DashboardPeriod.Today);

        dashboard.LowStockProducts.Should().NotBeEmpty();
        dashboard.LowStockProducts.Should().OnlyContain(p => p.IsLowStock);
    }

    [Fact]
    public async Task The_dashboard_reports_total_receivables_and_payables()
    {
        var (reporting, _) = Reporting();

        var dashboard = await reporting.DashboardAsync(DashboardPeriod.Today);

        dashboard.TotalReceivables.Should().BeGreaterThanOrEqualTo(0m);
        dashboard.TotalPayables.Should().BeGreaterThanOrEqualTo(0m);
    }

    // ================================================================
    //  Reports
    // ================================================================

    [Fact]
    public async Task Product_wise_profit_reconciles_with_its_lines()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(100, 800m, 1100m);

        await SellAsync(clock, productId, 5, 1100m, userId);

        var rows = await reporting.ProfitByProductAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 100);

        var row = rows.Single(r => r.ProductId == productId);

        row.QuantitySold.Should().Be(5);
        row.TotalSale.Should().Be(5500m);
        row.TotalCost.Should().Be(4000m);
        row.TotalProfit.Should().Be(1500m);
        (row.TotalSale - row.TotalCost).Should().Be(row.TotalProfit);
    }

    [Fact]
    public async Task The_profit_report_subtracts_expenses_per_bucket()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(100, 800m, 1100m);

        await SellAsync(clock, productId, 10, 1100m, userId);   // 3,000 gross
        await AddExpenseAsync(1_000m, NowUtc, userId);

        var rows = await reporting.ProfitAsync(
            new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 9), ReportGrouping.Day);

        var day = rows.Single();
        day.GrossProfit.Should().Be(3_000m);
        day.Expenses.Should().Be(1_000m);
        day.NetProfit.Should().Be(2_000m);
    }

    [Fact]
    public async Task The_sales_report_buckets_by_local_day()
    {
        await ResetTransactionsAsync();
        var (reporting, clock) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(100, 800m, 1100m);

        clock.UtcNow = new DateTime(2026, 9, 8, 8, 0, 0, DateTimeKind.Utc);
        await SellAsync(clock, productId, 1, 1100m, userId);

        clock.UtcNow = NowUtc;
        await SellAsync(clock, productId, 2, 1100m, userId);

        var rows = await reporting.SalesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), ReportGrouping.Day);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Period).Should().Equal("2026-09-08", "2026-09-09");
    }

    [Fact]
    public async Task The_expense_report_breaks_down_by_category()
    {
        await ResetTransactionsAsync();
        var (reporting, _) = Reporting();
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);

        await AddExpenseAsync(12_000m, NowUtc, userId);

        var report = await reporting.ExpensesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        report.Total.Should().Be(12_000m);
        report.ByCategory.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ExpenseCategoryTotal("Electricity", 12_000m));
    }

    [Fact]
    public async Task The_stock_report_values_stock_at_current_cost()
    {
        var (reporting, _) = Reporting();
        var productId = await CreateProductAsync(quantity: 10, cost: 800m, salePrice: 1100m);

        var rows = await reporting.StockAsync(lowStockOnly: false);
        var row = rows.Single(r => r.ProductId == productId);

        row.StockValue.Should().Be(8000m);
    }

    [Fact]
    public async Task A_reversed_date_range_is_refused()
    {
        var (reporting, _) = Reporting();

        var act = async () => await reporting.SalesAsync(
            new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 1), ReportGrouping.Day);

        await act.Should().ThrowAsync<MoizPos.Domain.Errors.BusinessRuleViolationException>();
    }

    [Fact]
    public async Task Stock_movements_can_be_filtered_by_reason()
    {
        var (reporting, _) = Reporting();

        var page = await reporting.StockMovementsAsync(null, null, "Purchase", 1, 50);

        page.Items.Should().OnlyContain(m => m.Reason == "Purchase");
    }
}
