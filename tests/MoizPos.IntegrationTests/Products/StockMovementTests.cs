using Dapper;
using FluentAssertions;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Infrastructure.Data;
using MoizPos.Infrastructure.Repositories;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Products;

/// <summary>
/// T059 — every quantity change writes exactly one append-only movement whose resulting_qty
/// matches the product's new quantity (FR-005, data-model.md invariant 1).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StockMovementTests
{
    private readonly ApiFactory _api;

    public StockMovementTests(ApiFactory api) => _api = api;

    private MySqlConnectionFactory ConnectionFactory() => new(_api.ConnectionString);

    private StockService StockService() =>
        new(
            new UnitOfWorkFactory(ConnectionFactory()),
            new StockWriteRepository(),
            new StockMovementRepository(ConnectionFactory()),
            new StockMovementWriter(),
            new AuditWriter(),
            new SystemClock());

    private PurchaseService PurchaseService() =>
        new(
            new UnitOfWorkFactory(ConnectionFactory()),
            new PurchaseWriteRepository(),
            new StockMovementWriter(),
            new AuditWriter(),
            new SystemClock());

    private async Task<long> CreateProductAsync(int quantity = 0)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            -- Products carry a category foreign key now, so the category has to exist first.
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 0, 0, 0, @quantity, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Cable {Guid.NewGuid():N}"[..20], quantity });
    }

    private async Task<long> CreateSupplierAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO suppliers (name, payable_balance, is_active, created_at_utc)
            VALUES (@name, 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"S {Guid.NewGuid():N}"[..18] });
    }

    private async Task<int> ReadQuantityAsync(long productId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT quantity_on_hand FROM products WHERE id = @productId;", new { productId });
    }

    private async Task<int?> LatestResultingQtyAsync(long productId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<int?>(
            "SELECT resulting_qty FROM stock_movements WHERE product_id = @productId ORDER BY id DESC LIMIT 1;",
            new { productId });
    }

    [Fact]
    public async Task Quantity_always_matches_the_latest_movements_resulting_quantity()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();

        await PurchaseService().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = 20, NewRetailPrice = 1100m },
            userId);

        await StockService().AdjustAsync(productId, 17, "Recount after stock take", userId);

        // data-model.md invariant 1.
        (await ReadQuantityAsync(productId)).Should().Be(17);
        (await LatestResultingQtyAsync(productId)).Should().Be(17);
    }

    [Fact]
    public async Task An_adjustment_records_the_signed_change()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 10);

        await StockService().AdjustAsync(productId, 7, "Three broken in transit", userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var movement = await connection.QuerySingleAsync<(int ChangeQty, string Reason, string Note)>(
            """
            SELECT change_qty, reason, note FROM stock_movements
            WHERE product_id = @productId ORDER BY id DESC LIMIT 1;
            """,
            new { productId });

        movement.ChangeQty.Should().Be(-3);
        movement.Reason.Should().Be("Adjustment");
        movement.Note.Should().Be("Three broken in transit");
    }

    [Fact]
    public async Task An_adjustment_upwards_is_recorded_as_a_positive_change()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 5);

        await StockService().AdjustAsync(productId, 12, "Found extra box in storeroom", userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var change = await connection.ExecuteScalarAsync<int>(
            "SELECT change_qty FROM stock_movements WHERE product_id = @productId ORDER BY id DESC LIMIT 1;",
            new { productId });

        change.Should().Be(7);
    }

    [Fact]
    public async Task An_adjustment_writes_an_audit_entry()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 10);

        await StockService().AdjustAsync(productId, 8, "Recount", userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var audit = await connection.QuerySingleAsync<(string OldValue, string NewValue, string Action)>(
            """
            SELECT old_value, new_value, action FROM audit_entries
            WHERE entity_type = 'Product' AND entity_id = @productId AND action = 'Adjustment'
            ORDER BY id DESC LIMIT 1;
            """,
            new { productId });

        audit.OldValue.Should().Be("10");
        audit.NewValue.Should().Be("8");
    }

    [Fact]
    public async Task An_adjustment_to_the_same_quantity_records_nothing()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 10);

        await StockService().AdjustAsync(productId, 10, "No change", userId);

        // A zero movement would be noise in a history the owner has to read.
        (await LatestResultingQtyAsync(productId)).Should().BeNull();
    }

    [Fact]
    public async Task Rejects_an_adjustment_to_a_negative_quantity()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 10);

        var act = async () => await StockService().AdjustAsync(productId, -1, "Nonsense", userId);

        await act.Should().ThrowAsync<Exception>();
        (await ReadQuantityAsync(productId)).Should().Be(10);
    }

    [Fact]
    public async Task Rejects_an_adjustment_without_a_note()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 10);

        var act = async () => await StockService().AdjustAsync(productId, 5, "   ", userId);

        await act.Should().ThrowAsync<Exception>();
        (await ReadQuantityAsync(productId)).Should().Be(10, "an unexplained change must not happen");
    }

    [Fact]
    public async Task History_lists_movements_newest_first()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 10);
        var service = StockService();

        await service.AdjustAsync(productId, 20, "First", userId);
        await service.AdjustAsync(productId, 30, "Second", userId);

        var history = await service.HistoryAsync(productId, page: 1, pageSize: 25);

        history.TotalItems.Should().Be(2);
        history.Items[0].Note.Should().Be("Second");
        history.Items[0].ResultingQty.Should().Be(30);
    }

    [Fact]
    public async Task History_reports_the_reason_for_each_movement()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync();

        await PurchaseService().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await StockService().AdjustAsync(productId, 9, "One damaged", userId);

        var history = await StockService().HistoryAsync(productId, 1, 25);

        history.Items.Select(m => m.Reason).Should().Contain(["Adjustment", "Purchase"]);
    }
}
