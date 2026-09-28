using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Products;

namespace MoizPos.ArchitectureTests;

/// <summary>
/// T142 — cost confidentiality as a structural property, not a runtime hope.
///
/// FR-040 requires that a Staff principal cannot obtain cost or profit data by any route. The
/// integration suite proves the endpoints that exist today behave. These tests aim at the
/// endpoints that do NOT exist yet: they fail the build if someone later adds an action that
/// returns cost data without an AdminOnly policy.
/// </summary>
public sealed class StaffDtoExposureTests
{
    /// <summary>Property-name fragments that indicate cost, margin, or profit data.</summary>
    private static readonly string[] ForbiddenFragments =
    [
        "cost", "profit", "margin", "wholesale", "payable", "expense",
    ];

    private static readonly Assembly ApiAssembly = typeof(Roles).Assembly;

    private static readonly Assembly ApplicationAssembly = typeof(ProductStaffDto).Assembly;

    /// <summary>Every controller action reachable in the API.</summary>
    private static IEnumerable<(Type Controller, MethodInfo Action)> AllActions() =>
        ApiAssembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Select(method => (Controller: type, Action: method)));

    /// <summary>
    /// True when neither the action nor its controller carries the AdminOnly policy, i.e. a
    /// signed-in Staff user can reach it.
    /// </summary>
    private static bool IsStaffReachable(Type controller, MethodInfo action)
    {
        var attributes = action.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        return !attributes.Any(a =>
            string.Equals(a.Policy, Policies.AdminOnly, StringComparison.Ordinal) ||
            (a.Roles?.Contains(Roles.Admin, StringComparison.Ordinal) ?? false));
    }

    private static IEnumerable<string> ForbiddenProperties(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type) || type.Namespace is null)
        {
            yield break;
        }

        // Only walk our own types; framework types are not part of the contract.
        if (!type.Namespace.StartsWith("MoizPos", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (ForbiddenFragments.Any(f =>
                    property.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"{type.Name}.{property.Name}";
            }

            foreach (var nested in ForbiddenProperties(Unwrap(property.PropertyType), visited))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Looks through Task, nullable, collection and paged wrappers to the real type.</summary>
    private static Type Unwrap(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();

            if (definition == typeof(Nullable<>)
                || definition == typeof(Task<>)
                || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            {
                return Unwrap(type.GetGenericArguments()[0]);
            }
        }

        return type.IsArray ? Unwrap(type.GetElementType()!) : type;
    }

    [Fact]
    public void The_staff_product_dto_declares_no_cost_property()
    {
        var offenders = ForbiddenProperties(typeof(ProductStaffDto), [])
            .ToList();

        offenders.Should().BeEmpty(
            "FR-040: Staff must not receive cost data by any route, so the property must not exist");
    }

    [Fact]
    public void The_admin_product_dto_does_carry_the_cost_price()
    {
        // The counterpart check: confidentiality must not be achieved by hiding it from everyone.
        typeof(ProductAdminDto).GetProperty(nameof(ProductAdminDto.CostPrice))
            .Should().NotBeNull();
    }

    [Fact]
    public void Every_controller_action_states_its_access_requirement()
    {
        // Attributes apply at controller level as well as action level, so both are inspected.
        var unguarded = AllActions()
            .Where(x =>
                !x.Action.GetCustomAttributes<AuthorizeAttribute>(true).Any() &&
                !x.Controller.GetCustomAttributes<AuthorizeAttribute>(true).Any() &&
                !x.Action.GetCustomAttributes<AllowAnonymousAttribute>(true).Any() &&
                !x.Controller.GetCustomAttributes<AllowAnonymousAttribute>(true).Any())
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        // The fallback policy in Program.cs makes these authenticated anyway, but an explicit
        // attribute is what a reviewer reads.
        unguarded.Should().BeEmpty(
            "every action should state its access requirement rather than relying on the fallback");
    }

    /// <summary>
    /// Anonymous access is a deliberate exception, not something that should accumulate. This
    /// test fails the build if anyone adds a second one — the plan justified exactly one.
    /// </summary>
    [Fact]
    public void Only_the_public_document_endpoint_is_anonymous()
    {
        var anonymous = AllActions()
            .Where(x =>
                x.Action.GetCustomAttributes<AllowAnonymousAttribute>(true).Any() ||
                x.Controller.GetCustomAttributes<AllowAnonymousAttribute>(true).Any())
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        anonymous.Should().BeEquivalentTo(
            [
                // Sign-in cannot require a sign-in.
                "AuthController.Login",
                "AuthController.Refresh",
                // A shop customer holds no credential and never will (plan.md, research.md R2).
                "PublicDocumentsController.Get",
            ],
            "each unauthenticated endpoint is a justified exception recorded in plan.md");
    }

    [Fact]
    public void Controllers_that_serve_financial_data_are_admin_only()
    {
        var mustBeAdminOnly = new[]
        {
            "SuppliersController",
            "PurchasesController",
            "PurchaseReturnsController",
            "ExpensesController",
            "ExpenseCategoriesController",
            "ReportsController",
            "DashboardController",
        };

        var offenders = ApiAssembly.GetTypes()
            .Where(type => mustBeAdminOnly.Contains(type.Name))
            .Where(type => type.GetCustomAttributes<AuthorizeAttribute>(true)
                .All(a => a.Policy != Policies.AdminOnly))
            .Select(type => type.Name)
            .ToList();

        offenders.Should().BeEmpty("these controllers expose cost, profit or payables (FR-040)");

        // Guards against a rename quietly emptying the list above.
        ApiAssembly.GetTypes()
            .Count(type => mustBeAdminOnly.Contains(type.Name))
            .Should().Be(mustBeAdminOnly.Length, "every named controller should still exist");
    }

    [Fact]
    public void No_admin_only_controller_exposes_a_staff_reachable_action()
    {
        var offenders = AllActions()
            .Where(x => x.Controller.GetCustomAttributes<AuthorizeAttribute>(true)
                .Any(a => a.Policy == Policies.AdminOnly))
            .Where(x => x.Action.GetCustomAttributes<AllowAnonymousAttribute>(true).Any())
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            "an [AllowAnonymous] action on an Admin-only controller would open a hole in it");
    }

    [Fact]
    public void No_staff_reachable_action_returns_a_type_carrying_cost_or_profit()
    {
        var offenders = new List<string>();

        foreach (var (controller, action) in AllActions())
        {
            if (!IsStaffReachable(controller, action))
            {
                continue;
            }

            // Controllers return IActionResult, so the declared return type says nothing. The
            // ProducesResponseType attributes are the machine-readable contract.
            var declaredTypes = action.GetCustomAttributes<ProducesResponseTypeAttribute>(true)
                .Select(a => a.Type)
                .Where(type => type is not null);

            foreach (var declared in declaredTypes)
            {
                var leaks = ForbiddenProperties(Unwrap(declared!), []).ToList();

                if (leaks.Count > 0)
                {
                    offenders.Add($"{controller.Name}.{action.Name} -> {string.Join(", ", leaks)}");
                }
            }
        }

        offenders.Should().BeEmpty("FR-040: no Staff-reachable endpoint may declare cost or profit data");
    }

    [Fact]
    public void Application_contracts_that_carry_cost_are_named_for_admins()
    {
        // A naming convention the next person can follow: if a contract type in the Products
        // namespace carries cost data, its name says Admin.
        var offenders = ApplicationAssembly.GetTypes()
            .Where(type => type.Namespace == "MoizPos.Application.Contracts.Products")
            .Where(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(p => p.Name.Contains("Cost", StringComparison.OrdinalIgnoreCase)))
            .Where(type => !type.Name.Contains("Admin", StringComparison.Ordinal)
                        && !type.Name.Contains("Request", StringComparison.Ordinal))
            .Select(type => type.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "a product contract carrying cost should be named Admin so the intent is obvious");
    }
}
