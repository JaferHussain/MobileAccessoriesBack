using Dapper;
using FluentAssertions;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Invariants;

/// <summary>
/// T173 — the six invariants from data-model.md, checked against whatever the whole suite has
/// left in the database.
///
/// Every other test proves one operation behaves. These prove that after hundreds of sales,
/// purchases, payments, returns and adjustments interleaved across parallel collections, the
/// books still balance. If any of these fail, some code path is quietly corrupting the shop's
/// records regardless of what its own test says.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DataInvariantTests
{
    private readonly ApiFactory _api;

    public DataInvariantTests(ApiFactory api) => _api = api;

    [Fact]
    public async Task Product_quantity_matches_the_latest_stock_movement()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        // For every product that has ever moved, quantity_on_hand must equal the resulting_qty
        // of its most recent movement (invariant 1).
        var mismatches = (await connection.QueryAsync<(long ProductId, int Quantity, int Resulting)>(
            """
            SELECT p.id, p.quantity_on_hand, sm.resulting_qty
            FROM products p
            JOIN stock_movements sm ON sm.id = (
                SELECT id FROM stock_movements
                WHERE product_id = p.id
                ORDER BY id DESC
                LIMIT 1
            )
            WHERE p.quantity_on_hand <> sm.resulting_qty;
            """)).ToList();

        mismatches.Should().BeEmpty(
            "stock and its movement history are written in the same transaction");
    }

    [Fact]
    public async Task Customer_balance_matches_the_latest_ledger_entry()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var mismatches = (await connection.QueryAsync<(long CustomerId, decimal Balance, decimal Ledger)>(
            """
            SELECT c.id, c.outstanding_balance, le.balance_after
            FROM customers c
            JOIN ledger_entries le ON le.id = (
                SELECT id FROM ledger_entries
                WHERE customer_id = c.id
                ORDER BY id DESC
                LIMIT 1
            )
            WHERE c.outstanding_balance <> le.balance_after;
            """)).ToList();

        // Invariant 2 — SC-004: what the system says a customer owes must equal the register.
        mismatches.Should().BeEmpty("the balance and its ledger entry are written together");
    }

    [Fact]
    public async Task Supplier_payable_equals_purchases_less_payments_and_returns()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var mismatches = (await connection.QueryAsync<(long SupplierId, decimal Stored, decimal Computed)>(
            """
            SELECT s.id,
                   s.payable_balance,
                   COALESCE((SELECT SUM(total) FROM purchases WHERE supplier_id = s.id), 0)
                 - COALESCE((SELECT SUM(amount) FROM supplier_payments WHERE supplier_id = s.id), 0)
                 - COALESCE((SELECT SUM(total) FROM purchase_returns WHERE supplier_id = s.id), 0)
            FROM suppliers s
            HAVING s.payable_balance <> (
                COALESCE((SELECT SUM(total) FROM purchases WHERE supplier_id = s.id), 0)
              - COALESCE((SELECT SUM(amount) FROM supplier_payments WHERE supplier_id = s.id), 0)
              - COALESCE((SELECT SUM(total) FROM purchase_returns WHERE supplier_id = s.id), 0)
            );
            """)).ToList();

        mismatches.Should().BeEmpty("invariant 3: the payable is the sum of what moved it");
    }

    [Fact]
    public async Task Stock_is_never_negative()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var negative = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM products WHERE quantity_on_hand < 0;");

        var negativeMovements = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_movements WHERE resulting_qty < 0;");

        // Invariant 4, FR-006 — the shop can never have sold what it did not have.
        negative.Should().Be(0);
        negativeMovements.Should().Be(0);
    }

    [Fact]
    public async Task Invoice_totals_reconcile_with_their_line_items()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var mismatches = (await connection.QueryAsync<(long InvoiceId, decimal Subtotal, decimal Lines)>(
            """
            SELECT i.id, i.subtotal, COALESCE(SUM(ii.line_total), 0)
            FROM invoices i
            JOIN invoice_items ii ON ii.invoice_id = i.id
            GROUP BY i.id, i.subtotal
            HAVING i.subtotal <> COALESCE(SUM(ii.line_total), 0);
            """)).ToList();

        mismatches.Should().BeEmpty("the server recomputes the subtotal from the lines it stored");
    }

    [Fact]
    public async Task Every_invoice_that_owes_money_names_a_customer()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var orphans = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM invoices WHERE amount_remaining > 0 AND customer_id IS NULL;");

        // FR-017: an unpaid sale needs someone to owe it, or the debt belongs to nobody.
        orphans.Should().Be(0);
    }

    [Fact]
    public async Task Paid_plus_remaining_always_equals_the_invoice_total()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var mismatches = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM invoices WHERE ROUND(amount_paid + amount_remaining, 2) <> ROUND(total, 2);");

        mismatches.Should().Be(0);
    }

    [Fact]
    public async Task No_line_was_returned_more_times_than_it_was_sold()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var overReturned = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM invoice_items WHERE returned_qty > quantity OR returned_qty < 0;");

        var overReturnedPurchases = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM purchases WHERE returned_qty > quantity OR returned_qty < 0;");

        // FR-026.
        overReturned.Should().Be(0);
        overReturnedPurchases.Should().Be(0);
    }

    [Fact]
    public async Task Every_sale_line_recorded_the_cost_it_was_sold_at()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        // Invariant 6. A zero cost would mean profit was computed against nothing, which under
        // the latest-cost rule is how history silently becomes wrong.
        var missing = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM invoice_items ii
            JOIN products p ON p.id = ii.product_id
            WHERE ii.unit_cost_price = 0 AND p.cost_price > 0;
            """);

        missing.Should().Be(0, "each line snapshots the cost in force when it was sold");
    }

    [Fact]
    public async Task Every_ledger_entry_follows_from_the_one_before_it()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        // balance_after = previous balance_after + bill - paid, for every entry (FR-020).
        var broken = (await connection.QueryAsync<(long Id, long CustomerId, decimal Expected, decimal Actual)>(
            """
            SELECT curr.id,
                   curr.customer_id,
                   ROUND(COALESCE(prev.balance_after, 0) + curr.bill_amount - curr.paid_amount, 2),
                   ROUND(curr.balance_after, 2)
            FROM ledger_entries curr
            LEFT JOIN ledger_entries prev ON prev.id = (
                SELECT id FROM ledger_entries
                WHERE customer_id = curr.customer_id AND id < curr.id
                ORDER BY id DESC
                LIMIT 1
            )
            WHERE ROUND(curr.balance_after, 2)
                  <> ROUND(COALESCE(prev.balance_after, 0) + curr.bill_amount - curr.paid_amount, 2);
            """)).ToList();

        broken.Should().BeEmpty("the running balance is the shopkeeper's own arithmetic");
    }

    [Fact]
    public async Task Every_audit_entry_names_a_real_user()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var orphans = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM audit_entries a
            LEFT JOIN users u ON u.id = a.user_id
            WHERE u.id IS NULL;
            """);

        // FR-041, SC-013: every change traceable to who made it.
        orphans.Should().Be(0);
    }
}
