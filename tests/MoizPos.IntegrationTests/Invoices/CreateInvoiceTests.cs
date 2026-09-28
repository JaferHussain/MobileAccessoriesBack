using Dapper;
using FluentAssertions;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Data;
using MoizPos.Infrastructure.Repositories;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Invoices;

/// <summary>
/// T079–T083, T087 — the sale transaction. The most important tests in the system: stock,
/// customer balances and profit history all depend on this being exactly right.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CreateInvoiceTests
{
    private readonly ApiFactory _api;

    public CreateInvoiceTests(ApiFactory api) => _api = api;

    private MySqlConnectionFactory Factory() => new(_api.ConnectionString);

    private InvoiceService Service() =>
        new(
            new UnitOfWorkFactory(Factory()),
            new InvoiceWriteRepository(),
            new CustomerRepository(Factory()),
            new StockMovementWriter(),
            new AuditWriter(),
            new SystemClock());

    private PurchaseService Purchases() =>
        new(
            new UnitOfWorkFactory(Factory()),
            new PurchaseWriteRepository(),
            new StockMovementWriter(),
            new AuditWriter(),
            new SystemClock());

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
            new { name = $"Cable {Guid.NewGuid():N}"[..20], cost, salePrice, quantity });
    }

    private async Task<long> CreateCustomerAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO customers (name, mobile_number, outstanding_balance, is_active, created_at_utc)
            VALUES (@name, '923001234567', 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Cust {Guid.NewGuid():N}"[..18] });
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

    private async Task<int> QuantityAsync(long productId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT quantity_on_hand FROM products WHERE id = @productId;", new { productId });
    }

    private async Task<decimal> BalanceAsync(long customerId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT outstanding_balance FROM customers WHERE id = @customerId;", new { customerId });
    }

    private static CreateInvoiceRequest Sale(
        long productId, int quantity, decimal price, decimal paid,
        long? customerId = null, decimal lineDiscount = 0m, decimal orderDiscount = 0m,
        PaymentMethod method = PaymentMethod.Cash, string? key = null) =>
        new()
        {
            CustomerId = customerId,
            AmountPaid = paid,
            OrderDiscount = orderDiscount,
            PaymentMethod = method,
            IdempotencyKey = key,
            Items =
            [
                new CreateInvoiceLine
                {
                    ProductId = productId,
                    Quantity = quantity,
                    UnitSalePrice = price,
                    LineDiscount = lineDiscount,
                },
            ],
        };

    // ================================================================
    //  spec US1 scenario 1 — the everyday cash sale
    // ================================================================

    [Fact]
    public async Task A_cash_sale_totals_correctly_and_decrements_stock()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(quantity: 10, cost: 800m, salePrice: 1100m);

        var result = await Service().CreateAsync(Sale(productId, 2, 1100m, paid: 2200m), userId, UserRole.Admin);

        result.Total.Should().Be(2200m);
        result.AmountRemaining.Should().Be(0m);
        (await QuantityAsync(productId)).Should().Be(8);
    }

    [Fact]
    public async Task An_invoice_number_is_assigned()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        var result = await Service().CreateAsync(Sale(productId, 1, 1100m, 1100m), userId, UserRole.Admin);

        result.InvoiceNumber.Should().MatchRegex(@"^INV-\d{4}-\d{6}$");
    }

    [Fact]
    public async Task Invoice_numbers_do_not_repeat()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1100m);
        var service = Service();

        var first = await service.CreateAsync(Sale(productId, 1, 1100m, 1100m), userId, UserRole.Admin);
        var second = await service.CreateAsync(Sale(productId, 1, 1100m, 1100m), userId, UserRole.Admin);

        second.InvoiceNumber.Should().NotBe(first.InvoiceNumber);
    }

    // ================================================================
    //  spec US1 scenario 3 — refusing to oversell
    // ================================================================

    [Fact]
    public async Task Selling_more_than_is_in_stock_is_refused_and_writes_nothing()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(quantity: 3, cost: 800m, salePrice: 1100m);

        var act = async () => await Service().CreateAsync(Sale(productId, 5, 1100m, 5500m), userId, UserRole.Admin);

        await act.Should().ThrowAsync<InsufficientStockException>();

        (await QuantityAsync(productId)).Should().Be(3, "a refused sale must not move stock");

        await using var connection = await _api.OpenDatabaseAsync();

        var movements = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_movements WHERE product_id = @productId;", new { productId });

        movements.Should().Be(0, "a refused sale must not write a movement");
    }

    [Fact]
    public async Task A_shortage_on_one_line_refuses_the_whole_sale()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var plenty = await CreateProductAsync(quantity: 100, cost: 100m, salePrice: 200m);
        var scarce = await CreateProductAsync(quantity: 1, cost: 800m, salePrice: 1100m);

        var request = new CreateInvoiceRequest
        {
            AmountPaid = 0m,
            PaymentMethod = PaymentMethod.Credit,
            CustomerId = await CreateCustomerAsync(),
            Items =
            [
                new CreateInvoiceLine { ProductId = plenty, Quantity = 2, UnitSalePrice = 200m },
                new CreateInvoiceLine { ProductId = scarce, Quantity = 5, UnitSalePrice = 1100m },
            ],
        };

        var act = async () => await Service().CreateAsync(request, userId, UserRole.Admin);

        await act.Should().ThrowAsync<InsufficientStockException>();

        // The in-stock line must not have been deducted (FR-050).
        (await QuantityAsync(plenty)).Should().Be(100);
        (await QuantityAsync(scarce)).Should().Be(1);
    }

    [Fact]
    public async Task Selling_exactly_the_last_units_is_allowed()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(quantity: 3, cost: 800m, salePrice: 1100m);

        await Service().CreateAsync(Sale(productId, 3, 1100m, 3300m), userId, UserRole.Admin);

        (await QuantityAsync(productId)).Should().Be(0);
    }

    // ================================================================
    //  Concurrency — spec edge case: two sales for the last unit
    // ================================================================

    [Fact]
    public async Task Two_concurrent_sales_for_the_last_unit_produce_exactly_one_success()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(quantity: 1, cost: 800m, salePrice: 1100m);

        var first = Service().CreateAsync(Sale(productId, 1, 1100m, 1100m), userId, UserRole.Admin);
        var second = Service().CreateAsync(Sale(productId, 1, 1100m, 1100m), userId, UserRole.Admin);

        var outcomes = await Task.WhenAll(
            Capture(first), Capture(second));

        outcomes.Count(o => o.Succeeded).Should().Be(1, "only one sale can have the last unit");
        outcomes.Count(o => !o.Succeeded).Should().Be(1);

        // Stock must never go negative (FR-006).
        (await QuantityAsync(productId)).Should().Be(0);

        static async Task<(bool Succeeded, Exception? Error)> Capture(Task<CreateInvoiceResult> task)
        {
            try
            {
                await task;
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex);
            }
        }
    }

    // ================================================================
    //  Cost snapshot — FR-011c, the guard on historical profit
    // ================================================================

    [Fact]
    public async Task Each_line_snapshots_the_cost_at_the_moment_of_sale()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var productId = await CreateProductAsync(quantity: 10, cost: 800m, salePrice: 1100m);

        var result = await Service().CreateAsync(Sale(productId, 2, 1100m, 2200m), userId, UserRole.Admin);

        await using var connection = await _api.OpenDatabaseAsync();

        var cost = await connection.ExecuteScalarAsync<decimal>(
            "SELECT unit_cost_price FROM invoice_items WHERE invoice_id = @id;",
            new { id = result.InvoiceId });

        cost.Should().Be(800m);
    }

    [Fact]
    public async Task A_later_purchase_does_not_rewrite_an_earlier_sales_cost()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);
        var supplierId = await CreateSupplierAsync();
        var productId = await CreateProductAsync(quantity: 10, cost: 800m, salePrice: 1100m);

        // Sell while the cost is 800.
        var sale = await Service().CreateAsync(Sale(productId, 2, 1100m, 2200m), userId, UserRole.Admin);

        // A later purchase raises the cost of all stock on hand to 850 (FR-011a).
        await Purchases().RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 850m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var lineCost = await connection.ExecuteScalarAsync<decimal>(
            "SELECT unit_cost_price FROM invoice_items WHERE invoice_id = @id;",
            new { id = sale.InvoiceId });

        var currentCost = await connection.ExecuteScalarAsync<decimal>(
            "SELECT cost_price FROM products WHERE id = @productId;", new { productId });

        // spec US4 scenario 3: the earlier sale's profit is unchanged.
        lineCost.Should().Be(800m);
        currentCost.Should().Be(850m);
    }

    [Fact]
    public async Task The_product_name_is_snapshotted_so_old_invoices_stay_readable()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        var result = await Service().CreateAsync(Sale(productId, 1, 1100m, 1100m), userId, UserRole.Admin);

        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            "UPDATE products SET name = 'Renamed Later' WHERE id = @productId;", new { productId });

        var storedName = await connection.ExecuteScalarAsync<string>(
            "SELECT product_name FROM invoice_items WHERE invoice_id = @id;",
            new { id = result.InvoiceId });

        storedName.Should().NotBe("Renamed Later");
    }

    // ================================================================
    //  Credit sales and the customer requirement — FR-017, FR-020
    // ================================================================

    [Fact]
    public async Task An_unpaid_sale_without_a_customer_is_refused()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        var act = async () => await Service().CreateAsync(
            Sale(productId, 1, 1100m, paid: 0m, method: PaymentMethod.Credit), userId, UserRole.Admin);

        await act.Should().ThrowAsync<CustomerRequiredException>();

        (await QuantityAsync(productId)).Should().Be(10);
    }

    [Fact]
    public async Task A_partial_payment_raises_the_customer_balance_by_the_remainder()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 3000m);
        var customerId = await CreateCustomerAsync();

        // spec US2 scenario 1: bill 3,000, paid 1,000 -> balance 2,000.
        var result = await Service().CreateAsync(
            Sale(productId, 1, 3000m, paid: 1000m, customerId: customerId,
                 method: PaymentMethod.Partial), userId, UserRole.Admin);

        result.AmountRemaining.Should().Be(2000m);
        (await BalanceAsync(customerId)).Should().Be(2000m);
    }

    [Fact]
    public async Task A_credit_sale_writes_a_ledger_entry_with_the_running_balance()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 3000m);
        var customerId = await CreateCustomerAsync();

        await Service().CreateAsync(
            Sale(productId, 1, 3000m, 1000m, customerId, method: PaymentMethod.Partial), userId, UserRole.Admin);

        await using var connection = await _api.OpenDatabaseAsync();

        var entry = await connection.QuerySingleAsync<(decimal Bill, decimal Paid, decimal Balance, string Type)>(
            """
            SELECT bill_amount, paid_amount, balance_after, entry_type
            FROM ledger_entries WHERE customer_id = @customerId ORDER BY id DESC LIMIT 1;
            """,
            new { customerId });

        entry.Bill.Should().Be(3000m);
        entry.Paid.Should().Be(1000m);
        entry.Balance.Should().Be(2000m);
        entry.Type.Should().Be("Invoice");
    }

    [Fact]
    public async Task Customer_balance_always_equals_the_latest_ledger_entry()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(100, 800m, 1000m);
        var customerId = await CreateCustomerAsync();
        var service = Service();

        await service.CreateAsync(Sale(productId, 1, 1000m, 400m, customerId, method: PaymentMethod.Partial), userId, UserRole.Admin);
        await service.CreateAsync(Sale(productId, 2, 1000m, 0m, customerId, method: PaymentMethod.Credit), userId, UserRole.Admin);

        await using var connection = await _api.OpenDatabaseAsync();

        var latest = await connection.ExecuteScalarAsync<decimal>(
            "SELECT balance_after FROM ledger_entries WHERE customer_id = @customerId ORDER BY id DESC LIMIT 1;",
            new { customerId });

        // data-model.md invariant 2.
        (await BalanceAsync(customerId)).Should().Be(latest);
    }

    [Fact]
    public async Task A_fully_paid_sale_needs_no_customer()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        var result = await Service().CreateAsync(Sale(productId, 1, 1100m, 1100m), userId, UserRole.Admin);

        result.CustomerId.Should().BeNull();
    }

    [Fact]
    public async Task A_new_customer_can_be_created_inline_during_the_sale()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 3000m);

        var result = await Service().CreateAsync(
            new CreateInvoiceRequest
            {
                NewCustomer = new NewCustomer("Bilal", "923009999999"),
                AmountPaid = 1000m,
                PaymentMethod = PaymentMethod.Partial,
                Items = [new CreateInvoiceLine { ProductId = productId, Quantity = 1, UnitSalePrice = 3000m }],
            }, userId, UserRole.Admin);

        result.CustomerId.Should().NotBeNull();
        (await BalanceAsync(result.CustomerId!.Value)).Should().Be(2000m);
    }

    // ================================================================
    //  Server-authoritative pricing and discounts
    // ================================================================

    [Fact]
    public async Task Discounts_reduce_the_payable_total()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 3000m);

        // spec US1 scenario 2: 5,000 cart, 100 line discount, 400 order discount -> 4,500.
        var result = await Service().CreateAsync(
            new CreateInvoiceRequest
            {
                AmountPaid = 4500m,
                OrderDiscount = 400m,
                Items =
                [
                    new CreateInvoiceLine
                    {
                        ProductId = productId, Quantity = 2, UnitSalePrice = 2500m, LineDiscount = 100m,
                    },
                ],
            }, userId, UserRole.Admin);

        result.Subtotal.Should().Be(4900m);
        result.Total.Should().Be(4500m);
    }

    [Fact]
    public async Task An_over_discount_is_refused()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1000m);

        var act = async () => await Service().CreateAsync(
            Sale(productId, 1, 1000m, 0m, await CreateCustomerAsync(), orderDiscount: 1500m), userId, UserRole.Admin);

        await act.Should().ThrowAsync<DiscountExceedsTotalException>();
        (await QuantityAsync(productId)).Should().Be(10);
    }

    [Fact]
    public async Task Paying_more_than_the_total_is_refused()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1000m);

        var act = async () => await Service().CreateAsync(Sale(productId, 1, 1000m, 1500m), userId, UserRole.Admin);

        await act.Should().ThrowAsync<BusinessRuleViolationException>();
    }

    [Fact]
    public async Task The_same_product_twice_in_one_cart_is_refused()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1000m);

        var act = async () => await Service().CreateAsync(
            new CreateInvoiceRequest
            {
                AmountPaid = 2000m,
                Items =
                [
                    new CreateInvoiceLine { ProductId = productId, Quantity = 1, UnitSalePrice = 1000m },
                    new CreateInvoiceLine { ProductId = productId, Quantity = 1, UnitSalePrice = 1000m },
                ],
            }, userId, UserRole.Admin);

        // Two lines for one product would each check stock against the same locked quantity.
        await act.Should().ThrowAsync<BusinessRuleViolationException>();
    }

    [Fact]
    public async Task An_empty_cart_is_refused()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);

        var act = async () => await Service().CreateAsync(
            new CreateInvoiceRequest { AmountPaid = 0m, Items = [] }, userId, UserRole.Admin);

        await act.Should().ThrowAsync<BusinessRuleViolationException>();
    }

    [Fact]
    public async Task A_sale_of_an_unknown_product_writes_nothing()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);

        var act = async () => await Service().CreateAsync(
            Sale(999_999_999, 1, 1000m, 1000m), userId, UserRole.Admin);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ================================================================
    //  Idempotency — a double-tap at a busy counter
    // ================================================================

    [Fact]
    public async Task A_replayed_idempotency_key_does_not_sell_the_goods_twice()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1100m);
        var key = Guid.NewGuid().ToString("N");
        var service = Service();

        var first = await service.CreateAsync(Sale(productId, 2, 1100m, 2200m, key: key), userId, UserRole.Admin);

        var act = async () => await service.CreateAsync(Sale(productId, 2, 1100m, 2200m, key: key), userId, UserRole.Admin);

        var duplicate = (await act.Should().ThrowAsync<DuplicateInvoiceException>()).Which;
        duplicate.ExistingInvoiceId.Should().Be(first.InvoiceId);

        // Stock moved once, not twice.
        (await QuantityAsync(productId)).Should().Be(8);
    }

    [Fact]
    public async Task Different_keys_record_separate_sales()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1100m);
        var service = Service();

        await service.CreateAsync(Sale(productId, 1, 1100m, 1100m, key: Guid.NewGuid().ToString("N")), userId, UserRole.Admin);
        await service.CreateAsync(Sale(productId, 1, 1100m, 1100m, key: Guid.NewGuid().ToString("N")), userId, UserRole.Admin);

        (await QuantityAsync(productId)).Should().Be(8);
    }

    // ================================================================
    //  Stock movements and audit
    // ================================================================

    [Fact]
    public async Task A_sale_writes_a_negative_stock_movement_per_line()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 1100m);

        var result = await Service().CreateAsync(Sale(productId, 3, 1100m, 3300m), userId, UserRole.Admin);

        await using var connection = await _api.OpenDatabaseAsync();

        var movement = await connection.QuerySingleAsync<(int Change, int Resulting, string Reason, long Reference)>(
            """
            SELECT change_qty, resulting_qty, reason, reference_id
            FROM stock_movements WHERE product_id = @productId ORDER BY id DESC LIMIT 1;
            """,
            new { productId });

        movement.Change.Should().Be(-3);
        movement.Resulting.Should().Be(7);
        movement.Reason.Should().Be("Sale");
        movement.Reference.Should().Be(result.InvoiceId);
    }

    [Fact]
    public async Task A_sale_audits_the_stock_and_balance_changes()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var productId = await CreateProductAsync(10, 800m, 3000m);
        var customerId = await CreateCustomerAsync();

        await Service().CreateAsync(
            Sale(productId, 1, 3000m, 1000m, customerId, method: PaymentMethod.Partial), userId, UserRole.Admin);

        await using var connection = await _api.OpenDatabaseAsync();

        var fields = (await connection.QueryAsync<string>(
            "SELECT field_name FROM audit_entries WHERE action = 'Sale' AND user_id = @userId;",
            new { userId })).ToList();

        fields.Should().Contain("quantity_on_hand").And.Contain("outstanding_balance");
    }
}
