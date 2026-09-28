using System.Reflection;
using Microsoft.Extensions.Configuration;
using MoizPos.Application.Abstractions;
using MoizPos.Infrastructure.Data;
using MoizPos.Migrator;
using MySqlConnector;

namespace MoizPos.IntegrationTests.Infrastructure;

/// <summary>
/// Brings the test database up to the real production schema once per test run, by applying the
/// same migration scripts the shop's database gets. Tests never hand-write DDL — otherwise a
/// broken migration would still show green.
///
/// Fails fast and loudly when MySQL is unreachable. The transactional guarantees in Constitution
/// Principle IV cannot be verified against a fake, so silently skipping would be worse than
/// failing.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string TestConnectionKey = "ConnectionStrings:Test";

    /// <summary>
    /// xUnit runs separate collections in parallel, so several fixtures initialise at once.
    /// DbUp is not safe to run concurrently against the same database — two upgrades racing
    /// corrupt its internal script list. Migrate exactly once per test process.
    /// </summary>
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);

    private static bool _migrated;

    public string ConnectionString { get; private set; } = string.Empty;

    public IDbConnectionFactory ConnectionFactory { get; private set; } = null!;

    public IUnitOfWorkFactory UnitOfWorkFactory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        ConnectionString = ResolveConnectionString();
        GuardAgainstNonTestDatabase(ConnectionString);

        await AssertServerReachableAsync(ConnectionString);

        await MigrationGate.WaitAsync();

        try
        {
            if (!_migrated)
            {
                MigrationRunner.Upgrade(ConnectionString);
                _migrated = true;
            }
        }
        finally
        {
            MigrationGate.Release();
        }

        ConnectionFactory = new MySqlConnectionFactory(ConnectionString);
        UnitOfWorkFactory = new UnitOfWorkFactory(ConnectionFactory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Opens a plain connection for arrange/assert steps outside the code under test.</summary>
    /// <summary>
    /// A connection for a test to seed or inspect with.
    ///
    /// <para><b>Strict, like the shop's server.</b> The live database runs
    /// <c>STRICT_TRANS_TABLES</c> globally, so every connection there refuses a truncated string
    /// or a NOT NULL column left out of an INSERT. The local MySQL does not, so without this a
    /// test could seed data the real server would have rejected — and the suite would be proving
    /// behaviour that cannot happen in the shop.</para>
    /// </summary>
    public async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SET SESSION sql_mode = CONCAT(@@sql_mode, ',STRICT_ALL_TABLES')";
            await command.ExecuteNonQueryAsync();
        }

        return connection;
    }

    private static string ResolveConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables("MOIZPOS_")
            .Build();

        var connectionString = configuration[TestConnectionKey]
                               ?? configuration.GetConnectionString("Test");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                """
                No test database connection string configured.

                Set it with:
                  dotnet user-secrets set "ConnectionStrings:Test" ^
                    "Server=localhost;Database=moizpos_test;Uid=root;Pwd=;" ^
                    --project backend/src/MoizPos.Api

                Create the schema first with docs/create-databases.sql.
                """);
        }

        return connectionString;
    }

    /// <summary>
    /// Refuses to run against anything not obviously a test database. These tests create and drop
    /// tables; pointing them at the live shop database would destroy real sales records.
    /// </summary>
    private static void GuardAgainstNonTestDatabase(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);

        if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to run integration tests against database '{builder.Database}'. " +
                "The test database name must contain 'test'.");
        }
    }

    private static async Task AssertServerReachableAsync(string connectionString)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
        }
        catch (MySqlException ex)
        {
            throw new InvalidOperationException(
                $"Cannot reach the test database: {ex.Message}\n" +
                "Start MySQL and create the schemas with docs/create-databases.sql.",
                ex);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
