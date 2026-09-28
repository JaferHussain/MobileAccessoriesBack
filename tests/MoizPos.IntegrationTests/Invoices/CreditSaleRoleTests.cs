using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Invoices;

/// <summary>
/// T012–T014 — only the owner may let goods leave on credit (FR-051 … FR-055).
///
/// Every credit sale the owner did not sanction is stock that has left the shop against a debt
/// they never agreed to, and unlike a mistyped figure it cannot be corrected afterwards — the
/// goods are gone. These tests prove the rule end to end, and prove it costs the salesman nothing
/// they are supposed to be able to do.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CreditSaleRoleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public CreditSaleRoleTests(ApiFactory api) => _api = api;

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

    private async Task<long> CreateProductAsync(decimal salePrice = 5000m, int quantity = 20)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 800, 0,
                    @salePrice, @quantity, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Cr {Guid.NewGuid():N}"[..20], salePrice, quantity });
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

    private static object Sale(long productId, long customerId, decimal unitPrice, decimal amountPaid) =>
        new
        {
            customerId,
            amountPaid,
            paymentMethod = amountPaid <= 0m ? "Credit" : "Partial",
            items = new[]
            {
                new { productId, quantity = 1, unitSalePrice = unitPrice, lineDiscount = 0m },
            },
        };

    // ---------------------------------------------------------------- the rule

    [Fact]
    public async Task A_salesman_cannot_sell_wholly_on_credit()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync();
        var customerId = await CreateCustomerAsync();

        var response = await staff.PostAsJsonAsync(
            "/api/invoices", Sale(productId, customerId, 5000m, amountPaid: 0m));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("CREDIT_REQUIRES_ADMIN");
    }

    [Fact]
    public async Task A_salesman_cannot_sell_part_paid()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync();
        var customerId = await CreateCustomerAsync();

        // FR-052: Rs 2,000 of the shop's goods still walked out against a debt.
        var response = await staff.PostAsJsonAsync(
            "/api/invoices", Sale(productId, customerId, 5000m, amountPaid: 3000m));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_salesman_cannot_dodge_the_rule_by_calling_it_a_cash_sale()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync();
        var customerId = await CreateCustomerAsync();

        // The label says Cash; the money says otherwise. The server recomputes the total from the
        // locked product rows, so the label buys nothing.
        var response = await staff.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 1000m,
            paymentMethod = "Cash",
            items = new[]
            {
                new { productId, quantity = 1, unitSalePrice = 5000m, lineDiscount = 0m },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_salesman_can_sell_for_full_payment()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync();
        var customerId = await CreateCustomerAsync();

        var response = await staff.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 5000m,
            paymentMethod = "Cash",
            items = new[]
            {
                new { productId, quantity = 1, unitSalePrice = 5000m, lineDiscount = 0m },
            },
        });

        // FR-053. This is the shop's main trade and must be completely unaffected.
        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "selling for full payment is the salesman's job. Server said: {0}",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_walk_in_cash_sale_by_a_salesman_still_works()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync();

        // No customer at all — the commonest sale in the shop.
        var response = await staff.PostAsJsonAsync("/api/invoices", new
        {
            amountPaid = 5000m,
            paymentMethod = "Cash",
            items = new[]
            {
                new { productId, quantity = 1, unitSalePrice = 5000m, lineDiscount = 0m },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_owner_can_sell_on_credit()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync();
        var customerId = await CreateCustomerAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/invoices", Sale(productId, customerId, 5000m, amountPaid: 2000m));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        data.GetProperty("amountRemaining").GetDecimal().Should().Be(3000m);
        data.GetProperty("customerBalance").GetDecimal().Should().Be(3000m);
    }

    // ---------------------------------------------------------------- nothing is written

    [Fact]
    public async Task A_refused_credit_sale_changes_absolutely_nothing()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var productId = await CreateProductAsync(quantity: 20);
        var customerId = await CreateCustomerAsync();

        await using var connection = await _api.OpenDatabaseAsync();

        var invoicesBefore = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM invoices;");
        var stockBefore = await connection.ExecuteScalarAsync<int>(
            "SELECT quantity_on_hand FROM products WHERE id = @productId;", new { productId });
        var movementsBefore = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_movements WHERE product_id = @productId;", new { productId });

        var response = await staff.PostAsJsonAsync(
            "/api/invoices", Sale(productId, customerId, 5000m, amountPaid: 0m));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // FR-055. The refusal happens before the first write, so a rejected sale must be
        // indistinguishable from one that was never attempted.
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM invoices;"))
            .Should().Be(invoicesBefore);

        (await connection.ExecuteScalarAsync<int>(
            "SELECT quantity_on_hand FROM products WHERE id = @productId;", new { productId }))
            .Should().Be(stockBefore);

        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM stock_movements WHERE product_id = @productId;", new { productId }))
            .Should().Be(movementsBefore);

        (await connection.ExecuteScalarAsync<decimal>(
            "SELECT outstanding_balance FROM customers WHERE id = @customerId;", new { customerId }))
            .Should().Be(0m);
    }

    [Fact]
    public async Task A_salesman_leaves_no_credit_invoice_behind_however_hard_they_try()
    {
        // Data-model invariant 5, asserted against the database rather than through the API so it
        // holds regardless of which route a sale arrived by.
        //
        // Scoped to THIS salesman's own user id rather than every Staff row in the database: the
        // integration database is shared across the suite and retains invoices seeded by earlier
        // tests from before this rule existed. A global scan would be asserting the history of the
        // test database, not the behaviour of the system.
        var (staffId, username, password) = await _api.CreateUserAsync(UserRole.Staff);

        var client = _api.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers
            .AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        var productId = await CreateProductAsync();
        var customerId = await CreateCustomerAsync();

        // Every shape of credit this salesman could attempt.
        foreach (var paid in new[] { 0m, 100m, 4999.99m })
        {
            var attempt = await client.PostAsJsonAsync(
                "/api/invoices", Sale(productId, customerId, 5000m, paid));

            attempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        await using var connection = await _api.OpenDatabaseAsync();

        var offenders = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM invoices
            WHERE user_id = @staffId AND amount_remaining > 0;
            """,
            new { staffId });

        offenders.Should().Be(0);
    }

    // ---------------------------------------------------------------- not collateral damage

    [Fact]
    public async Task A_salesman_can_still_collect_money_that_is_owed()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var productId = await CreateProductAsync();
        var customerId = await CreateCustomerAsync();

        // The owner puts the debt on the books...
        var sale = await admin.PostAsJsonAsync(
            "/api/invoices", Sale(productId, customerId, 5000m, amountPaid: 0m));

        sale.StatusCode.Should().Be(HttpStatusCode.Created);

        // ...and the salesman takes the money when the customer comes in to pay.
        var staff = await ClientAsync(UserRole.Staff);

        var payment = await staff.PostAsJsonAsync(
            $"/api/customers/{customerId}/payments",
            new { amount = 1000m, paymentMethod = "Cash" });

        // FR-054. Collecting a debt does not create one, and the credit rule must not have
        // quietly taken this away from the salesman.
        payment.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "taking payment is the salesman's job. Server said: {0}",
            await payment.Content.ReadAsStringAsync());

        await using var connection = await _api.OpenDatabaseAsync();

        (await connection.ExecuteScalarAsync<decimal>(
            "SELECT outstanding_balance FROM customers WHERE id = @customerId;", new { customerId }))
            .Should().Be(4000m);
    }

    [Fact]
    public async Task A_salesman_can_still_read_a_customers_ledger()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();

        var response = await staff.GetAsync($"/api/customers/{customerId}/ledger");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
