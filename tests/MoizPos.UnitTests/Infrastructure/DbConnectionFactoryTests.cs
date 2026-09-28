using System.Reflection;
using Dapper;
using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Entities;
using MoizPos.Infrastructure.Data;

namespace MoizPos.UnitTests.Infrastructure;

/// <summary>T016 — connection-string handling. Opening a real connection is covered by the
/// integration suite; this guards the configuration mistakes that produce confusing failures.</summary>
public sealed class DbConnectionFactoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_missing_connection_string(string? connectionString)
    {
        var act = () => new MySqlConnectionFactory(connectionString!);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*connection string is required*");
    }

    [Fact]
    public void Accepts_a_valid_connection_string()
    {
        var act = () => new MySqlConnectionFactory("Server=localhost;Database=moizpos;Uid=root;Pwd=;");

        act.Should().NotThrow();
    }
}

/// <summary>T020 — Dapper mapping and the no-floating-point rule (research.md R5).</summary>
public sealed class DapperConfigTests
{
    [Fact]
    public void Maps_snake_case_columns_to_pascal_case_properties()
    {
        DapperConfig.Apply();

        DefaultTypeMap.MatchNamesWithUnderscores.Should().BeTrue(
            "the schema is snake_case and the entities are PascalCase");
    }

    [Fact]
    public void Applying_twice_is_harmless()
    {
        DapperConfig.Apply();
        var act = DapperConfig.Apply;

        act.Should().NotThrow();
    }

    /// <summary>
    /// Guards Constitution Principle IV: binary floating point cannot represent 0.10 exactly, so
    /// a single double in a money field would make ledger balances fail to reconcile (SC-004).
    /// </summary>
    [Fact]
    public void No_entity_exposes_a_floating_point_money_field()
    {
        var offenders = typeof(Product).Assembly
            .GetTypes()
            .Where(type => type.IsClass && type.Namespace == "MoizPos.Domain.Entities")
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => new { Type = type.Name, Property = property }))
            .Where(x => x.Property.PropertyType == typeof(double)
                        || x.Property.PropertyType == typeof(float)
                        || x.Property.PropertyType == typeof(double?)
                        || x.Property.PropertyType == typeof(float?))
            .Select(x => $"{x.Type}.{x.Property.Name}")
            .ToList();

        offenders.Should().BeEmpty("money and quantities must be decimal or int, never floating point");
    }

    [Fact]
    public void No_calculator_returns_a_floating_point_amount()
    {
        var offenders = typeof(InvoiceCalculator).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "MoizPos.Application.Calculations")
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName && method.DeclaringType == type)
                .Select(method => new { Type = type.Name, Method = method }))
            .Where(x => x.Method.ReturnType == typeof(double) || x.Method.ReturnType == typeof(float))
            .Select(x => $"{x.Type}.{x.Method.Name}")
            .ToList();

        offenders.Should().BeEmpty("every monetary calculation must return decimal");
    }
}
