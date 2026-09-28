using Dapper;
using FluentAssertions;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Data;
using MoizPos.Infrastructure.Repositories;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Ledger;

/// <summary>
/// T102 / T104 — the udhaar register end to end. The first test walks the shop owner's own
/// worked example: bill 3,000 paid 1,000 leaves 2,000, then a 1,500 payment leaves 500.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReceivePaymentTests
{
    private readonly ApiFactory _api;

    public ReceivePaymentTests(ApiFactory api) => _api = api;

    private MySqlConnectionFactory Factory() => new(_api.ConnectionString);

    private CustomerLedgerService Ledger() =>
        new(
            new UnitOfWorkFactory(Factory()),
            new CustomerPaymentWriteRepository(),
            new LedgerRepository(Factory()),
            new AuditWriter(),
            new SystemClock());

    private InvoiceService Sales() =>
        new(
            new UnitOfWorkFactory(Factory()),
            new InvoiceWriteRepository(),
            new CustomerRepository(Factory()),
            new StockMovementWriter(),
            new AuditWriter(),
            new SystemClock());

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

    private async Task<long> CreateProductAsync(int quantity, decimal salePrice)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            -- Products carry a category foreign key now, so the category has to exist first.
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 800, 0, @salePrice, @quantity, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Cable {Guid.NewGuid():N}"[..20], salePrice, quantity });
    }

    private async Task<decimal> BalanceAsync(long customerId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT outstanding_balance FROM customers WHERE id = @customerId;", new { customerId });
    }

    private async Task SellOnCreditAsync(long customerId, long productId, decimal price, decimal paid)
    {
        await Sales().CreateAsync(
            new CreateInvoiceRequest
            {
                CustomerId = customerId,
                AmountPaid = paid,
                PaymentMethod = paid > 0 ? PaymentMethod.Partial : PaymentMethod.Credit,
                Items = [new CreateInvoiceLine { ProductId = productId, Quantity = 1, UnitSalePrice = price }],
            },
            // The owner, because only the owner may sell on credit (FR-051). This helper seeds
            // the debt these ledger tests then recover; it is not testing the role rule itself.
            (await _api.CreateUserAsync(UserRole.Admin)).Id,
            UserRole.Admin);
    }

    // ================================================================
    //  The owner's worked example — spec US2 scenarios 1 and 2
    // ================================================================

    [Fact]
    public async Task Walks_the_owners_worked_example_end_to_end()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 3000m);

        // "goods worth 3,000, pays only 1,000 today" -> balance 2,000
        await SellOnCreditAsync(customerId, productId, 3000m, 1000m);
        (await BalanceAsync(customerId)).Should().Be(2000m);

        // "days later the customer pays 1,500" -> balance 500
        var payment = await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 1500m }, userId);

        payment.BalanceAfter.Should().Be(500m);
        (await BalanceAsync(customerId)).Should().Be(500m);
    }

    [Fact]
    public async Task The_ledger_shows_both_entries_with_their_running_balance()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 3000m);

        await SellOnCreditAsync(customerId, productId, 3000m, 1000m);
        await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 1500m }, userId);

        var ledger = await Ledger().LedgerAsync(customerId, null, null, 1, 25);

        ledger.TotalItems.Should().Be(2);

        ledger.Items[0].BillAmount.Should().Be(3000m);
        ledger.Items[0].PaidAmount.Should().Be(1000m);
        ledger.Items[0].BalanceAfter.Should().Be(2000m);
        ledger.Items[0].EntryType.Should().Be(LedgerEntryType.Invoice);

        ledger.Items[1].BillAmount.Should().Be(0m);
        ledger.Items[1].PaidAmount.Should().Be(1500m);
        ledger.Items[1].BalanceAfter.Should().Be(500m);
        ledger.Items[1].EntryType.Should().Be(LedgerEntryType.Payment);
    }

    [Fact]
    public async Task Ledger_entries_carry_their_document_number()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 1000m);

        await SellOnCreditAsync(customerId, productId, 1000m, 0m);
        await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 400m }, userId);

        var ledger = await Ledger().LedgerAsync(customerId, null, null, 1, 25);

        ledger.Items[0].ReferenceNumber.Should().StartWith("INV-");
        ledger.Items[1].ReferenceNumber.Should().StartWith("RCP-");
    }

    // ================================================================
    //  Payments
    // ================================================================

    [Fact]
    public async Task A_payment_produces_a_receipt_number()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 1000m);

        await SellOnCreditAsync(customerId, productId, 1000m, 0m);

        var result = await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 400m }, userId);

        result.ReceiptNumber.Should().MatchRegex(@"^RCP-\d{4}-\d{6}$");
    }

    [Fact]
    public async Task Receipt_numbers_do_not_repeat()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 5000m);
        var ledger = Ledger();

        await SellOnCreditAsync(customerId, productId, 5000m, 0m);

        var first = await ledger.ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 100m }, userId);
        var second = await ledger.ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 100m }, userId);

        second.ReceiptNumber.Should().NotBe(first.ReceiptNumber);
    }

    [Fact]
    public async Task Settling_the_full_balance_lands_on_zero()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 2500m);

        await SellOnCreditAsync(customerId, productId, 2500m, 0m);

        var result = await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 2500m }, userId);

        result.BalanceAfter.Should().Be(0m);
    }

    // ================================================================
    //  Overpayment — FR-022
    // ================================================================

    [Fact]
    public async Task Paying_more_than_is_owed_is_refused_without_confirmation()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 500m);

        await SellOnCreditAsync(customerId, productId, 500m, 0m);

        var act = async () => await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 10_000m }, userId);

        await act.Should().ThrowAsync<OverpaymentNotConfirmedException>();

        // spec US2 scenario: the balance must be untouched by a rejected payment.
        (await BalanceAsync(customerId)).Should().Be(500m);
    }

    [Fact]
    public async Task A_rejected_overpayment_writes_no_receipt_and_no_ledger_entry()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();

        var act = async () => await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 100m }, userId);

        await act.Should().ThrowAsync<OverpaymentNotConfirmedException>();

        await using var connection = await _api.OpenDatabaseAsync();

        var payments = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM customer_payments WHERE customer_id = @customerId;", new { customerId });
        var entries = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ledger_entries WHERE customer_id = @customerId;", new { customerId });

        payments.Should().Be(0);
        entries.Should().Be(0);
    }

    [Fact]
    public async Task A_confirmed_overpayment_is_accepted_and_flagged()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 500m);

        await SellOnCreditAsync(customerId, productId, 500m, 0m);

        var result = await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest
            { CustomerId = customerId, Amount = 800m, ConfirmOverpayment = true },
            userId);

        result.BalanceAfter.Should().Be(-300m);

        await using var connection = await _api.OpenDatabaseAsync();

        var flagged = await connection.ExecuteScalarAsync<bool>(
            "SELECT is_overpayment FROM customer_payments WHERE customer_id = @customerId ORDER BY id DESC LIMIT 1;",
            new { customerId });

        flagged.Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_a_zero_or_negative_payment()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();

        foreach (var amount in new[] { 0m, -100m })
        {
            var act = async () => await Ledger().ReceivePaymentAsync(
                new ReceivePaymentRequest { CustomerId = customerId, Amount = amount }, userId);

            await act.Should().ThrowAsync<BusinessRuleViolationException>();
        }
    }

    [Fact]
    public async Task Rejects_a_payment_for_an_unknown_customer()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);

        var act = async () => await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = 999_999_999, Amount = 100m }, userId);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ================================================================
    //  Invariants and summary
    // ================================================================

    [Fact]
    public async Task Balance_always_equals_the_latest_ledger_entry()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(100, 1000m);
        var ledger = Ledger();

        await SellOnCreditAsync(customerId, productId, 1000m, 0m);
        await ledger.ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 300m }, userId);
        await SellOnCreditAsync(customerId, productId, 1000m, 200m);
        await ledger.ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 450m }, userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var latest = await connection.ExecuteScalarAsync<decimal>(
            "SELECT balance_after FROM ledger_entries WHERE customer_id = @customerId ORDER BY id DESC LIMIT 1;",
            new { customerId });

        // data-model.md invariant 2.
        (await BalanceAsync(customerId)).Should().Be(latest);
    }

    [Fact]
    public async Task The_running_balance_follows_bills_minus_payments()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(100, 1500m);
        var ledger = Ledger();

        await SellOnCreditAsync(customerId, productId, 1500m, 0m);      // 1500
        await SellOnCreditAsync(customerId, productId, 2500m, 1000m);   // 3000
        await ledger.ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 3000m }, userId); // 0

        var entries = await ledger.LedgerAsync(customerId, null, null, 1, 25);

        entries.Items.Select(e => e.BalanceAfter).Should().Equal(1500m, 3000m, 0m);
    }

    [Fact]
    public async Task The_profile_summary_reconciles_with_the_ledger()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(100, 2000m);
        var ledger = Ledger();

        await SellOnCreditAsync(customerId, productId, 2000m, 500m);
        await ledger.ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 700m }, userId);

        var summary = await ledger.SummaryAsync(customerId);

        // FR-023: total purchased, total paid, total outstanding.
        summary.TotalPurchased.Should().Be(2000m);
        summary.TotalPaid.Should().Be(1200m);
        summary.TotalOutstanding.Should().Be(800m);
        summary.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task A_payment_is_audited()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(10, 1000m);

        await SellOnCreditAsync(customerId, productId, 1000m, 0m);
        await Ledger().ReceivePaymentAsync(
            new ReceivePaymentRequest { CustomerId = customerId, Amount = 400m }, userId);

        await using var connection = await _api.OpenDatabaseAsync();

        var audit = await connection.QuerySingleAsync<(string Old, string New)>(
            """
            SELECT old_value, new_value FROM audit_entries
            WHERE entity_type = 'Customer' AND entity_id = @customerId AND action = 'Payment'
            ORDER BY id DESC LIMIT 1;
            """,
            new { customerId });

        audit.Old.Should().Be("1000.00");
        audit.New.Should().Be("600.00");
    }

    [Fact]
    public async Task An_empty_ledger_reports_zero_totals()
    {
        var customerId = await CreateCustomerAsync();

        var summary = await Ledger().SummaryAsync(customerId);

        summary.TotalPurchased.Should().Be(0m);
        summary.TotalPaid.Should().Be(0m);
        summary.TotalOutstanding.Should().Be(0m);
    }
}
