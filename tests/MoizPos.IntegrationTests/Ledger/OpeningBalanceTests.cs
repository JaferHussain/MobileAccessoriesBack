using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Ledger;

/// <summary>
/// T026–T031 — carrying a customer's paper-register debt into the system (FR-065 … FR-073).
///
/// The shop kept udhaar on paper for years. Until this exists, every long-standing customer reads
/// as owing nothing and the receivables total understates what the shop is owed — which makes the
/// one number the owner cares most about wrong.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OpeningBalanceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public OpeningBalanceTests(ApiFactory api) => _api = api;

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

    private async Task<long> CreateCustomerAsync()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO customers (name, mobile_number, outstanding_balance, is_active, created_at_utc)
            VALUES (@name, '923001234567', 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Op {Guid.NewGuid():N}"[..18] });
    }

    private async Task<long> CreateProductAsync(decimal salePrice = 3000m, int quantity = 50)
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
            new { name = $"Ob {Guid.NewGuid():N}"[..20], salePrice, quantity });
    }

    private async Task<decimal> BalanceAsync(long customerId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT outstanding_balance FROM customers WHERE id = @customerId;", new { customerId });
    }

    private sealed record LedgerRow(string EntryType, decimal BillAmount, decimal PaidAmount,
        decimal BalanceAfter, string? Note);

    private async Task<IReadOnlyList<LedgerRow>> LedgerAsync(long customerId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return (await connection.QueryAsync<LedgerRow>(
            """
            SELECT entry_type AS EntryType, bill_amount AS BillAmount, paid_amount AS PaidAmount,
                   balance_after AS BalanceAfter, note AS Note
            FROM ledger_entries WHERE customer_id = @customerId ORDER BY id;
            """,
            new { customerId })).AsList();
    }

    private static Task<HttpResponseMessage> SetAsync(
        HttpClient client, long customerId, decimal amount, string? reason = null) =>
        client.PutAsJsonAsync($"/api/customers/{customerId}/opening-balance", new { amount, reason });

    // ---------------------------------------------------------------- first recording

    [Fact]
    public async Task The_owner_can_record_what_a_customer_already_owed()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        var response = await SetAsync(admin, customerId, 12_000m);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "Server said: {0}", await response.Content.ReadAsStringAsync());

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        data.GetProperty("openingBalance").GetDecimal().Should().Be(12_000m);
        data.GetProperty("outstandingBalance").GetDecimal().Should().Be(12_000m);
        data.GetProperty("wasCorrection").GetBoolean().Should().BeFalse();
        data.GetProperty("previousOpeningBalance").ValueKind.Should().Be(JsonValueKind.Null);

        (await BalanceAsync(customerId)).Should().Be(12_000m);
    }

    [Fact]
    public async Task It_appears_in_the_ledger_as_brought_forward_not_as_a_sale()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        await SetAsync(admin, customerId, 12_000m);

        var ledger = await LedgerAsync(customerId);

        // FR-067: distinguishable from a sale, a payment or a return, and first.
        ledger.Should().ContainSingle();
        ledger[0].EntryType.Should().Be("OpeningBalance");
        ledger[0].BillAmount.Should().Be(12_000m);
        ledger[0].PaidAmount.Should().Be(0m);
        ledger[0].BalanceAfter.Should().Be(12_000m);
    }

    [Fact]
    public async Task It_counts_towards_what_the_shop_is_owed()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        await SetAsync(admin, customerId, 12_000m);

        var receivables = await admin.GetFromJsonAsync<Envelope<JsonElement>>(
            "/api/reports/receivables", Json);

        var row = receivables!.Data!.EnumerateArray()
            .Single(r => r.GetProperty("customerId").GetInt64() == customerId);

        // FR-066. Without this the owner's headline receivables figure is simply wrong.
        row.GetProperty("outstandingBalance").GetDecimal().Should().Be(12_000m);
    }

    [Fact]
    public async Task A_recorded_zero_is_accepted()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        var response = await SetAsync(admin, customerId, 0m);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BalanceAsync(customerId)).Should().Be(0m);
    }

    // ---------------------------------------------------------------- it behaves like real money

    [Fact]
    public async Task It_behaves_like_money_owed_once_recorded()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(salePrice: 3000m);

        await SetAsync(admin, customerId, 12_000m);

        // Sold 3,000 on credit — only the owner may (FR-051).
        var sale = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 0m,
            paymentMethod = "Credit",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 3000m, lineDiscount = 0m } },
        });

        sale.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(customerId)).Should().Be(15_000m);

        // Paid 5,000.
        var payment = await admin.PostAsJsonAsync(
            $"/api/customers/{customerId}/payments", new { amount = 5_000m, paymentMethod = "Cash" });

        payment.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(customerId)).Should().Be(10_000m);

        var ledger = await LedgerAsync(customerId);

        ledger.Select(e => e.EntryType).Should()
            .ContainInOrder("OpeningBalance", "Invoice", "Payment");

        // SC-020: reading the register top to bottom reproduces the balance.
        ledger.Sum(e => e.BillAmount - e.PaidAmount).Should().Be(10_000m);
        ledger[^1].BalanceAfter.Should().Be(10_000m);
    }

    // ---------------------------------------------------------------- corrections

    [Fact]
    public async Task Recording_it_again_corrects_the_figure_and_never_doubles_the_debt()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        await SetAsync(admin, customerId, 12_000m);

        var response = await SetAsync(
            admin, customerId, 10_000m, reason: "Mistyped from the register.");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        data.GetProperty("wasCorrection").GetBoolean().Should().BeTrue();
        data.GetProperty("previousOpeningBalance").GetDecimal().Should().Be(12_000m);
        data.GetProperty("openingBalance").GetDecimal().Should().Be(10_000m);

        // FR-070, the whole point: down by the 2,000 difference, NOT up by another 10,000.
        (await BalanceAsync(customerId)).Should().Be(10_000m);
        (await BalanceAsync(customerId)).Should().NotBe(22_000m);
    }

    [Fact]
    public async Task A_correction_leaves_the_original_entry_untouched()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        await SetAsync(admin, customerId, 12_000m);
        await SetAsync(admin, customerId, 10_000m, reason: "Mistyped from the register.");

        var ledger = await LedgerAsync(customerId);

        // FR-071: the register is a record of what happened, so a correction is appended rather
        // than rewriting history.
        ledger.Should().HaveCount(2);

        ledger[0].EntryType.Should().Be("OpeningBalance");
        ledger[0].BillAmount.Should().Be(12_000m, "the original entry must not be altered");

        ledger[1].EntryType.Should().Be("Adjustment");
        ledger[1].PaidAmount.Should().Be(2_000m, "a reduction of 2,000");
        ledger[1].BalanceAfter.Should().Be(10_000m);
        ledger[1].Note.Should().Contain("Mistyped");
    }

    [Fact]
    public async Task A_correction_upward_is_recorded_as_a_bill()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        await SetAsync(admin, customerId, 10_000m);
        await SetAsync(admin, customerId, 12_000m, reason: "Found another page in the register.");

        var ledger = await LedgerAsync(customerId);

        ledger[1].EntryType.Should().Be("Adjustment");
        ledger[1].BillAmount.Should().Be(2_000m);
        ledger[1].PaidAmount.Should().Be(0m);

        (await BalanceAsync(customerId)).Should().Be(12_000m);
    }

    [Fact]
    public async Task A_correction_after_trading_moves_only_the_opening_figure()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();
        var productId = await CreateProductAsync(salePrice: 3000m);

        await SetAsync(admin, customerId, 12_000m);

        await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 0m,
            paymentMethod = "Credit",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 3000m, lineDiscount = 0m } },
        });

        (await BalanceAsync(customerId)).Should().Be(15_000m);

        await SetAsync(admin, customerId, 10_000m, reason: "Mistyped from the register.");

        // The sale is untouched; only the carried-forward part moved, by exactly 2,000.
        (await BalanceAsync(customerId)).Should().Be(13_000m);
    }

    [Fact]
    public async Task Re_recording_the_same_figure_changes_nothing()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        await SetAsync(admin, customerId, 12_000m);
        await SetAsync(admin, customerId, 12_000m, reason: "Confirming against the register.");

        (await BalanceAsync(customerId)).Should().Be(12_000m);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task A_negative_amount_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        var response = await SetAsync(admin, customerId, -500m);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BalanceAsync(customerId)).Should().Be(0m);
    }

    [Fact]
    public async Task A_correction_without_a_reason_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        await SetAsync(admin, customerId, 12_000m);

        var response = await SetAsync(admin, customerId, 10_000m);

        // 422, not 400: whether a reason is required depends on whether a figure already exists,
        // which only the service knows. A request validator sees the shape, not the state.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // And nothing moved.
        (await BalanceAsync(customerId)).Should().Be(12_000m);
    }

    [Fact]
    public async Task A_salesman_cannot_record_or_change_one()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var staff = await ClientAsync(UserRole.Staff);
        var customerId = await CreateCustomerAsync();

        // FR-068. This is a statement about money owed, not a sale.
        (await SetAsync(staff, customerId, 12_000m))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await SetAsync(admin, customerId, 12_000m);

        (await SetAsync(staff, customerId, 1m, reason: "Nope"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await BalanceAsync(customerId)).Should().Be(12_000m);
    }

    [Fact]
    public async Task An_unknown_customer_is_not_found()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await SetAsync(admin, 999_999_999L, 12_000m);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- audit and the clean case

    [Fact]
    public async Task Recording_and_correcting_are_both_audited()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var customerId = await CreateCustomerAsync();

        await SetAsync(admin, customerId, 12_000m);
        await SetAsync(admin, customerId, 10_000m, reason: "Mistyped from the register.");

        await using var connection = await _api.OpenDatabaseAsync();

        var rows = (await connection.QueryAsync<(string? OldValue, string NewValue, long UserId)>(
            """
            SELECT old_value AS OldValue, new_value AS NewValue, user_id AS UserId
            FROM audit_entries
            WHERE entity_type = 'Customer' AND entity_id = @customerId
              AND field_name = 'opening_balance'
            ORDER BY id;
            """,
            new { customerId })).AsList();

        // SC-021: every carried-forward figure traceable to who entered it.
        rows.Should().HaveCount(2);
        rows[0].OldValue.Should().BeNull();
        rows[0].NewValue.Should().Contain("12000");
        rows[1].OldValue.Should().Contain("12000");
        rows[1].NewValue.Should().Contain("10000");
        rows.Should().OnlyContain(r => r.UserId > 0);
    }

    [Fact]
    public async Task A_customer_with_no_history_starts_clean()
    {
        var customerId = await CreateCustomerAsync();

        await using var connection = await _api.OpenDatabaseAsync();

        // FR-073: no opening entry invented for a customer who never had one.
        (await connection.ExecuteScalarAsync<decimal?>(
            "SELECT opening_balance FROM customers WHERE id = @customerId;", new { customerId }))
            .Should().BeNull();

        (await BalanceAsync(customerId)).Should().Be(0m);
        (await LedgerAsync(customerId)).Should().BeEmpty();
    }
}
