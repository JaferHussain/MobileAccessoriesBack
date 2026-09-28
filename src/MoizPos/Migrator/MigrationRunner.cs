using System.Reflection;
using DbUp;
using DbUp.Engine;

namespace MoizPos.Migrator;

/// <summary>
/// Builds and runs the DbUp upgrade engine.
///
/// Public so integration tests can bring a throwaway schema up to the exact same shape as
/// production — tests must never define their own schema, or they would stop catching migration
/// mistakes (Constitution Principle VI).
/// </summary>
public static class MigrationRunner
{
    public static UpgradeEngine BuildEngine(string connectionString) =>
        DeployChanges.To
            .MySqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(MigrationRunner).Assembly)
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();

    /// <summary>Applies every pending script. Throws with the failing script name on error.</summary>
    public static void Upgrade(string connectionString)
    {
        var result = BuildEngine(connectionString).PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Migration failed on '{result.ErrorScript?.Name}': {result.Error?.Message}",
                result.Error);
        }
    }

    /// <summary>Scripts embedded in this assembly, in the order DbUp would apply them.</summary>
    public static IReadOnlyList<string> AllScriptNames() =>
        typeof(MigrationRunner).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    internal static Assembly ScriptAssembly => typeof(MigrationRunner).Assembly;
}
