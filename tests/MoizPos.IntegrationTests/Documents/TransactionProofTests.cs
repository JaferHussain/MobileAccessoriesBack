using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace MoizPos.IntegrationTests.Documents;

/// <summary>
/// The owner's rule: <b>every transaction except cash is kept with its proof</b> — a sale, udhaar
/// recovered, a supplier paid, a refund handed back, a bank expense.
///
/// <para>Optional when the transaction is saved, so the counter never waits on a screenshot; the
/// "Proof missing" list is how the owner makes sure none is forgotten.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransactionProofTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public TransactionProofTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    // ================================================================
    //  Helpers
    // ================================================================

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

    private static MultipartFormDataContent Screenshot()
    {
        using var image = new Image<Rgba32>(300, 500);
        var bytes = new MemoryStream();
        image.Save(bytes, new JpegEncoder());

        var file = new ByteArrayContent(bytes.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        return new MultipartFormDataContent { { file, "file", "transfer.jpg" } };
    }

    private static Task<HttpResponseMessage> AttachAsync(HttpClient client, string kind, long id) =>
        client.PostAsync($"/api/proofs/{kind}/{id}", Screenshot());

    private async Task<long> ProductAsync()
    {
        var (categoryId, brandId) = await _api.EnsureCatalogueAsync();
        await using var connection = await _api.OpenDatabaseAsync();

        var productId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO products
                (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
            VALUES (@name, @categoryId, @brandId, 0, 0, 0, 0, 3, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name = $"Proof {Guid.NewGuid():N}"[..20], categoryId, brandId });

        await _api.StockProductAsync(productId, quantity: 50, costPrice: 400m, salePrice: 1000m);

        return productId;
    }

    private async Task<long> CustomerAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/customers", new
        {
            name = $"Asif {Guid.NewGuid():N}"[..14],
            mobileNumber = "03001234567",
        });

        var id = (await DataAsync(response)).GetProperty("id").GetInt64();
        await _api.MarkUdhaarAsync(id);

        return id;
    }

    private async Task<long> SaleAsync(HttpClient admin, long? customerId, decimal amountPaid, string paymentMethod)
    {
        var response = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid,
            paymentMethod,
            items = new[] { new { productId = await ProductAsync(), quantity = 1, unitSalePrice = 1000m, lineDiscount = 0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await DataAsync(response)).GetProperty("invoiceId").GetInt64();
    }

    private static async Task<long> RecoveryAsync(HttpClient admin, long customerId, decimal amount, string method)
    {
        var response = await admin.PostAsJsonAsync($"/api/customers/{customerId}/payments", new
        {
            amount,
            paymentMethod = method,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await DataAsync(response)).GetProperty("paymentId").GetInt64();
    }

    private async Task<long> InvoiceItemAsync(long invoiceId)
    {
        await using var connection = await _api.OpenDatabaseAsync();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM invoice_items WHERE invoice_id = @invoiceId LIMIT 1;", new { invoiceId });
    }

    private async Task<HttpResponseMessage> ReturnAsync(HttpClient admin, long invoiceId, string? refundMethod) =>
        await admin.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId,
            refundMethod,
            items = new[] { new { invoiceItemId = await InvoiceItemAsync(invoiceId), quantity = 1 } },
        });

    private static async Task<long> SupplierPaymentAsync(HttpClient admin, string method)
    {
        var supplier = await admin.PostAsJsonAsync("/api/suppliers", new { name = $"Sup {Guid.NewGuid():N}"[..14] });
        var supplierId = (await DataAsync(supplier)).GetProperty("id").GetInt64();

        var response = await admin.PostAsJsonAsync($"/api/suppliers/{supplierId}/payments", new
        {
            amount = 500m,
            paymentMethod = method,
            confirmOverpayment = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await DataAsync(response)).GetProperty("paymentId").GetInt64();
    }

    private static async Task<HttpResponseMessage> ExpenseAsync(HttpClient admin, string source, string? method)
    {
        var categories = await admin.GetAsync("/api/expense-categories");
        var categoryId = (await DataAsync(categories)).EnumerateArray().First().GetProperty("id").GetInt64();

        return await admin.PostAsJsonAsync("/api/expenses", new
        {
            categoryId,
            amount = 2_500m,
            expenseDate = DateTime.UtcNow,
            paymentSource = source,
            paymentMethod = method,
        });
    }

    private static async Task<IReadOnlyList<JsonElement>> MissingAsync(HttpClient admin) =>
        (await DataAsync(await admin.GetAsync("/api/proofs/missing"))).EnumerateArray().ToList();

    private static string KarachiToday() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ================================================================
    //  Attaching and viewing
    // ================================================================

    [Fact]
    public async Task Udhaar_recovered_by_transfer_takes_a_proof_that_can_be_opened_again()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);
        await SaleAsync(admin, customerId, amountPaid: 0m, paymentMethod: "Credit");
        var paymentId = await RecoveryAsync(admin, customerId, 400m, "JazzCash");

        (await AttachAsync(admin, "customer-payment", paymentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var viewed = await admin.GetAsync($"/api/proofs/customer-payment/{paymentId}");

        viewed.StatusCode.Should().Be(HttpStatusCode.OK);
        viewed.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await viewed.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Cash_is_refused_a_proof()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);
        await SaleAsync(admin, customerId, amountPaid: 0m, paymentMethod: "Credit");
        var paymentId = await RecoveryAsync(admin, customerId, 400m, "Cash");

        (await AttachAsync(admin, "customer-payment", paymentId)).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_sale_proof_can_be_attached_and_viewed_through_the_same_route()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin, null, amountPaid: 1000m, paymentMethod: "BankTransfer");

        (await AttachAsync(admin, "sale", invoiceId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/proofs/sale/{invoiceId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_supplier_payment_proof_is_the_owners_alone()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var paymentId = await SupplierPaymentAsync(admin, "BankTransfer");
        var staff = await SignedInAsync(UserRole.Staff);

        (await AttachAsync(staff, "supplier-payment", paymentId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.GetAsync($"/api/proofs/supplier-payment/{paymentId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await AttachAsync(admin, "supplier-payment", paymentId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_transaction_with_no_proof_yet_has_nothing_to_open()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin, null, amountPaid: 1000m, paymentMethod: "EasyPaisa");

        (await admin.GetAsync($"/api/proofs/sale/{invoiceId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/proofs/purchase/{invoiceId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ================================================================
    //  Refunds
    // ================================================================

    [Fact]
    public async Task A_refund_must_say_how_it_was_handed_back()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var invoiceId = await SaleAsync(admin, null, amountPaid: 1000m, paymentMethod: "Cash");

        // Assuming Cash is exactly what showed a JazzCash refund as the drawer running OVER.
        (await ReturnAsync(admin, invoiceId, refundMethod: null)).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);

        var returned = await ReturnAsync(admin, invoiceId, refundMethod: "JazzCash");
        returned.StatusCode.Should().Be(HttpStatusCode.Created);

        var returnId = (await DataAsync(returned)).GetProperty("returnId").GetInt64();
        (await AttachAsync(admin, "refund", returnId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_return_that_refunded_nothing_has_nothing_to_prove()
    {
        // Against unpaid udhaar the return only reduces what is owed — no money moves.
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);
        var invoiceId = await SaleAsync(admin, customerId, amountPaid: 0m, paymentMethod: "Credit");

        var returned = await ReturnAsync(admin, invoiceId, refundMethod: null);
        returned.StatusCode.Should().Be(HttpStatusCode.Created, "nothing was refunded, so no method is asked");

        var returnId = (await DataAsync(returned)).GetProperty("returnId").GetInt64();
        (await AttachAsync(admin, "refund", returnId)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Only_a_cash_refund_comes_out_of_the_drawer()
    {
        var admin = await SignedInAsync(UserRole.Admin);

        async Task<decimal> CashRefundsAsync() =>
            (await DataAsync(await admin.GetAsync($"/api/day-closings/preview?date={KarachiToday()}")))
                .GetProperty("cashRefunds").GetDecimal();

        var before = await CashRefundsAsync();

        (await ReturnAsync(admin, await SaleAsync(admin, null, 1000m, "Cash"), "Raast")).StatusCode
            .Should().Be(HttpStatusCode.Created);
        (await CashRefundsAsync()).Should().Be(before, "a Raast refund never left the till");

        (await ReturnAsync(admin, await SaleAsync(admin, null, 1000m, "Cash"), "Cash")).StatusCode
            .Should().Be(HttpStatusCode.Created);
        (await CashRefundsAsync()).Should().Be(before + 1000m);
    }

    // ================================================================
    //  Expenses
    // ================================================================

    [Fact]
    public async Task A_bank_expense_records_how_it_went_and_takes_a_proof()
    {
        var admin = await SignedInAsync(UserRole.Admin);

        var created = await ExpenseAsync(admin, "Bank", "BankTransfer");
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var expenseId = (await DataAsync(created)).GetProperty("id").GetInt64();

        (await AttachAsync(admin, "expense", expenseId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var listed = (await DataAsync(await admin.GetAsync("/api/expenses?pageSize=100")))
            .GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("id").GetInt64() == expenseId);

        listed.GetProperty("paymentMethod").GetString().Should().Be("BankTransfer");
        listed.GetProperty("hasProof").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_till_expense_was_cash_so_it_has_no_method_and_no_proof()
    {
        var admin = await SignedInAsync(UserRole.Admin);

        (await ExpenseAsync(admin, "Till", "JazzCash")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var created = await ExpenseAsync(admin, "Till", null);
        var expenseId = (await DataAsync(created)).GetProperty("id").GetInt64();

        (await AttachAsync(admin, "expense", expenseId)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ================================================================
    //  Proof missing
    // ================================================================

    [Fact]
    public async Task Proof_missing_lists_what_is_owed_a_proof_until_it_is_attached()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);
        await SaleAsync(admin, customerId, amountPaid: 0m, paymentMethod: "Credit");

        var transfer = await RecoveryAsync(admin, customerId, 300m, "EasyPaisa");
        var cash = await RecoveryAsync(admin, customerId, 200m, "Cash");

        bool Listed(IReadOnlyList<JsonElement> rows, long id) => rows.Any(row =>
            row.GetProperty("kind").GetString() == "CustomerPayment" && row.GetProperty("referenceId").GetInt64() == id);

        var missing = await MissingAsync(admin);
        Listed(missing, transfer).Should().BeTrue();
        Listed(missing, cash).Should().BeFalse("cash is its own proof");

        await AttachAsync(admin, "customer-payment", transfer);

        Listed(await MissingAsync(admin), transfer).Should().BeFalse("its proof is now attached");
    }

    [Fact]
    public async Task Proof_missing_is_the_owners_list()
    {
        var staff = await SignedInAsync(UserRole.Staff);

        (await staff.GetAsync("/api/proofs/missing")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ================================================================
    //  Where proofs show
    // ================================================================

    [Fact]
    public async Task The_customer_ledger_says_how_each_payment_went_and_whether_its_proof_is_attached()
    {
        var admin = await SignedInAsync(UserRole.Admin);
        var customerId = await CustomerAsync(admin);
        await SaleAsync(admin, customerId, amountPaid: 0m, paymentMethod: "Credit");
        var paymentId = await RecoveryAsync(admin, customerId, 400m, "BankTransfer");
        await AttachAsync(admin, "customer-payment", paymentId);

        var row = (await DataAsync(await admin.GetAsync($"/api/customers/{customerId}/ledger")))
            .GetProperty("items").EnumerateArray()
            .Single(entry => entry.GetProperty("entryType").GetString() == "Payment");

        row.GetProperty("paymentMethod").GetString().Should().Be("BankTransfer");
        row.GetProperty("hasProof").GetBoolean().Should().BeTrue();
    }
}
