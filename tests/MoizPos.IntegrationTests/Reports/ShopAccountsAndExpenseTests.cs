using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Reports;

/// <summary>
/// The shop's own accounts, the expense categories the owner keeps, and one plain question for how
/// an expense was paid.
///
/// <para>The owner registers each bank or wallet account once; after that a non-cash expense or
/// supplier payment says which account it left, picked from a list. Nothing is ever hard-deleted —
/// a retired account or category stops being offered, and everything recorded against it keeps
/// its label.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ShopAccountsAndExpenseTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public ShopAccountsAndExpenseTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private async Task<HttpClient> SignedInAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var data = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());

        return client;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..(prefix.Length + 9)];

    private static async Task<long> AccountAsync(HttpClient admin, string type, string? name = null)
    {
        var response = await admin.PostAsJsonAsync("/api/shop-accounts", new
        {
            name = name ?? Unique(type),
            accountType = type,
            accountNumber = "0300 7194095",
            accountTitle = "Moiz Mobile & Corporation",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await DataAsync(response)).GetProperty("id").GetInt64();
    }

    private static async Task<long> CategoryAsync(HttpClient admin, string? name = null)
    {
        var response = await admin.PostAsJsonAsync("/api/expense-categories", new { name = name ?? Unique("Cat") });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await DataAsync(response)).GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> ExpenseAsync(
        HttpClient admin, long categoryId, string paymentMethod, long? shopAccountId = null, string? transactionId = null) =>
        admin.PostAsJsonAsync("/api/expenses", new
        {
            categoryId,
            amount = 1_500m,
            expenseDate = DateTime.UtcNow,
            paymentMethod,
            shopAccountId,
            transactionId,
        });

    private static async Task<JsonElement> ListedExpenseAsync(HttpClient admin, long id) =>
        (await DataAsync(await admin.GetAsync("/api/expenses?pageSize=100")))
            .GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("id").GetInt64() == id);

    private static string KarachiToday() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ================================================================
    //  Shop accounts
    // ================================================================

    [Fact]
    public async Task The_owner_registers_an_account_once_and_it_is_listed()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var name = Unique("HBL");
        var id = await AccountAsync(admin, "Bank", name);

        var listed = (await DataAsync(await admin.GetAsync("/api/shop-accounts")))
            .EnumerateArray().Single(account => account.GetProperty("id").GetInt64() == id);

        listed.GetProperty("name").GetString().Should().Be(name);
        listed.GetProperty("accountType").GetString().Should().Be("Bank");
        listed.GetProperty("accountNumber").GetString().Should().Be("0300 7194095");
        listed.GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Two_accounts_cannot_share_a_name()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var name = Unique("Jazz");
        await AccountAsync(admin, "JazzCash", name);

        var again = await admin.PostAsJsonAsync("/api/shop-accounts", new { name, accountType = "JazzCash" });

        again.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_retired_account_is_hidden_but_never_deleted_and_can_come_back()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var id = await AccountAsync(admin, "EasyPaisa");

        (await admin.DeleteAsync($"/api/shop-accounts/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var active = (await DataAsync(await admin.GetAsync("/api/shop-accounts"))).EnumerateArray();
        active.Should().NotContain(account => account.GetProperty("id").GetInt64() == id);

        var all = (await DataAsync(await admin.GetAsync("/api/shop-accounts?includeInactive=true"))).EnumerateArray();
        all.Should().Contain(account => account.GetProperty("id").GetInt64() == id
            && !account.GetProperty("isActive").GetBoolean());

        (await admin.PostAsync($"/api/shop-accounts/{id}/reactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_account_can_be_renamed_and_its_number_corrected()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var id = await AccountAsync(admin, "Bank");
        var name = Unique("Meezan");

        var updated = await admin.PutAsJsonAsync($"/api/shop-accounts/{id}", new
        {
            name,
            accountType = "Bank",
            accountNumber = "9989",
            accountTitle = (string?)null,
        });

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DataAsync(updated)).GetProperty("accountNumber").GetString().Should().Be("9989");
    }

    [Fact]
    public async Task Shop_accounts_are_the_owners_alone()
    {
        var staff = await SignedInAsync(UserRole.Staff);

        (await staff.GetAsync("/api/shop-accounts")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ================================================================
    //  Expense categories
    // ================================================================

    [Fact]
    public async Task The_owner_adds_renames_and_hides_expense_categories()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var id = await CategoryAsync(admin);
        var renamed = Unique("Tea");

        (await admin.PutAsJsonAsync($"/api/expense-categories/{id}", new { name = renamed }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.DeleteAsync($"/api/expense-categories/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Hidden from the dropdown…
        (await DataAsync(await admin.GetAsync("/api/expense-categories"))).EnumerateArray()
            .Should().NotContain(category => category.GetProperty("id").GetInt64() == id);

        // …but kept, with its new name, and can come back.
        (await DataAsync(await admin.GetAsync("/api/expense-categories?includeInactive=true"))).EnumerateArray()
            .Should().Contain(category => category.GetProperty("id").GetInt64() == id
                && category.GetProperty("name").GetString() == renamed);

        (await admin.PostAsync($"/api/expense-categories/{id}/reactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_hidden_category_keeps_its_label_on_old_expenses_and_cannot_take_new_ones()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var name = Unique("Paint");
        var categoryId = await CategoryAsync(admin, name);
        var expenseId = (await DataAsync(await ExpenseAsync(admin, categoryId, "Cash"))).GetProperty("id").GetInt64();

        await admin.DeleteAsync($"/api/expense-categories/{categoryId}");

        (await ListedExpenseAsync(admin, expenseId)).GetProperty("categoryName").GetString().Should().Be(name);
        (await ExpenseAsync(admin, categoryId, "Cash")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Two_categories_cannot_share_a_name()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var name = Unique("Rent2");
        await CategoryAsync(admin, name);

        (await admin.PostAsJsonAsync("/api/expense-categories", new { name })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ================================================================
    //  One question: how was it paid?
    // ================================================================

    [Fact]
    public async Task A_cash_expense_is_taken_from_the_till()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var categoryId = await CategoryAsync(admin);

        async Task<decimal> CashPaidOutAsync() =>
            (await DataAsync(await admin.GetAsync($"/api/day-closings/preview?date={KarachiToday()}")))
                .GetProperty("cashPaidOut").GetDecimal();

        var before = await CashPaidOutAsync();

        var created = await ExpenseAsync(admin, categoryId, "Cash");
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        (await CashPaidOutAsync()).Should().Be(before + 1_500m, "cash came out of the drawer");

        var listed = await ListedExpenseAsync(admin, (await DataAsync(created)).GetProperty("id").GetInt64());
        listed.GetProperty("paymentSource").GetString().Should().Be("Till");
    }

    [Fact]
    public async Task A_JazzCash_expense_is_paid_from_the_bank_side_and_names_its_account_and_reference()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var categoryId = await CategoryAsync(admin);
        var accountName = Unique("Jazz");
        var accountId = await AccountAsync(admin, "JazzCash", accountName);

        async Task<decimal> CashPaidOutAsync() =>
            (await DataAsync(await admin.GetAsync($"/api/day-closings/preview?date={KarachiToday()}")))
                .GetProperty("cashPaidOut").GetDecimal();

        var before = await CashPaidOutAsync();

        var created = await ExpenseAsync(admin, categoryId, "JazzCash", accountId, "TXN 5521");
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        (await CashPaidOutAsync()).Should().Be(before, "a JazzCash payment never left the till");

        var listed = await ListedExpenseAsync(admin, (await DataAsync(created)).GetProperty("id").GetInt64());
        listed.GetProperty("paymentSource").GetString().Should().Be("Bank");
        listed.GetProperty("paymentMethod").GetString().Should().Be("JazzCash");
        listed.GetProperty("shopAccountName").GetString().Should().Be(accountName);
        listed.GetProperty("transactionId").GetString().Should().Be("TXN 5521");
    }

    [Fact]
    public async Task The_account_is_optional()
    {
        var admin = await SignedInAsync(UserRole.Admin);

        (await ExpenseAsync(admin, await CategoryAsync(admin), "BankTransfer")).StatusCode
            .Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_payment_cannot_name_an_account_that_could_not_have_carried_it()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var bank = await AccountAsync(admin, "Bank");

        (await ExpenseAsync(admin, await CategoryAsync(admin), "JazzCash", bank)).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_retired_account_takes_no_new_payments()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var account = await AccountAsync(admin, "EasyPaisa");
        await admin.DeleteAsync($"/api/shop-accounts/{account}");

        (await ExpenseAsync(admin, await CategoryAsync(admin), "EasyPaisa", account)).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Cash_has_no_account_and_no_reference()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var categoryId = await CategoryAsync(admin);

        (await ExpenseAsync(admin, categoryId, "Cash", transactionId: "X1")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await ExpenseAsync(admin, categoryId, "Cash", await AccountAsync(admin, "Bank"))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Credit")]
    [InlineData("Partial")]
    public async Task An_expense_is_paid_or_it_is_not_an_expense(string method)
    {
        var admin = await SignedInAsync(UserRole.Admin);

        (await ExpenseAsync(admin, await CategoryAsync(admin), method)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    // ================================================================
    //  Supplier payments
    // ================================================================

    [Fact]
    public async Task A_supplier_payment_names_the_account_it_left_and_the_ledger_shows_it()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var accountName = Unique("HBL");
        var accountId = await AccountAsync(admin, "Bank", accountName);

        var supplier = await admin.PostAsJsonAsync("/api/suppliers", new { name = Unique("Sup") });
        var supplierId = (await DataAsync(supplier)).GetProperty("id").GetInt64();

        var paid = await admin.PostAsJsonAsync($"/api/suppliers/{supplierId}/payments", new
        {
            amount = 700m,
            paymentMethod = "BankTransfer",
            shopAccountId = accountId,
            confirmOverpayment = true,
        });

        paid.StatusCode.Should().Be(HttpStatusCode.OK);

        var line = (await DataAsync(await admin.GetAsync($"/api/suppliers/{supplierId}/ledger")))
            .GetProperty("entries").EnumerateArray().Single();

        line.GetProperty("shopAccountName").GetString().Should().Be(accountName);
    }

    [Fact]
    public async Task A_supplier_payment_cannot_name_an_account_that_could_not_have_carried_it()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var jazz = await AccountAsync(admin, "JazzCash");

        var supplier = await admin.PostAsJsonAsync("/api/suppliers", new { name = Unique("Sup") });
        var supplierId = (await DataAsync(supplier)).GetProperty("id").GetInt64();

        (await admin.PostAsJsonAsync($"/api/suppliers/{supplierId}/payments", new
        {
            amount = 700m,
            paymentMethod = "BankTransfer",
            shopAccountId = jazz,
            confirmOverpayment = true,
        })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
