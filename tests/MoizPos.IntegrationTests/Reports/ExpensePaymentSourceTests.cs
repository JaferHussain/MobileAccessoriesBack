using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Reports;

/// <summary>
/// Where an expense's money came from.
///
/// <para>Migration 0022 added the column, but nothing wrote to it — so every expense saved as
/// NULL, was excluded from the drawer, and "Cash paid out" read zero however much was taken out
/// of the till. The drawer then looked short by exactly the amount that legitimately left it.</para>
///
/// <para><b>Required at the validator, not only on the form.</b> A required dropdown is a
/// frontend-only check, and this system never relies on one.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ExpensePaymentSourceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ExpensePaymentSourceTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message);

    private async Task<HttpClient> ClientAsync(UserRole role = UserRole.Admin)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<long> CategoryAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/expense-categories", new
        {
            name = $"Cat {Guid.NewGuid():N}"[..16],
        });

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();
    }

    private async Task<HttpResponseMessage> RecordAsync(
        HttpClient admin, long categoryId, string? paymentSource, decimal amount = 300m)
    {
        return await admin.PostAsJsonAsync("/api/expenses", new
        {
            categoryId,
            amount,
            expenseDate = DateTime.UtcNow,
            note = "tea",
            paymentSource,
        });
    }

    [Fact]
    public async Task An_expense_records_where_the_money_came_from()
    {
        var admin = await ClientAsync();

        var response = await RecordAsync(admin, await CategoryAsync(admin), "Till");
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        await using var connection = await _api.OpenDatabaseAsync();

        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT payment_source FROM expenses WHERE id = @id;", new { id });

        stored.Should().Be("Till");
    }

    [Fact]
    public async Task An_expense_paid_from_the_bank_records_that_instead()
    {
        var admin = await ClientAsync();

        var response = await RecordAsync(admin, await CategoryAsync(admin), "Bank");

        var id = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        await using var connection = await _api.OpenDatabaseAsync();

        (await connection.ExecuteScalarAsync<string>(
            "SELECT payment_source FROM expenses WHERE id = @id;", new { id }))
            .Should().Be("Bank");
    }

    [Fact]
    public async Task An_expense_with_no_source_is_refused_by_the_server()
    {
        var admin = await ClientAsync();

        // A required dropdown is a frontend-only check. The rule has to live where a caller
        // cannot route around it.
        var response = await RecordAsync(admin, await CategoryAsync(admin), paymentSource: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_refused_expense_is_not_recorded_at_all()
    {
        var admin = await ClientAsync();

        await using var connection = await _api.OpenDatabaseAsync();
        var before = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM expenses;");

        await RecordAsync(admin, await CategoryAsync(admin), paymentSource: null);

        var after = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM expenses;");

        after.Should().Be(before);
    }

    [Fact]
    public async Task A_source_that_is_not_one_of_the_two_is_refused()
    {
        var admin = await ClientAsync();

        var response = await RecordAsync(admin, await CategoryAsync(admin), "Pocket");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_expense_list_says_where_each_one_came_from()
    {
        var admin = await ClientAsync();

        await RecordAsync(admin, await CategoryAsync(admin), "Till", amount: 777m);

        var response = await admin.GetAsync("/api/expenses?pageSize=100");
        var page = (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        var row = page.GetProperty("items").EnumerateArray()
            .First(e => e.GetProperty("amount").GetDecimal() == 777m);

        row.GetProperty("paymentSource").GetString().Should().Be("Till");
    }

    /// <summary>
    /// The point of the whole thing: money out of the till has to reach the drawer calculation,
    /// or the drawer reads short by exactly the amount that legitimately left it.
    /// </summary>
    [Fact]
    public async Task An_expense_from_the_till_reaches_the_drawer_calculation()
    {
        var admin = await ClientAsync();
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi")));

        var date = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var before = await PaidOutAsync(admin, date);

        await RecordAsync(admin, await CategoryAsync(admin), "Till", amount: 250m);

        (await PaidOutAsync(admin, date)).Should().Be(before + 250m);
    }

    [Fact]
    public async Task An_expense_from_the_bank_never_touches_the_drawer()
    {
        var admin = await ClientAsync();
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi")));

        var date = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var before = await PaidOutAsync(admin, date);

        await RecordAsync(admin, await CategoryAsync(admin), "Bank", amount: 250m);

        (await PaidOutAsync(admin, date)).Should().Be(before);
    }

    // ================================================================
    //  The floor under the rule (migration 0024)
    // ================================================================

    [Fact]
    public async Task The_column_itself_refuses_an_expense_with_no_source()
    {
        await using var connection = await _api.OpenDatabaseAsync();

        var isNullable = await connection.ExecuteScalarAsync<string>(
            """
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = DATABASE()
              AND table_name = 'expenses'
              AND column_name = 'payment_source';
            """);

        // The validator is the rule; this is the floor under it. An expense with no source is
        // invisible to the drawer count, so the drawer reads short by exactly the amount that
        // legitimately left the till.
        isNullable.Should().Be("NO");
    }

    /// <summary>
    /// What the column actually guarantees — measured on MySQL 8.0.40 with STRICT_ALL_TABLES on.
    ///
    /// <para><b>An explicit NULL is refused.</b> An OMITTED column is <b>not</b>: MySQL treats an
    /// ENUM's first member as an implicit default and stores it without error, even in strict
    /// mode. That cannot be switched off, so migration 0025 chose what it lands on.</para>
    /// </summary>
    [Fact]
    public async Task The_database_refuses_an_explicit_null()
    {
        var admin = await ClientAsync();
        var categoryId = await CategoryAsync(admin);

        await using var connection = await _api.OpenDatabaseAsync();

        var explicitNull = async () => await connection.ExecuteAsync(
            """
            INSERT INTO expenses
                (category_id, amount, payment_source, expense_date_utc, note, user_id, created_at_utc)
            VALUES (@categoryId, 100, NULL, UTC_TIMESTAMP(6), 'explicit null',
                    (SELECT id FROM users ORDER BY id LIMIT 1), UTC_TIMESTAMP(6));
            """,
            new { categoryId });

        await explicitNull.Should().ThrowAsync<Exception>();
    }

    /// <summary>
    /// The one that protects the drawer.
    ///
    /// <para>A write that forgets this column cannot be stopped, so it must not land on 'Till'.
    /// If it did, day close would subtract money that never left the till and report a short the
    /// salesman cannot account for. 'Bank' is excluded from the reconciliation instead — the same
    /// treatment the old NULL got.</para>
    ///
    /// <para>If this ever reddens, the ENUM order was changed back and expenses are being booked
    /// as cash by accident.</para>
    /// </summary>
    [Fact]
    public async Task A_write_that_forgets_the_source_never_lands_on_cash()
    {
        var admin = await ClientAsync();
        var categoryId = await CategoryAsync(admin);
        var note = $"omitted {Guid.NewGuid():N}"[..20];

        await using var connection = await _api.OpenDatabaseAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO expenses
                (category_id, amount, expense_date_utc, note, user_id, created_at_utc)
            VALUES (@categoryId, 100, UTC_TIMESTAMP(6), @note,
                    (SELECT id FROM users ORDER BY id LIMIT 1), UTC_TIMESTAMP(6));
            """,
            new { categoryId, note });

        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT payment_source FROM expenses WHERE note = @note;", new { note });

        stored.Should().Be("Bank", "a forgotten source must not be deducted from the drawer");
    }

    [Fact]
    public async Task Both_sources_still_read_back_as_they_were_written()
    {
        var admin = await ClientAsync();

        // Reordering the ENUM members rebuilds the table. If MySQL had re-mapped by stored index
        // rather than by string, every Till would now read as Bank.
        var till = await RecordAsync(admin, await CategoryAsync(admin), "Till", amount: 611m);
        var bank = await RecordAsync(admin, await CategoryAsync(admin), "Bank", amount: 612m);

        await using var connection = await _api.OpenDatabaseAsync();

        var tillId = (await till.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();
        var bankId = (await bank.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        (await connection.ExecuteScalarAsync<string>(
            "SELECT payment_source FROM expenses WHERE id = @tillId;", new { tillId }))
            .Should().Be("Till");

        (await connection.ExecuteScalarAsync<string>(
            "SELECT payment_source FROM expenses WHERE id = @bankId;", new { bankId }))
            .Should().Be("Bank");
    }

    private async Task<decimal> PaidOutAsync(HttpClient admin, string date)
    {
        var response = await admin.GetAsync($"/api/day-closings/preview?date={date}&openingFloat=0");

        return (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("cashPaidOut").GetDecimal();
    }
}
