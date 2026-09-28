using System.Text.RegularExpressions;

namespace MoizPos.Application.Calculations;

/// <summary>
/// Turns what the shopkeeper types into the words product search looks for (FR-074 … FR-079).
///
/// <para>Pure, so every rule that decides whether "c type" finds "Type-C" is unit-tested without a
/// database. The repository builds SQL from <see cref="Words"/> and makes no decisions of its own.</para>
///
/// <para>Each word matches as a substring of a product's name, model, brand or category, each
/// normalised the same way — lower-cased with everything but letters and digits removed. That is
/// what lets "typec" match "Type-C": the stored side becomes "typecbraidedcable".</para>
/// </summary>
public sealed partial class ProductSearchTerms
{
    /// <summary>Bounds the query against a pasted paragraph. Nobody types nine words at a counter.</summary>
    public const int MaxWords = 8;

    /// <summary>
    /// Only words at least this long have a trailing "s" removed, so short words and model codes
    /// ("bus", "abs") are never damaged.
    /// </summary>
    private const int PluralMinimumLength = 4;

    private ProductSearchTerms(IReadOnlyList<string> words) => Words = words;

    /// <summary>
    /// The words to match, each containing only <c>[a-z0-9]</c> — nothing meaningful to SQL or to
    /// <c>LIKE</c>. They are still bound as parameters; this is the second line, not the only one.
    /// </summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>
    /// True when every word is a single character (FR-079). "c" alone matches most of the
    /// catalogue; "c type" is fine, which is the whole reason a one-letter word is allowed at all.
    /// An empty search is not too short — it lists the catalogue, as it always has.
    /// </summary>
    public bool IsTooShort => Words.Count > 0 && Words.All(word => word.Length == 1);

    public static ProductSearchTerms Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ProductSearchTerms([]);
        }

        var words = new List<string>();

        // Splitting on anything that is not a letter or digit is what makes "type-c", "type c"
        // and "type_c" the same search.
        foreach (var raw in NonAlphanumeric().Split(text))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            var word = Singular(raw.ToLowerInvariant());

            // De-duplicated so a repeated word never adds a redundant predicate.
            if (!words.Contains(word))
            {
                words.Add(word);
            }

            if (words.Count == MaxWords)
            {
                break;
            }
        }

        return new ProductSearchTerms(words);
    }

    /// <summary>
    /// "chargers" → "charger", so a typed plural finds a singular name (FR-078). Substring matching
    /// already covers the reverse. Removing a trailing character can only widen a substring match,
    /// never break one — "wireless" becomes "wireles", still inside "wireless".
    /// </summary>
    private static string Singular(string word) =>
        word.Length >= PluralMinimumLength && word.EndsWith('s') ? word[..^1] : word;

    [GeneratedRegex("[^a-zA-Z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
