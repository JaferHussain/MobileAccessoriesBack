using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MoizPos.Domain.Enums;
using MoizPos.IntegrationTests.Infrastructure;

namespace MoizPos.IntegrationTests.Purchases;

/// <summary>
/// Feature 007 — the identification and payment details the shop used to keep on paper.
///
/// <para>Every new field is optional by design: a supplier recorded before this feature must
/// still open, edit and save without being forced to fill anything in. That is what stops a
/// schema addition turning into a data-entry chore across a list the shop already relies on.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SupplierDetailsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public SupplierDetailsTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data, ErrorBody? Error);

    private sealed record ErrorBody(string Code, string Message);

    private async Task<HttpClient> ClientAsync(UserRole role)
    {
        var (_, username, password) = await _api.CreateUserAsync(role);
        var client = _api.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        var body = await login.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body!.Data!.GetProperty("accessToken").GetString());

        return client;
    }

    private static object FullSupplier(string name) => new
    {
        name,
        contactNumber = "03001234567",
        address = "Shop 4, Mobile Market, Lodhran",
        cnic = "36603-1234567-1",
        email = "rehman.traders@example.com",
        bankName = "Meezan Bank",
        bankAccountTitle = "Al-Rehman Traders",
        bankAccountNumber = "PK36MEZN0001234567890123",
        notes = "Delivers on Tuesdays. Prefers transfer over cash.",
    };

    [Fact]
    public async Task A_supplier_keeps_every_detail_it_was_given()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = $"Rehman {Guid.NewGuid():N}"[..20];

        var created = await admin.PostAsJsonAsync("/api/suppliers", FullSupplier(name));
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var read = await admin.GetAsync($"/api/suppliers/{id}");
        var supplier = (await read.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        supplier.GetProperty("cnic").GetString().Should().Be("36603-1234567-1");
        supplier.GetProperty("email").GetString().Should().Be("rehman.traders@example.com");
        supplier.GetProperty("bankName").GetString().Should().Be("Meezan Bank");
        supplier.GetProperty("bankAccountTitle").GetString().Should().Be("Al-Rehman Traders");
        supplier.GetProperty("bankAccountNumber").GetString()
            .Should().Be("PK36MEZN0001234567890123");
        supplier.GetProperty("notes").GetString().Should().StartWith("Delivers on Tuesdays");
    }

    [Fact]
    public async Task A_cnic_is_stored_exactly_as_it_was_typed()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = $"Plain {Guid.NewGuid():N}"[..20];

        // No dashes. The supplier's own paperwork decides the format, not this software —
        // reformatting it would make the record disagree with the document it came from.
        var created = await admin.PostAsJsonAsync("/api/suppliers", new
        {
            name,
            cnic = "3660312345671",
        });

        var id = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var read = await admin.GetAsync($"/api/suppliers/{id}");
        var supplier = (await read.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        supplier.GetProperty("cnic").GetString().Should().Be("3660312345671");
    }

    [Fact]
    public async Task A_supplier_with_nothing_but_a_name_is_still_valid()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = $"Bare {Guid.NewGuid():N}"[..20];

        var created = await admin.PostAsJsonAsync("/api/suppliers", new { name });

        created.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "every new field is optional — a supplier the shop only knows by name must still save");

        var supplier = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        supplier.GetProperty("cnic").ValueKind.Should().Be(JsonValueKind.Null);
        supplier.GetProperty("bankName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Editing_a_supplier_can_fill_the_new_details_in_later()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = $"Later {Guid.NewGuid():N}"[..20];

        // Exactly the shape of a supplier that existed before this feature: name only.
        var created = await admin.PostAsJsonAsync("/api/suppliers", new { name });
        var id = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var updated = await admin.PutAsJsonAsync($"/api/suppliers/{id}", new
        {
            name,
            bankName = "HBL",
            bankAccountNumber = "1234567890123",
        });

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var read = await admin.GetAsync($"/api/suppliers/{id}");
        var supplier = (await read.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!.Data!;

        supplier.GetProperty("bankName").GetString().Should().Be("HBL");
        supplier.GetProperty("bankAccountNumber").GetString().Should().Be("1234567890123");
    }

    [Fact]
    public async Task Suppliers_stay_closed_to_Staff_entirely()
    {
        var admin = await ClientAsync(UserRole.Admin);
        var name = $"Read {Guid.NewGuid():N}"[..20];

        var created = await admin.PostAsJsonAsync("/api/suppliers", FullSupplier(name));
        var id = (await created.Content.ReadFromJsonAsync<Envelope<JsonElement>>(Json))!
            .Data!.GetProperty("id").GetInt64();

        var staff = await ClientAsync(UserRole.Staff);

        // Unchanged by this feature, and asserted because of what the feature added: bank
        // details and a CNIC are exactly the kind of thing a widening would leak, so the
        // existing Admin-only boundary is now worth a test of its own.
        var read = await staff.GetAsync($"/api/suppliers/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var write = await staff.PutAsJsonAsync($"/api/suppliers/{id}", new { name, bankName = "X" });
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_over_long_detail_is_refused_rather_than_silently_cut()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/api/suppliers", new
        {
            name = $"Long {Guid.NewGuid():N}"[..20],
            bankName = new string('x', 200),
        });

        // Truncating at the column width would store something the owner never typed.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
