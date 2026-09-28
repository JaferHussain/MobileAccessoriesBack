using Dapper;
using FluentAssertions;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Infrastructure.Data;
using MoizPos.Infrastructure.Repositories;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Purchases;

/// <summary>
/// T063 / T064 — the shop owner's costing rule, and the six-part purchase transaction.
///
/// THE test in this suite. If it fails, every profit figure the owner sees is wrong.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PurchaseCostRuleTests
{
    private readonly ApiFactory _api;

    public PurchaseCostRuleTests(ApiFactory api) => _api = api;

    private PurchaseService Service() =>
        new(
            new UnitOfWorkFactory(new MySqlConnectionFactory(_api.ConnectionString)),
            new PurchaseWriteRepository(),
            new StockMovementWriter(),
            new AuditWriter(),
            new SystemClock());

    private async Task<long> CreateSupplierAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO suppliers (name, payable_balance, is_active, created_at_utc)
            VALUES (@name, 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Supplier {Guid.NewGuid():N}"[..20] });
    }

    private async Task<long> CreateProductAsync(decimal costPrice = 0m, int quantity = 0, decimal salePrice = 0m)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            -- Products carry a category foreign key now, so the category has to exist first.
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES
                (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), @costPrice, 0, @salePrice, @quantity, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Cable {Guid.NewGuid():N}"[..20], costPrice, quantity, salePrice });
    }

    private async Task<(int Quantity, decimal CostPrice, decimal RetailPrice)> ReadProductAsync(long id)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        // Three prices, since 0029: cost, wholesale and retail. retail_price is what a walk-in
        // is quoted — there is no longer a separate sale_price column.
        return await connection.QuerySingleAsync<(int, decimal, decimal)>(
            "SELECT quantity_on_hand, cost_price, retail_price FROM products WHERE id = @id;",
            new { id });
    }

    private async Task<decimal> ReadPayableAsync(long supplierId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT payable_balance FROM suppliers WHERE id = @supplierId;", new { supplierId });
    }

    private async Task SellAsync(long productId, int quantity)
    {
        // Phase 4 owns real selling; this just reduces stock so the worked example can be set up.
        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            "UPDATE products SET quantity_on_hand = quantity_on_hand - @quantity WHERE id = @productId;",
            new { productId, quantity });
    }

    // ================================================================
    //  The owner's worked example, verbatim
    // ================================================================

    [Fact]
    public async Task Latest_purchase_cost_replaces_the_cost_of_all_stock_on_hand()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();
        var service = Service();

        // "10 pieces purchased at 800 per piece"
        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            {
                SupplierId = supplierId,
                ProductId = productId,
                UnitCost = 800m,
                Quantity = 10,
                NewRetailPrice = 1100m,
            },
            userId);

        // "we sold 5 pieces"
        await SellAsync(productId, 5);

        // "next month prices increase and we purchase 10 more at 850 per single item"
        var result = await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            {
                SupplierId = supplierId,
                ProductId = productId,
                UnitCost = 850m,
                Quantity = 10,
                NewRetailPrice = 1100m,
            },
            userId);

        var product = await ReadProductAsync(productId);

        // "the price will update for all items"
        product.Quantity.Should().Be(15);
        product.CostPrice.Should().Be(850m);
        result.NewCostPrice.Should().Be(850m);

        // Emphatically NOT a weighted average of 825.
        product.CostPrice.Should().NotBe(825m,
            "the shop uses latest purchase cost, not weighted average");
    }

    [Fact]
    public async Task Repricing_on_a_cost_rise_applies_to_stock_bought_earlier_and_cheaper()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();
        var service = Service();

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            {
                SupplierId = supplierId, ProductId = productId,
                UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m,
            },
            userId);

        await SellAsync(productId, 5);

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            {
                SupplierId = supplierId, ProductId = productId,
                UnitCost = 850m, Quantity = 10, NewRetailPrice = 1200m,
            },
            userId);

        var product = await ReadProductAsync(productId);

        // All 15 units — including the 5 bought at 800 — now sell at 1,200 (FR-011d).
        product.RetailPrice.Should().Be(1200m);
        product.Quantity.Should().Be(15);
    }

    [Fact]
    public async Task A_cheaper_purchase_lowers_the_cost_of_all_stock_on_hand()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();
        var service = Service();

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 850m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 780m, Quantity = 5, NewRetailPrice = 1100m },
            userId);

        (await ReadProductAsync(productId)).CostPrice.Should().Be(780m);
    }

    // ================================================================
    //  The six-part transaction (data-model.md §11)
    // ================================================================

    [Fact]
    public async Task A_purchase_raises_stock_and_the_supplier_payable_together()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync(quantity: 5);
        var service = Service();

        var result = await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = 50, NewRetailPrice = 1100m },
            userId);

        // spec US3 scenario 1: 5 + 50 = 55 in stock, payable up by 40,000.
        (await ReadProductAsync(productId)).Quantity.Should().Be(55);
        (await ReadPayableAsync(supplierId)).Should().Be(40_000m);
        result.NewSupplierPayable.Should().Be(40_000m);
    }

    [Fact]
    public async Task A_purchase_writes_a_stock_movement_matching_the_new_quantity()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync(quantity: 5);

        await Service().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = 50, NewRetailPrice = 1100m },
            userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var movement = await connection.QuerySingleAsync<(int ChangeQty, int ResultingQty, string Reason)>(
            """
            SELECT change_qty, resulting_qty, reason
            FROM stock_movements WHERE product_id = @productId ORDER BY id DESC LIMIT 1;
            """,
            new { productId });

        movement.ChangeQty.Should().Be(50);
        movement.ResultingQty.Should().Be(55);
        movement.Reason.Should().Be("Purchase");
    }

    [Fact]
    public async Task A_purchase_audits_the_quantity_and_cost_changes()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();

        await Service().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var fields = (await connection.QueryAsync<string>(
            """
            SELECT field_name FROM audit_entries
            WHERE entity_type = 'Product' AND entity_id = @productId;
            """,
            new { productId })).ToList();

        fields.Should().Contain("quantity_on_hand").And.Contain("cost_price");
    }

    [Fact]
    public async Task A_purchase_for_an_unknown_product_writes_nothing()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();

        var act = async () => await Service().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = 999_999_999, UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await act.Should().ThrowAsync<Exception>();

        // The payable must be untouched: no partial state (FR-050).
        (await ReadPayableAsync(supplierId)).Should().Be(0m);
    }

    [Fact]
    public async Task A_purchase_for_an_unknown_supplier_writes_nothing()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 5);

        var act = async () => await Service().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = 999_999_999, ProductId = productId, UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await act.Should().ThrowAsync<Exception>();

        var product = await ReadProductAsync(productId);
        product.Quantity.Should().Be(5, "stock must not move when the purchase failed");
        product.CostPrice.Should().Be(0m, "the cost must not move when the purchase failed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Rejects_a_non_positive_quantity(int quantity)
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();

        var act = async () => await Service().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = quantity, NewRetailPrice = 1100m },
            userId);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Rejects_a_zero_unit_cost()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();

        var act = async () => await Service().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 0m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await act.Should().ThrowAsync<Exception>();
    }

    // ================================================================
    //  Supplier payments (FR-009)
    // ================================================================

    [Fact]
    public async Task A_payment_reduces_the_payable()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();
        var service = Service();

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        var newPayable = await service.RecordSupplierPaymentAsync(
            new RecordSupplierPaymentRequest { SupplierId = supplierId, Amount = 3_000m },
            userId);

        newPayable.Should().Be(5_000m);
        (await ReadPayableAsync(supplierId)).Should().Be(5_000m);
    }

    [Fact]
    public async Task Paying_more_than_is_owed_needs_explicit_confirmation()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();
        var service = Service();

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 100m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        var act = async () => await service.RecordSupplierPaymentAsync(
            new RecordSupplierPaymentRequest { SupplierId = supplierId, Amount = 5_000m },
            userId);

        await act.Should().ThrowAsync<MoizPos.Domain.Errors.OverpaymentNotConfirmedException>();

        (await ReadPayableAsync(supplierId)).Should().Be(1_000m, "the rejected payment changed nothing");
    }

    [Fact]
    public async Task A_confirmed_overpayment_is_recorded_and_flagged()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();
        var service = Service();

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 100m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        var newPayable = await service.RecordSupplierPaymentAsync(
            new RecordSupplierPaymentRequest
            { SupplierId = supplierId, Amount = 1_500m, ConfirmOverpayment = true },
            userId);

        newPayable.Should().Be(-500m);

        await using var connection = await _api.OpenDatabaseAsync();

        var isOverpayment = await connection.ExecuteScalarAsync<bool>(
            "SELECT is_overpayment FROM supplier_payments WHERE supplier_id = @supplierId ORDER BY id DESC LIMIT 1;",
            new { supplierId });

        isOverpayment.Should().BeTrue();
    }

    [Fact]
    public async Task Supplier_payable_equals_purchases_minus_payments()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();
        var service = Service();

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await service.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 850m, Quantity = 4, NewRetailPrice = 1100m },
            userId);

        await service.RecordSupplierPaymentAsync(
            new RecordSupplierPaymentRequest { SupplierId = supplierId, Amount = 5_000m }, userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var expected = await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE((SELECT SUM(total) FROM purchases WHERE supplier_id = @supplierId), 0)
                 - COALESCE((SELECT SUM(amount) FROM supplier_payments WHERE supplier_id = @supplierId), 0);
            """,
            new { supplierId });

        // data-model.md invariant 3.
        (await ReadPayableAsync(supplierId)).Should().Be(expected);
    }
}
