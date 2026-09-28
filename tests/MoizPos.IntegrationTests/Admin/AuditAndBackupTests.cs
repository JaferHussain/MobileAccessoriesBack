using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Infrastructure.Backup;
using MoizPos.Infrastructure.Data;
using MoizPos.Infrastructure.Repositories;
using MoizPos.IntegrationTests.Infrastructure;
using MySqlConnector;

namespace MoizPos.IntegrationTests.Admin;

/// <summary>
/// T162, T163, T168, T170 — the audit trail and the backup/restore round trip.
///
/// The restore test runs against a throwaway schema, never the test database itself, so a
/// failure cannot destroy the data the rest of the suite depends on.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditAndBackupTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public AuditAndBackupTests(ApiFactory api) => _api = api;

    private sealed record Envelope<T>(bool Success, T? Data);

    private MySqlConnectionFactory Factory() => new(_api.ConnectionString);

    /// <summary>Where MySQL's client tools live when they are not on PATH.</summary>
    private static string ToolsDirectory()
    {
        foreach (var candidate in new[]
        {
            @"C:\Program Files\MySQL\MySQL Server 8.0\bin",
            @"C:\Program Files\MySQL\MySQL Server 8.4\bin",
            "/usr/bin",
            "/usr/local/bin",
        })
        {
            if (File.Exists(Path.Combine(candidate, "mysqldump"))
                || File.Exists(Path.Combine(candidate, "mysqldump.exe")))
            {
                return candidate;
            }
        }

        // Empty means "already on PATH", which is the normal server case.
        return string.Empty;
    }

    private (BackupService Service, string Directory) Backup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moizpos-backup-{Guid.NewGuid():N}");

        var service = new BackupService(
            new BackupOptions
            {
                Directory = directory,
                RetainDays = 30,
                ToolsDirectory = ToolsDirectory(),
            },
            _api.ConnectionString,
            new SystemClock(),
            new PeriodResolver());

        return (service, directory);
    }

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

    // ================================================================
    //  Audit trail — FR-041
    // ================================================================

    [Fact]
    public async Task Every_stock_and_balance_change_names_who_made_it_and_when()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);

        long productId;
        long supplierId;

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            productId = await connection.ExecuteScalarAsync<long>(
                """
                -- Products carry a category foreign key now, so the category has to exist first.
                INSERT IGNORE INTO categories (name, created_at_utc) VALUES ('Cables', UTC_TIMESTAMP(6));
                INSERT IGNORE INTO brands (name, is_local, is_active, created_at_utc) VALUES ('TestBrand', FALSE, TRUE, UTC_TIMESTAMP(6));
                INSERT INTO products
                    (name, category_id, brand_id, cost_price, wholesale_price, retail_price, quantity_on_hand, min_stock_threshold, is_active, created_at_utc)
                VALUES (@name, (SELECT id FROM categories WHERE name = 'Cables'), (SELECT id FROM brands WHERE name = 'TestBrand'), 0, 0, 1100, 0, 3, TRUE, UTC_TIMESTAMP(6));
                SELECT LAST_INSERT_ID();
                """,
                new { name = $"Aud {Guid.NewGuid():N}"[..18] });

            supplierId = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO suppliers (name, payable_balance, is_active, created_at_utc)
                VALUES (@name, 0, TRUE, UTC_TIMESTAMP(6));
                SELECT LAST_INSERT_ID();
                """,
                new { name = $"S {Guid.NewGuid():N}"[..18] });
        }

        var purchases = new PurchaseService(
            new UnitOfWorkFactory(Factory()),
            new PurchaseWriteRepository(),
            new StockMovementWriter(),
            new AuditWriter(),
            new SystemClock());

        await purchases.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = productId, UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        var audit = new AuditRepository(Factory());

        var (entries, total) = await audit.SearchAsync(
            "Product", productId, null, null, null, 1, 50);

        total.Should().BeGreaterThan(0);

        var quantityChange = entries.Single(e => e.FieldName == "quantity_on_hand");
        quantityChange.OldValue.Should().Be("0");
        quantityChange.NewValue.Should().Be("10");
        quantityChange.Action.Should().Be("Purchase");
        quantityChange.UserName.Should().NotBeNullOrWhiteSpace();
        quantityChange.OccurredAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_rolled_back_change_leaves_no_audit_entry_claiming_it_happened()
    {
        var (userId, _, _) = await _api.CreateUserAsync(UserRole.Admin);

        long supplierId;

        await using (var connection = await _api.OpenDatabaseAsync())
        {
            supplierId = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO suppliers (name, payable_balance, is_active, created_at_utc)
                VALUES (@name, 0, TRUE, UTC_TIMESTAMP(6));
                SELECT LAST_INSERT_ID();
                """,
                new { name = $"Roll {Guid.NewGuid():N}"[..18] });
        }

        var purchases = new PurchaseService(
            new UnitOfWorkFactory(Factory()),
            new PurchaseWriteRepository(),
            new StockMovementWriter(),
            new AuditWriter(),
            new SystemClock());

        // Fails on an unknown product, after the supplier row has been locked.
        var act = async () => await purchases.RecordPurchaseAsync(
            new RecordPurchaseRequest
            { SupplierId = supplierId, ProductId = 999_999_999, UnitCost = 800m, Quantity = 10, NewRetailPrice = 1100m },
            userId);

        await act.Should().ThrowAsync<Exception>();

        var audit = new AuditRepository(Factory());
        var (entries, _) = await audit.SearchAsync("Supplier", supplierId, null, null, null, 1, 50);

        // data-model.md invariant 5.
        entries.Should().BeEmpty("a rolled-back change must leave no trace at all");
    }

    [Fact]
    public async Task The_audit_trail_can_be_filtered_by_entity_and_user()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.GetAsync("/api/admin/audit?entityType=Product&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().Contain("items");
    }

    [Fact]
    public async Task Staff_cannot_read_the_audit_trail()
    {
        var staff = await ClientAsync(UserRole.Staff);

        (await staff.GetAsync("/api/admin/audit")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    // ================================================================
    //  Backup authorization — FR-046
    // ================================================================

    [Fact]
    public async Task Staff_cannot_take_or_restore_a_backup()
    {
        var staff = await ClientAsync(UserRole.Staff);

        (await staff.GetAsync("/api/admin/backups")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        (await staff.PostAsync("/api/admin/backups", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        (await staff.PostAsJsonAsync(
            "/api/admin/backups/restore", new { backupFileName = "x.sql", confirm = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Restoring_without_confirmation_is_refused()
    {
        var admin = await ClientAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync(
            "/api/admin/backups/restore", new { backupFileName = "anything.sql", confirm = false });

        // Replacing all data must be deliberate.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ================================================================
    //  Backup and restore — FR-045, FR-047, SC-012
    // ================================================================

    [Fact]
    public async Task A_backup_is_written_and_listed()
    {
        var (service, directory) = Backup();

        try
        {
            var result = await service.CreateAsync();

            result.FileName.Should().StartWith("moizpos-").And.EndWith(".sql");
            result.SizeBytes.Should().BeGreaterThan(0);

            var listed = await service.ListAsync();
            listed.Should().ContainSingle(file => file.FileName == result.FileName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_restored_backup_reproduces_the_shops_records()
    {
        var (service, directory) = Backup();
        var scratchDatabase = $"moizpos_test_restore_{Guid.NewGuid():N}"[..40];

        var builder = new MySqlConnectionStringBuilder(_api.ConnectionString);
        var serverConnectionString = new MySqlConnectionStringBuilder(_api.ConnectionString)
        {
            Database = "moizpos_test",
        }.ConnectionString;

        try
        {
            // Other test collections write to this schema in parallel, so an exact count would
            // be a race: rows inserted after the dump would show up in a live read but not in
            // the backup. Bracketing is the honest invariant — the restored copy must hold
            // everything that existed before the dump, and nothing that arrived after it.
            var before = await CountsAsync(_api.ConnectionString);

            var backup = await service.CreateAsync();

            var after = await CountsAsync(_api.ConnectionString);

            await using (var server = new MySqlConnection(serverConnectionString))
            {
                await server.OpenAsync();
                await server.ExecuteAsync(
                    $"CREATE DATABASE `{scratchDatabase}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;");
            }

            var scratchConnectionString = new MySqlConnectionStringBuilder(_api.ConnectionString)
            {
                Database = scratchDatabase,
            }.ConnectionString;

            var restorer = new BackupService(
                new BackupOptions { Directory = directory, ToolsDirectory = ToolsDirectory() },
                scratchConnectionString,
                new SystemClock(),
                new PeriodResolver());

            await restorer.RestoreAsync(backup.FileName);

            var restored = await CountsAsync(scratchConnectionString);

            foreach (var (table, beforeCount) in before)
            {
                // SC-012: everything committed before the backup survives the restore.
                restored[table].Should().BeGreaterThanOrEqualTo(
                    beforeCount,
                    $"'{table}' lost rows that existed when the backup was taken");

                restored[table].Should().BeLessThanOrEqualTo(
                    after[table],
                    $"'{table}' gained rows the backup could not have contained");
            }

            // The dump must actually contain the shop's records, not an empty schema.
            restored["users"].Should().BeGreaterThan(0);
            restored["products"].Should().BeGreaterThan(0);
        }
        finally
        {
            await using (var server = new MySqlConnection(serverConnectionString))
            {
                await server.OpenAsync();
                await server.ExecuteAsync($"DROP DATABASE IF EXISTS `{scratchDatabase}`;");
            }

            Directory.Delete(directory, recursive: true);
        }

        static async Task<Dictionary<string, int>> CountsAsync(string connectionString)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();

            var counts = new Dictionary<string, int>();

            foreach (var table in new[]
            {
                "products", "customers", "suppliers", "invoices", "invoice_items",
                "ledger_entries", "purchases", "expenses", "users",
            })
            {
                counts[table] = await connection.ExecuteScalarAsync<int>(
                    $"SELECT COUNT(*) FROM `{table}`;");
            }

            return counts;
        }
    }

    // ================================================================
    //  Corrupt and malicious inputs — spec edge case
    // ================================================================

    [Fact]
    public async Task A_corrupt_backup_is_refused_before_anything_is_overwritten()
    {
        var (service, directory) = Backup();

        try
        {
            Directory.CreateDirectory(directory);
            var corrupt = Path.Combine(directory, "moizpos-corrupt.sql");
            // Plausible-looking SQL that is not a dump — e.g. a half-downloaded file.
            await File.WriteAllTextAsync(corrupt, "DROP TABLE users;\nSELECT 1;");

            var act = async () => await service.RestoreAsync("moizpos-corrupt.sql");

            await act.Should().ThrowAsync<MoizPos.Domain.Errors.BusinessRuleViolationException>()
                .WithMessage("*does not look like a MySQL backup*");

            // The live data must be untouched.
            await using var connection = await _api.OpenDatabaseAsync();
            (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users;"))
                .Should().BeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_empty_backup_file_is_refused()
    {
        var (service, directory) = Backup();

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "moizpos-empty.sql"), string.Empty);

            var act = async () => await service.RestoreAsync("moizpos-empty.sql");

            await act.Should().ThrowAsync<MoizPos.Domain.Errors.BusinessRuleViolationException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\config\\sam")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("sub/dir/file.sql")]
    public async Task A_path_that_escapes_the_backup_directory_is_refused(string fileName)
    {
        var (service, _) = Backup();

        var act = async () => await service.RestoreAsync(fileName);

        await act.Should().ThrowAsync<MoizPos.Domain.Errors.BusinessRuleViolationException>()
            .WithMessage("*not a valid backup file name*");
    }

    [Fact]
    public async Task An_unknown_backup_file_is_reported_as_not_found()
    {
        var (service, directory) = Backup();

        try
        {
            Directory.CreateDirectory(directory);

            var act = async () => await service.RestoreAsync("moizpos-nope.sql");

            await act.Should().ThrowAsync<MoizPos.Domain.Errors.NotFoundException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Listing_backups_before_any_exist_returns_nothing()
    {
        var (service, _) = Backup();

        (await service.ListAsync()).Should().BeEmpty();
    }
}
