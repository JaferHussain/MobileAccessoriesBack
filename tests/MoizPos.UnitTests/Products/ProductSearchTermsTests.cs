using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Products;

/// <summary>
/// T008 — turning what the shopkeeper types into the words search looks for (FR-074 … FR-079).
///
/// Every rule that decides whether "c type" finds "Type-C" lives here, as a pure function, so each
/// spelling the owner cares about is proven without a database. The repository only builds SQL
/// from the output.
/// </summary>
public sealed class ProductSearchTermsTests
{
    [Theory]
    [InlineData("c type")]
    [InlineData("type c")]
    [InlineData("type-c")]
    [InlineData("TYPE-C")]
    [InlineData("  type   c  ")]
    [InlineData("type_c")]
    [InlineData("type - c")]
    public void Every_spelling_of_type_c_yields_the_same_words(string typed)
    {
        // SC-022. Order, case, spacing and punctuation must not matter.
        ProductSearchTerms.Parse(typed).Words.Should().BeEquivalentTo(["type", "c"]);
    }

    [Fact]
    public void A_run_together_word_is_kept_whole()
    {
        // "typec" is one word; it matches "Type-C" because the stored name is normalised to
        // "typecbraidedcable", not because this splits it.
        ProductSearchTerms.Parse("typec").Words.Should().Equal("typec");
    }

    [Theory]
    [InlineData("Chargers", "charger")]
    [InlineData("cables", "cable")]
    [InlineData("EARBUDS", "earbud")]
    public void A_typed_plural_becomes_singular(string typed, string expected)
    {
        // FR-078. Substring matching already lets "charger" find "Chargers"; this closes the
        // reverse — "chargers" finding a product named "Charger 18W".
        ProductSearchTerms.Parse(typed).Words.Should().Equal(expected);
    }

    [Theory]
    [InlineData("bus")]
    [InlineData("abs")]
    [InlineData("gas")]
    public void Short_words_ending_in_s_are_left_alone(string typed)
    {
        // The plural rule applies only to words of four or more characters, so short words and
        // model codes are never damaged.
        ProductSearchTerms.Parse(typed).Words.Should().Equal(typed);
    }

    [Fact]
    public void Stripping_an_s_only_ever_widens_a_match()
    {
        // "wireless" becomes "wireles", which is still a substring of "wireless". Removing a
        // trailing character can never stop a substring from matching.
        var word = ProductSearchTerms.Parse("wireless").Words.Single();

        "wireless".Should().Contain(word);
    }

    [Theory]
    [InlineData("20-W")]
    [InlineData("20 w")]
    [InlineData("20_W")]
    public void Numbers_split_the_same_way_as_letters(string typed)
    {
        ProductSearchTerms.Parse(typed).Words.Should().BeEquivalentTo(["20", "w"]);
    }

    [Fact]
    public void A_repeated_word_counts_once()
    {
        ProductSearchTerms.Parse("cable cable CABLE").Words.Should().Equal("cable");
    }

    [Fact]
    public void Words_keep_the_order_they_were_typed()
    {
        // Order does not affect which products match, but a stable order keeps the SQL — and its
        // parameters — identical for identical input.
        ProductSearchTerms.Parse("oppo charger fast").Words.Should().Equal("oppo", "charger", "fast");
    }

    [Fact]
    public void A_search_is_capped_at_eight_words()
    {
        // Bounds the query against a pasted paragraph. Nobody types nine words at a counter.
        var words = ProductSearchTerms.Parse("one two three four five six seven eight nine ten").Words;

        words.Should().HaveCount(8);
        words.Should().NotContain(["nine", "ten"]);
    }

    [Theory]
    [InlineData("50%_off' --")]
    [InlineData("'; DROP TABLE products; --")]
    [InlineData("a%b_c\\d\"e")]
    public void Nothing_meaningful_to_sql_or_like_survives(string typed)
    {
        // Words reach the repository as bound parameters anyway, but reducing them to letters and
        // digits first means a % or _ can never widen a LIKE into a wildcard.
        ProductSearchTerms.Parse(typed).Words
            .Should().OnlyContain(w => System.Text.RegularExpressions.Regex.IsMatch(w, "^[a-z0-9]+$"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("!!! ???")]
    public void Nothing_typed_means_no_search(string? typed)
    {
        var terms = ProductSearchTerms.Parse(typed);

        // An empty search lists the whole catalogue, exactly as today — it is not "too short".
        terms.Words.Should().BeEmpty();
        terms.IsTooShort.Should().BeFalse();
    }

    [Theory]
    [InlineData("c")]
    [InlineData("C")]
    [InlineData("c t")]
    [InlineData("a-b-c")]
    public void A_search_of_only_single_letters_is_too_short(string typed)
    {
        // FR-079. "c" alone matches most of the catalogue.
        ProductSearchTerms.Parse(typed).IsTooShort.Should().BeTrue();
    }

    [Theory]
    [InlineData("c type")]
    [InlineData("20 w")]
    [InlineData("ab")]
    public void A_single_letter_alongside_a_longer_word_is_fine(string typed)
    {
        // "c type" is the whole reason a one-letter word must be allowed at all.
        ProductSearchTerms.Parse(typed).IsTooShort.Should().BeFalse();
    }
}
