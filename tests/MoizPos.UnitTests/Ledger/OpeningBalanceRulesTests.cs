using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Errors;

namespace MoizPos.UnitTests.Ledger;

/// <summary>
/// T025 — the arithmetic of carrying a customer's paper-register debt forward (FR-069, FR-070).
///
/// The rule that matters here is the correction: recording an opening balance a second time is a
/// **correction of the figure**, never a second debt. Getting this wrong doubles what a customer
/// owes, which is the single most damaging way this feature could fail — and it is the kind of
/// error nobody notices until the customer disputes it.
/// </summary>
public sealed class OpeningBalanceRulesTests
{
    [Fact]
    public void A_first_recording_is_the_whole_amount()
    {
        var change = OpeningBalanceRules.Delta(existing: null, requested: 12_000m);

        change.Should().Be(12_000m);
    }

    [Fact]
    public void Correcting_downward_reduces_the_balance_by_the_difference()
    {
        // 12,000 mistyped, actually 10,000. The balance must fall by 2,000 — not by 10,000, and
        // certainly not rise by 10,000.
        var change = OpeningBalanceRules.Delta(existing: 12_000m, requested: 10_000m);

        change.Should().Be(-2_000m);
    }

    [Fact]
    public void Correcting_upward_raises_the_balance_by_the_difference()
    {
        var change = OpeningBalanceRules.Delta(existing: 10_000m, requested: 12_000m);

        change.Should().Be(2_000m);
    }

    [Fact]
    public void Re_recording_the_same_figure_changes_nothing()
    {
        var change = OpeningBalanceRules.Delta(existing: 12_000m, requested: 12_000m);

        change.Should().Be(0m);
    }

    [Fact]
    public void A_correction_never_compounds()
    {
        // The invariant stated plainly: after correcting A to B, the balance has moved by exactly
        // B − A, whatever else the customer has bought or paid in between.
        const decimal balanceBefore = 15_000m;

        var change = OpeningBalanceRules.Delta(existing: 12_000m, requested: 10_000m);

        (balanceBefore + change).Should().Be(13_000m);
        (balanceBefore + change).Should().NotBe(25_000m, "that would be adding the debt again");
    }

    [Fact]
    public void A_recorded_zero_is_not_the_same_as_never_recorded()
    {
        // "Recorded as owing nothing" and "never been through the paper register" are different
        // facts, and only the first makes the next recording a correction.
        OpeningBalanceRules.Delta(existing: 0m, requested: 5_000m).Should().Be(5_000m);
        OpeningBalanceRules.Delta(existing: null, requested: 5_000m).Should().Be(5_000m);

        OpeningBalanceRules.IsCorrection(existing: 0m).Should().BeTrue();
        OpeningBalanceRules.IsCorrection(existing: null).Should().BeFalse();
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-500)]
    public void A_negative_carried_forward_amount_is_refused(decimal amount)
    {
        // That would mean the shop owes the customer, which is a different thing entirely and
        // out of scope (FR-069).
        var act = () => OpeningBalanceRules.Validate(amount, existing: null, reason: null);

        act.Should().Throw<BusinessRuleViolationException>()
            .WithMessage("*negative*");
    }

    [Fact]
    public void A_first_recording_needs_no_reason()
    {
        // It is a statement of fact copied from the paper register.
        var act = () => OpeningBalanceRules.Validate(12_000m, existing: null, reason: null);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_correction_must_say_why(string? reason)
    {
        // Someone changing a figure about money should have to explain it (FR-071).
        var act = () => OpeningBalanceRules.Validate(10_000m, existing: 12_000m, reason);

        act.Should().Throw<BusinessRuleViolationException>()
            .WithMessage("*reason*");
    }

    [Fact]
    public void A_correction_with_a_reason_is_accepted()
    {
        var act = () => OpeningBalanceRules.Validate(
            10_000m, existing: 12_000m, reason: "Mistyped from the register.");

        act.Should().NotThrow();
    }

    [Fact]
    public void The_amount_is_rounded_to_paisa()
    {
        // Money is held to two places everywhere else in this system; an opening balance must not
        // be the one figure carrying a fraction of a paisa into every later balance.
        OpeningBalanceRules.Delta(existing: null, requested: 12_000.005m).Should().Be(12_000.01m);
    }
}
