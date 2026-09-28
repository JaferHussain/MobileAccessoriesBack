using System.Text.RegularExpressions;
using FluentAssertions;

namespace MoizPos.ArchitectureTests;

/// <summary>
/// Layering, enforced on the source itself.
///
/// <para>The backend used to be five projects, and the compiler enforced Constitution Principle II
/// for free: <c>Domain</c> referenced nothing, so a domain type simply could not name a repository.
/// The backend is now one project, which is easier to open and build but means the compiler no
/// longer refuses those references. These tests take that job over.</para>
///
/// <para>They read every source file, work out which layer it belongs to from its namespace, and
/// check its <c>using</c> directives against what that layer is allowed to depend on. A violation
/// fails the build exactly as a missing project reference used to — which is the point: collapsing
/// the projects was meant to simplify the build, not to loosen the architecture.</para>
/// </summary>
public sealed class LayeringTests
{
    /// <summary>What each layer may depend on, beyond itself and anything outside MoizPos.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        // The rules from CLAUDE.md: Domain -> nothing. Application -> Domain.
        // Infrastructure -> Application. Api -> both.
        ["Domain"] = [],
        ["Application"] = ["Domain"],
        ["Infrastructure"] = ["Application", "Domain"],
        ["Api"] = ["Application", "Domain", "Infrastructure", "Migrator"],
        ["Migrator"] = [],
    };

    private static readonly Regex UsingDirective =
        new(@"^\s*using\s+(?:static\s+)?(MoizPos\.[A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline);

    private static readonly Regex NamespaceDeclaration =
        new(@"^\s*namespace\s+(MoizPos\.[A-Za-z0-9_.]+)", RegexOptions.Multiline);

    /// <summary>
    /// The project directory, found by walking up from the test binaries. Source is read from disk
    /// rather than reflected over, because a single assembly no longer records which layer a type
    /// came from — the namespace is the only remaining statement of intent.
    /// </summary>
    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "MoizPos")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the backend/src/MoizPos project must be findable from the test output");

        return Path.Combine(directory!.FullName, "src", "MoizPos");
    }

    private static IEnumerable<(string File, string Layer, string[] Uses)> SourceFiles()
    {
        var root = ProjectRoot();

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            var declared = NamespaceDeclaration.Match(text);

            if (!declared.Success)
            {
                // Program.cs uses top-level statements and declares no namespace. It is the host,
                // which is allowed to see everything, so there is nothing to check.
                continue;
            }

            var layer = declared.Groups[1].Value.Split('.')[1];

            var uses = UsingDirective.Matches(text)
                .Select(m => m.Groups[1].Value.Split('.')[1])
                .Distinct()
                .ToArray();

            yield return (Path.GetRelativePath(root, path), layer, uses);
        }
    }

    [Fact]
    public void Every_source_file_sits_in_a_known_layer()
    {
        // Guards the tests below: a new top-level namespace would otherwise be silently unchecked.
        var unknown = SourceFiles()
            .Where(f => !Allowed.ContainsKey(f.Layer))
            .Select(f => $"{f.File} declares namespace layer '{f.Layer}'")
            .ToList();

        unknown.Should().BeEmpty(
            "every file must be in Domain, Application, Infrastructure, Api or Migrator — " +
            "add the new layer to LayeringTests.Allowed and state what it may depend on");
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    [InlineData("Infrastructure")]
    [InlineData("Migrator")]
    public void A_layer_only_uses_what_it_is_allowed_to_use(string layer)
    {
        var permitted = Allowed[layer];

        var violations = SourceFiles()
            .Where(f => f.Layer == layer)
            .SelectMany(f => f.Uses
                .Where(used => used != layer && !permitted.Contains(used))
                .Select(used => $"{f.File}  ->  MoizPos.{used}"))
            .Distinct()
            .ToList();

        violations.Should().BeEmpty(
            $"{layer} may only depend on [{string.Join(", ", permitted)}] (Constitution II). " +
            "This used to be enforced by project references; now it is enforced here.");
    }

    [Fact]
    public void The_domain_depends_on_nothing_of_ours()
    {
        // Called out on its own because it is the rule most easily broken by a convenient using —
        // an entity reaching for a repository interface or a DTO.
        var offenders = SourceFiles()
            .Where(f => f.Layer == "Domain" && f.Uses.Any(u => u != "Domain"))
            .Select(f => f.File)
            .ToList();

        offenders.Should().BeEmpty("the domain is the centre; nothing in it may point outwards");
    }

    [Fact]
    public void No_layer_below_the_api_reaches_up_into_it()
    {
        // A service reaching into a controller inverts the whole arrangement.
        var offenders = SourceFiles()
            .Where(f => f.Layer != "Api" && f.Uses.Contains("Api"))
            .Select(f => $"{f.File} ({f.Layer})")
            .ToList();

        offenders.Should().BeEmpty("only the Api layer may know about the Api layer");
    }

    [Fact]
    public void A_service_never_opens_its_own_database_connection()
    {
        // CLAUDE.md: "If you need SQL inside a service, put it behind an interface in
        // Application/Abstractions and implement it in Infrastructure." With one project there is
        // nothing stopping a service adding `using MySqlConnector;` except this test.
        var root = ProjectRoot();
        var applicationDirectory = Path.Combine(root, "Application");

        var offenders = Directory
            .EnumerateFiles(applicationDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("using MySqlConnector", StringComparison.Ordinal)
                       || text.Contains("using Dapper", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        offenders.Should().BeEmpty(
            "the Application layer must not talk to a database driver directly — " +
            "put the SQL behind an abstraction and implement it in Infrastructure");
    }
}
