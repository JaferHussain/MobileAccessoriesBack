using System.Reflection;
using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Configuration;

namespace MoizPos.Migrator;

/// <summary>
/// T013 — applies the numbered SQL scripts in <c>Scripts/</c> to a MySQL database.
///
/// Schema changes only ever reach a database through this tool (Constitution: "ad-hoc schema
/// edits against any shared database are prohibited"). DbUp records each applied script in a
/// <c>schema_versions</c> table, so running it repeatedly is safe — already-applied scripts are
/// skipped.
///
/// Runs inside the single MoizPos project rather than as its own executable, reached by passing
/// <c>migrate</c> as the first argument. That keeps one assembly and one configuration source —
/// the migrator reads the same appsettings and user-secrets the API does, so the connection string
/// is defined once.
///
/// Usage:
///   dotnet run --project backend/src/MoizPos -- migrate
///   dotnet run --project backend/src/MoizPos -- migrate --target Test
///   dotnet run --project backend/src/MoizPos -- migrate --connection "Server=...;Database=...;"
///   dotnet run --project backend/src/MoizPos -- migrate --whatif
/// </summary>
internal static class MigratorCli
{
    private const int Success = 0;
    private const int Failure = 1;

    public static int Run(string[] args)
    {
        try
        {
            // Parsed before configuration: "--whatif" is a bare switch with no value, which the
            // command-line configuration provider would reject.
            var whatIf = args.Any(a =>
                a.Equals("--whatif", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-w", StringComparison.OrdinalIgnoreCase));

            var configuration = BuildConfiguration(args.Where(a =>
                !a.Equals("--whatif", StringComparison.OrdinalIgnoreCase) &&
                !a.Equals("-w", StringComparison.OrdinalIgnoreCase)).ToArray());

            var options = MigrationOptions.From(configuration, whatIf);

            Console.WriteLine($"Target      : {options.TargetName}");
            Console.WriteLine($"Database    : {options.DatabaseName}");
            Console.WriteLine($"Server      : {options.ServerName}");
            Console.WriteLine();

            return options.WhatIf
                ? ReportPending(options.ConnectionString)
                : Upgrade(options.ConnectionString);
        }
        catch (MigrationConfigurationException ex)
        {
            WriteError(ex.Message);
            return Failure;
        }
        catch (Exception ex)
        {
            WriteError($"Migration failed: {ex.Message}");
            return Failure;
        }
    }

    private static IConfiguration BuildConfiguration(string[] args)
    {
        // The environment-specific file matters: on the shop machine the real connection string
        // lives in appsettings.Production.json, and a migrator that read only appsettings.json
        // would quietly target whatever the developer's user-secrets point at — which is how a
        // migration meant for the server gets applied to a laptop instead.
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                          ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                          ?? "Production";

        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true);

        // User-secrets are a developer convenience and must not override a file that was placed
        // on a server on purpose, so they are added only outside Production.
        if (!environment.Equals("Production", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
        }

        return builder
            .AddEnvironmentVariables("MOIZPOS_")
            .AddEnvironmentVariables()
            .AddCommandLine(args, new Dictionary<string, string>
            {
                ["--connection"] = "Connection",
                ["--target"] = "Target",
            })
            .Build();
    }

    private static UpgradeEngine BuildEngine(string connectionString) =>
        DeployChanges.To
            .MySqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(MigrationRunner.ScriptAssembly)
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();

    private static int Upgrade(string connectionString)
    {
        var engine = BuildEngine(connectionString);

        var pending = engine.GetScriptsToExecute();

        if (pending.Count == 0)
        {
            Console.WriteLine("Database is already up to date. Nothing to apply.");
            return Success;
        }

        Console.WriteLine($"Applying {pending.Count} script(s):");

        foreach (var script in pending)
        {
            Console.WriteLine($"  - {script.Name}");
        }

        Console.WriteLine();

        var result = engine.PerformUpgrade();

        if (!result.Successful)
        {
            WriteError($"Failed on '{result.ErrorScript?.Name}': {result.Error?.Message}");
            return Failure;
        }

        WriteSuccess($"Applied {pending.Count} script(s). Database is up to date.");
        return Success;
    }

    /// <summary>Lists what would be applied without touching the database.</summary>
    private static int ReportPending(string connectionString)
    {
        var pending = BuildEngine(connectionString).GetScriptsToExecute();

        if (pending.Count == 0)
        {
            Console.WriteLine("No pending scripts. Database is up to date.");
            return Success;
        }

        Console.WriteLine($"{pending.Count} pending script(s) — nothing was applied (--whatif):");

        foreach (var script in pending)
        {
            Console.WriteLine($"  - {script.Name}");
        }

        return Success;
    }

    private static void WriteError(string message) => WriteColoured(message, ConsoleColor.Red);

    private static void WriteSuccess(string message) => WriteColoured(message, ConsoleColor.Green);

    private static void WriteColoured(string message, ConsoleColor colour)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}

internal sealed class MigrationConfigurationException(string message) : Exception(message);

/// <summary>Resolved migration settings and a little safety around which database is targeted.</summary>
internal sealed record MigrationOptions(
    string ConnectionString,
    string TargetName,
    bool WhatIf)
{
    public string DatabaseName => ValueFromConnectionString("Database");

    public string ServerName => ValueFromConnectionString("Server");

    public static MigrationOptions From(IConfiguration configuration, bool whatIf)
    {
        var target = configuration["Target"] ?? "Default";

        // An explicit --connection always wins; otherwise resolve the named connection string.
        var connection = configuration["Connection"]
                         ?? configuration.GetConnectionString(target);

        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new MigrationConfigurationException(
                $"""
                 No connection string found for target '{target}'.

                 Set one with any of:
                   dotnet user-secrets set "ConnectionStrings:{target}" "Server=localhost;Database=moizpos;Uid=root;Pwd=;" ^
                     --project backend/src/MoizPos.Api
                   set MOIZPOS_ConnectionStrings__{target}=...
                   dotnet run --project backend/src/MoizPos.Migrator -- --connection "Server=..."
                 """);
        }

        return new MigrationOptions(connection, target, whatIf);
    }

    private string ValueFromConnectionString(string key)
    {
        foreach (var part in ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');

            if (separator > 0 && part[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return part[(separator + 1)..].Trim();
            }
        }

        return "(unspecified)";
    }
}
