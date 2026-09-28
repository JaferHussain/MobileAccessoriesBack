using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Ledger;

/// <summary>
/// T044–T047 — settling a debt in instalments (FR-058 … FR-064).
///
/// <para><b>These tests are verification, not new behaviour.</b> The shop can already take a
/// payment of any amount against a balance, repeatedly, with overpayment gated. What was missing
/// was proof of the <i>sequence</i> — three payments in a row leaving the right figure each time,
/// which is what actually happens at the counter and what SC-018 measures.</para>
///
/// <para>They also stand as a guard on the credit rule added in this same feature: a salesman
/// must still be able to collect money owed, because collecting a debt does not create one
/// (FR-054).</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PartialRecoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public PartialRecoveryTests(ApiFactory api) => _api = api;

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

    /// <summary>A customer owing exactly <paramref name="amount"/>, set up through the front door.</summary>
    private async Task<long> CustomerOwingAsync(HttpClient admin, decimal amount)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var customerId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO customers (name, mobile_number, outstanding_balance, is_active, created_at_utc)
            VALUES (@name, '923001234567', 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Rec {Guid.NewGuid():N}"[..18] });

        var productId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
            INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 100, 0,
                    @amount, 100, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Rp {Guid.NewGuid():N}"[..20], amount });

        // The owner puts the debt on the books — only the owner may (FR-051).
        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 0m,
            paymentMethod = "Credit",
            items = new[] { new { productId, quantity = 1, unitSalePrice = amount, lineDiscount = 0m } },
        });

        sale.StatusCode.Should().Be(HttpStatusCode.Created);

        return customerId;
    }

    private async Task<decimal> BalanceAsync(long customerId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT outstanding_balance FROM customers WHERE id = @customerId;", new { customerId });
    }

    private static Task<HttpResponseMessage> PayAsync(
        HttpClient client, long customerId, decimal amount, bool confirmOverpayment = false) =>
        client.PostAsJsonAsync(
            $"/api/customers/{customerId}/payments",
            new { amount, paymentMethod = "Cash", confirmOverpayment });

    // ---------------------------------------------------------------- the sequence

    [Fact]
    public async Task A_debt_can_be_settled_in_instalments()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CustomerOwingAsync(admin, 3_000m);

        // SC-018, exactly as it happens at the counter: the customer pays what they can, when
        // they can, and the figure has to be right after every one of those visits.
        (await PayAsync(admin, customerId, 1_000m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(customerId)).Should().Be(2_000m);

        (await PayAsync(admin, customerId, 1_500m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(customerId)).Should().Be(500m);

        (await PayAsync(admin, customerId, 500m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(customerId)).Should().Be(0m);
    }

    [Fact]
    public async Task Each_payment_returns_the_balance_that_is_left()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CustomerOwingAsync(admin, 3_000m);

        var first = await PayAsync(admin, customerId, 1_000m);
        var firstData = (await first.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        // FR-060: the person who took the money must see what is still owed, there and then.
        firstData.GetProperty("balanceAfter").GetDecimal().Should().Be(2_000m);

        var second = await PayAsync(admin, customerId, 2_000m);
        var secondData = (await second.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        secondData.GetProperty("balanceAfter").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task Every_instalment_is_kept_as_its_own_record()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CustomerOwingAsync(admin, 3_000m);

        await PayAsync(admin, customerId, 1_000m);
        await PayAsync(admin, customerId, 1_500m);
        await PayAsync(admin, customerId, 500m);

        await using var connection = await _api.OpenDatabaseAsync();

        var payments = (await connection.QueryAsync<(decimal Amount, string Receipt)>(
            """
            SELECT amount AS Amount, receipt_number AS Receipt
            FROM customer_payments WHERE customer_id = @customerId ORDER BY id;
            """,
            new { customerId })).AsList();

        // FR-059, FR-063: three visits, three receipts. Nothing merged into a single figure.
        payments.Select(p => p.Amount).Should().Equal(1_000m, 1_500m, 500m);
        payments.Select(p => p.Receipt).Distinct().Should().HaveCount(3);

        var ledger = (await connection.QueryAsync<(string Type, decimal Paid, decimal After)>(
            """
            SELECT entry_type AS Type, paid_amount AS Paid, balance_after AS After
            FROM ledger_entries
            WHERE customer_id = @customerId AND entry_type = 'Payment'
            ORDER BY id;
            """,
            new { customerId })).AsList();

        // FR-064: a running balance the shopkeeper can read down the page.
        ledger.Select(e => e.Paid).Should().Equal(1_000m, 1_500m, 500m);
        ledger.Select(e => e.After).Should().Equal(2_000m, 500m, 0m);
    }

    [Fact]
    public async Task A_settled_customer_drops_out_of_the_money_owed_report()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CustomerOwingAsync(admin, 1_000m);

        await PayAsync(admin, customerId, 1_000m);

        var receivables = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            "/api/reports/receivables", Json);

        receivables!.Data!.EnumerateArray()
            .Select(r => r.GetProperty("customerId").GetInt64())
            .Should().NotContain(customerId);
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task A_payment_of_nothing_or_less_is_refused(decimal amount)
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CustomerOwingAsync(admin, 1_000m);

        var response = await PayAsync(admin, customerId, amount);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BalanceAsync(customerId)).Should().Be(1_000m, "nothing should have moved");
    }

    [Fact]
    public async Task Paying_more_than_is_owed_needs_confirming_first()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CustomerOwingAsync(admin, 500m);

        // FR-062. Usually this is a typo, and a balance that silently goes negative is a
        // conversation with the customer nobody wants to have.
        var unconfirmed = await PayAsync(admin, customerId, 800m);

        // 400, not 422: OverpaymentNotConfirmedException is a plain DomainException, which this
        // codebase treats as caller-correctable — resend with confirmOverpayment and it succeeds.
        unconfirmed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BalanceAsync(customerId)).Should().Be(500m);

        var confirmed = await PayAsync(admin, customerId, 800m, confirmOverpayment: true);

        confirmed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(customerId)).Should().Be(-300m);
    }

    // ---------------------------------------------------------------- who may collect

    [Fact]
    public async Task A_salesman_can_take_instalments_even_though_they_cannot_grant_credit()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var staff = await ClientAsync(UserRole.Staff);
        var customerId = await CustomerOwingAsync(admin, 3_000m);

        // FR-054, and the guard on this feature's own credit rule: taking money is not the same
        // as lending it, and the salesman must not have lost the ability to do the first.
        (await PayAsync(staff, customerId, 1_000m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(customerId)).Should().Be(2_000m);

        (await PayAsync(staff, customerId, 2_000m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(customerId)).Should().Be(0m);
    }

    [Fact]
    public async Task Instalments_settle_a_carried_forward_debt_too()
    {
        var admin = await ClientAsync(UserRole.Admin);

        await using var connection = await _api.OpenDatabaseAsync();

        var customerId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO customers (name, mobile_number, outstanding_balance, is_active, created_at_utc)
            VALUES (@name, '923001234567', 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Old {Guid.NewGuid():N}"[..18] });

        await admin.PutAsJsonAsync(
            $"/api/customers/{customerId}/opening-balance", new { amount = 12_000m });

        // A debt from the paper register behaves like any other once it is recorded.
        await PayAsync(admin, customerId, 5_000m);
        (await BalanceAsync(customerId)).Should().Be(7_000m);

        await PayAsync(admin, customerId, 7_000m);
        (await BalanceAsync(customerId)).Should().Be(0m);
    }
}
