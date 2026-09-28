using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Authorization;

/// <summary>
/// T143–T146 — the systematic sweep.
///
/// The constitution rejects UI hiding as sufficient, so these tests bypass the interface
/// entirely and call every route directly with a Staff token. This is the gate before a salesman
/// is given a login.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RoleEnforcementTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public RoleEnforcementTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message);

    private async Task<HttpClient> ClientAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var body = await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers
            .AuthenticationHeaderValue("Bearer", body!.Data!.GetProperty("accessToken").GetString());

        return client;
    }

    /// <summary>Every GET route that exposes cost, profit, payables or administration.</summary>
    private static readonly string[] AdminOnlyGetRoutes =
    [
        "/api/dashboard?period=Today",
        "/api/reports/sales?from=2026-09-01&to=2026-09-30",
        "/api/reports/profit?from=2026-09-01&to=2026-09-30",
        "/api/reports/profit-by-product?from=2026-09-01&to=2026-09-30",
        "/api/reports/purchases?from=2026-09-01&to=2026-09-30",
        "/api/reports/stock",
        "/api/reports/stock-movements",
        "/api/reports/receivables",
        "/api/reports/payables",
        "/api/reports/expenses?from=2026-09-01&to=2026-09-30",
        "/api/suppliers",
        "/api/suppliers/1",
        "/api/purchases",
        "/api/expenses",
        "/api/expense-categories",
        "/api/products/1/stock-movements",
    ];

    /// <summary>Routes a salesman legitimately needs to do their job.</summary>
    private static readonly string[] StaffRoutes =
    [
        "/api/products?search=cable",
        "/api/products/by-barcode/none",
        "/api/customers",
        "/api/invoices",
    ];

    public static TheoryData<string> AdminOnlyRoutes()
    {
        var data = new TheoryData<string>();

        foreach (var route in AdminOnlyGetRoutes)
        {
            data.Add(route);
        }

        return data;
    }

    public static TheoryData<string> StaffAllowedRoutes()
    {
        var data = new TheoryData<string>();

        foreach (var route in StaffRoutes)
        {
            data.Add(route);
        }

        return data;
    }

    // ================================================================
    //  Staff is refused everything financial — FR-040
    // ================================================================

    [Theory]
    [MemberData(nameof(AdminOnlyRoutes))]
    public async Task Staff_is_refused_every_admin_only_route(string url)
    {
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync(url);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden, $"{url} exposes cost, profit or payables");
    }

    [Theory]
    [MemberData(nameof(AdminOnlyRoutes))]
    public async Task Admin_can_reach_every_admin_only_route(string url)
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync(url);

        // 404 is fine for a route with an id that may not exist; 403 never is.
        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden, $"an Admin must be able to reach {url}");
    }

    [Fact]
    public async Task Staff_cannot_write_to_admin_only_routes()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var writes = new (string Url, object Body)[]
        {
            ("/api/suppliers", new { name = "X" }),
            ("/api/purchases", new { supplierId = 1, productId = 1, unitCost = 1m, quantity = 1 }),
            ("/api/expenses", new { categoryId = 1, amount = 1m, expenseDate = DateTime.UtcNow }),
            ("/api/expense-categories", new { name = "X" }),
            ("/api/purchase-returns", new { purchaseId = 1, quantity = 1 }),
            ("/api/products", new { name = "X", categoryId = 1L }),
        };

        foreach (var (url, body) in writes)
        {
            var response = await staff.PostAsJsonAsync(url, body);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"POST {url}");
        }
    }

    [Fact]
    public async Task Staff_cannot_adjust_stock_or_delete_a_product()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var adjust = await staff.PostAsJsonAsync(
            "/api/products/1/adjust-stock", new { newQuantity = 999, note = "nope" });

        var delete = await staff.DeleteAsync("/api/products/1");

        adjust.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ================================================================
    //  No cost data leaks in a permitted response — quickstart V7
    // ================================================================

    [Fact]
    public async Task No_staff_permitted_response_contains_cost_or_profit_words()
    {
        var admin = await ClientAsync(UserRole.Admin);

        // Something real to find, so the check is not passing on empty results.
        await admin.PostAsJsonAsync("/api/products", new
        {
            name = $"Guard {Guid.NewGuid():N}"[..20],
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            // Distinctive decimals: a bare "811" collides with ids and timestamps by chance,
            // but "811.37" appears in a response only if the cost itself leaked.
            costPrice = 811.37m,
            wholesalePrice = 912.53m,
            retailPrice = 1300m,
            salePrice = 1100m,
            quantityOnHand = 10,
            minStockThreshold = 3,
        });

        var staff = await ClientAsync(UserRole.Staff);

        foreach (var url in StaffRoutes)
        {
            var response = await staff.GetAsync(url);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            var raw = await response.Content.ReadAsStringAsync();

            raw.Should().NotContain("costPrice", $"{url} must not reveal cost");
            raw.Should().NotContain("wholesalePrice", $"{url} must not reveal wholesale price");
            raw.Should().NotContain("profit", $"{url} must not reveal profit");
            raw.Should().NotContain("811.37", $"{url} leaked the cost value");
            raw.Should().NotContain("912.53", $"{url} leaked the wholesale value");
        }
    }

    // ================================================================
    //  Staff can still do their job — spec US6 scenario 2
    // ================================================================

    [Theory]
    [MemberData(nameof(StaffAllowedRoutes))]
    public async Task Staff_can_reach_the_routes_they_need(string url)
    {
        var staff = await ClientAsync(UserRole.Staff);

        var response = await staff.GetAsync(url);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Staff_can_create_a_sale_and_receive_a_payment()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var created = await admin.PostAsJsonAsync("/api/products", new
        {
            name = $"Sellable {Guid.NewGuid():N}"[..20],
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            minStockThreshold = 3,
        });

        var productId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Prices and stock arrive with the first delivery, not with the product.
        await _api.StockProductAsync(productId, quantity: 10, salePrice: 1000m);

        var staff = await ClientAsync(UserRole.Staff);

        var customer = await staff.PostAsJsonAsync(
            "/api/customers", new { name = $"C {Guid.NewGuid():N}"[..16] });

        customer.StatusCode.Should().Be(HttpStatusCode.Created);

        var customerId = (await customer.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Selling for FULL payment is the salesman's job and must stay that way (FR-053).
        var sale = await staff.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 1000m,
            paymentMethod = "Cash",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1000m } },
        });

        sale.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "selling is the salesman's job. Server said: {0}",
            await sale.Content.ReadAsStringAsync());

        // A debt to collect against. Only the owner can create one now (FR-051), so the owner
        // makes it and the salesman recovers it — which is exactly the division of labour the
        // shop asked for.
        var onCredit = await admin.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 0m,
            paymentMethod = "Credit",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1000m } },
        });

        onCredit.StatusCode.Should().Be(HttpStatusCode.Created);

        var payment = await staff.PostAsJsonAsync(
            $"/api/customers/{customerId}/payments", new { amount = 200m, paymentMethod = "Cash" });

        payment.StatusCode.Should().Be(HttpStatusCode.Created, "taking payment is the salesman's job");
    }

    /// <summary>
    /// The other half of the test above, split out when FR-052 arrived.
    ///
    /// This case previously expected 201: a salesman could complete a part-paid sale. That was
    /// the shop's exposure — stock leaving against a debt the owner never agreed to — and the
    /// owner asked for it to stop. The assertion is kept rather than deleted so the change of
    /// behaviour is visible in the suite rather than silently disappearing from it.
    /// </summary>
    [Fact]
    public async Task Staff_cannot_create_a_part_paid_sale()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var created = await admin.PostAsJsonAsync("/api/products", new
        {
            name = $"Partial {Guid.NewGuid():N}"[..20],
            categoryId = (await _api.EnsureCatalogueAsync()).CategoryId,
            brandId = (await _api.EnsureCatalogueAsync()).BrandId,
            minStockThreshold = 3,
        });

        var productId = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        // Prices and stock arrive with the first delivery, not with the product.
        await _api.StockProductAsync(productId, quantity: 10, salePrice: 1000m);

        var staff = await ClientAsync(UserRole.Staff);

        var customer = await staff.PostAsJsonAsync(
            "/api/customers", new { name = $"C {Guid.NewGuid():N}"[..16] });

        var customerId = (await customer.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var sale = await staff.PostAsJsonAsync("/api/invoices", new
        {
            customerId,
            amountPaid = 400m,
            paymentMethod = "Partial",
            items = new[] { new { productId, quantity = 1, unitSalePrice = 1000m } },
        });

        sale.StatusCode.Should().Be(HttpStatusCode.Forbidden, "only the owner may approve udhaar");
    }

    [Fact]
    public async Task Staff_can_see_a_customers_ledger()
    {
        var staff = await ClientAsync(UserRole.Staff);

        var created = await staff.PostAsJsonAsync(
            "/api/customers", new { name = $"L {Guid.NewGuid():N}"[..16] });

        var id = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        (await staff.GetAsync($"/api/customers/{id}/ledger")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await staff.GetAsync($"/api/customers/{id}/summary")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Staff_can_record_a_sale_return_at_the_counter()
    {
        var staff = await ClientAsync(UserRole.Staff);

        // Reaches the handler and fails on the data, not on authorization.
        var response = await staff.PostAsJsonAsync("/api/sale-returns", new
        {
            invoiceId = 999_999_999,
            items = new[] { new { invoiceItemId = 1, quantity = 1 } },
        });

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    // ================================================================
    //  Nothing is reachable without a token — FR-038
    // ================================================================

    [Fact]
    public async Task Every_business_route_refuses_an_anonymous_caller()
    {
        var anonymous = _api.CreateClient();

        var routes = AdminOnlyGetRoutes.Concat(StaffRoutes).ToList();

        foreach (var url in routes)
        {
            var response = await anonymous.GetAsync(url);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, url);
        }
    }

    [Fact]
    public async Task Only_health_and_the_public_document_route_are_anonymous()
    {
        var anonymous = _api.CreateClient();

        (await anonymous.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Login and refresh are anonymous by necessity; they reject bad credentials on their own.
        (await anonymous.PostAsJsonAsync("/api/auth/login", new { username = "x", password = "yyyyyyyy" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_tampered_token_is_refused()
    {
        var staff = await ClientAsync(UserRole.Staff);
        var original = staff.DefaultRequestHeaders.Authorization!.Parameter!;

        // Change the FIRST character of the signature, not the last.
        //
        // A JWT HMAC-SHA256 signature is 32 bytes, which is 43 base64url characters — and the last
        // character carries only 4 meaningful bits. 'A', 'B', 'C' and 'D' all decode to the same
        // byte, so flipping the last character between 'A' and 'B' leaves the signature BYTE FOR
        // BYTE IDENTICAL. Whenever a signature happened to end in 'A' or 'B' the token was still
        // genuinely valid, the API was right to accept it, and this test failed for no reason.
        // The first character carries a full 6 bits, so changing it always changes the signature.
        var segments = original.Split('.');
        segments.Should().HaveCount(3, "a JWT is header.payload.signature");

        var signature = segments[2];
        segments[2] = (signature[0] == 'A' ? 'B' : 'A') + signature[1..];

        var tampered = string.Join('.', segments);
        tampered.Should().NotBe(original, "the tampering must actually change the token");

        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tampered);

        (await client.GetAsync("/api/products")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_deactivated_user_cannot_refresh_their_session()
    {
        var (userId, username, password) = await _api.CreateUserAsync(UserRole.Staff);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var refreshToken = (await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("refreshToken").GetString();

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE users SET is_active = FALSE WHERE id = @userId;", new { userId });
        }

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        refresh.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "a departed employee must lose access");
    }
}
