using System.Reflection;
using FluentAssertions;
using MoizPos.Application.Documents;

namespace MoizPos.ArchitectureTests;

/// <summary>
/// What a customer may be handed, as a structural property.
///
/// <para>A shared document travels out of the shop through the one unauthenticated endpoint, to a
/// phone the shop does not control, and cannot be recalled once sent. So the rule cannot be "the
/// current template happens not to print cost" — a template is one edit away from a cost column,
/// and nothing would fail. The rule is that the document model <b>has no cost to print</b>.</para>
///
/// <para>These tests aim at the document that does not exist yet: they fail the build if someone
/// later adds a field to an invoice or receipt model that carries cost, profit, supplier or proof
/// data (FR-113, FR-114, FR-129).</para>
/// </summary>
public sealed class SharedDocumentExposureTests
{
    /// <summary>
    /// Property-name fragments that have no business on a customer's bill.
    ///
    /// <para>The first six mirror <see cref="StaffDtoExposureTests"/> — what a salesman may not
    /// see, a customer certainly may not. The rest are specific to this boundary: a supplier is
    /// the shop's business, and a payment proof is evidence held for the shop's own disputes, not
    /// part of what the customer bought.</para>
    /// </summary>
    private static readonly string[] ForbiddenFragments =
    [
        "cost", "profit", "margin", "wholesale", "payable", "expense",
        "supplier", "proof", "purchase",
    ];

    /// <summary>Everything a shared document is rendered from.</summary>
    private static IEnumerable<Type> DocumentModels() =>
    [
        typeof(InvoiceDocument),
        typeof(InvoiceDocumentLine),
        typeof(ReceiptDocument),
        typeof(ShopDetails),
    ];

    private static IEnumerable<string> ForbiddenProperties(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type) || type.Namespace is null)
        {
            yield break;
        }

        // Only walk our own types; framework types are not part of the document's shape.
        if (!type.Namespace.StartsWith("MoizPos", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = property.Name;

            if (ForbiddenFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"{type.Name}.{name}";
            }

            // A nested type could smuggle cost in one level down.
            var nested = property.PropertyType;

            if (nested.IsGenericType)
            {
                foreach (var argument in nested.GetGenericArguments())
                {
                    foreach (var found in ForbiddenProperties(argument, visited))
                    {
                        yield return found;
                    }
                }

                continue;
            }

            foreach (var found in ForbiddenProperties(nested, visited))
            {
                yield return found;
            }
        }
    }

    [Fact]
    public void A_shared_document_carries_no_cost_or_profit_field()
    {
        var offenders = DocumentModels()
            .SelectMany(model => ForbiddenProperties(model, []))
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            "a document leaves the shop for a phone nobody controls and cannot be recalled — so " +
            "it must not be POSSIBLE to print cost or profit on one, not merely unusual");
    }

    [Fact]
    public void A_shared_document_carries_no_supplier_or_payment_proof_field()
    {
        // Named separately from the test above so a failure says which boundary was crossed.
        // Payment proofs live in their own directory precisely so that no rule meant for
        // receipts can serve one; this stops the model from reaching for one in the first place.
        var offenders = DocumentModels()
            .SelectMany(model => ForbiddenProperties(model, []))
            .Where(name =>
                name.Contains("supplier", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("proof", StringComparison.OrdinalIgnoreCase))
            .ToList();

        offenders.Should().BeEmpty("a customer's bill is not a window into the shop's buying");
    }

    /// <summary>
    /// A document names exactly one customer. A collection of customers on the model would mean
    /// one link could resolve to more than one person's business (FR-115).
    /// </summary>
    [Fact]
    public void A_shared_document_names_one_customer_and_never_a_list_of_them()
    {
        var offenders = DocumentModels()
            .SelectMany(model => model.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p =>
                p.Name.Contains("customer", StringComparison.OrdinalIgnoreCase) &&
                p.PropertyType != typeof(string) &&
                typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType))
            .Select(p => $"{p.DeclaringType?.Name}.{p.Name}")
            .ToList();

        offenders.Should().BeEmpty("one share link resolves to exactly one document");
    }
}
