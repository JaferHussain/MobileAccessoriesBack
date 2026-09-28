using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Errors;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// T098 — the udhaar running balance. The first two tests encode the shop owner's worked example
/// from spec.md US2 scenarios 1–2 verbatim and are a release blocker if they ever fail.
/// </summary>
public sealed class LedgerBalanceCalculatorTests
{
    // --- spec US2 scenario 1: bill 3,000, paid 1,000 -> balance 2,000 ---

    [Fact]
    public void Credit_sale_raises_the_balance_by_the_unpaid_amount()
    {
        var balance = LedgerBalanceCalculator.NextBalance(
            previousBalance: 0m,
            billAmount: 3000m,
            paidAmount: 1000m);

        balance.Should().Be(2000m);
    }

    // --- spec US2 scenario 2: then a payment of 1,500 -> balance 500 ---

    [Fact]
    public void Payment_reduces_the_balance()
    {
        var balance = LedgerBalanceCalculator.NextBalance(
            previousBalance: 2000m,
            billAmount: 0m,
            paidAmount: 1500m);

        balance.Should().Be(500m);
    }

    [Fact]
    public void Reproduces_the_owners_worked_example_end_to_end()
    {
        var entries = new[]
        {
            new LedgerMovement(BillAmount: 3000m, PaidAmount: 1000m),
            new LedgerMovement(BillAmount: 0m, PaidAmount: 1500m),
        };

        var balances = LedgerBalanceCalculator.RunningBalances(0m, entries).ToArray();

        balances.Should().Equal(2000m, 500m);
    }

    [Fact]
    public void Tracks_a_longer_sequence_of_bills_and_payments()
    {
        var entries = new[]
        {
            new LedgerMovement(1500m, 0m),      // 1500
            new LedgerMovement(2500m, 1000m),   // 3000
            new LedgerMovement(0m, 3000m),      // 0
            new LedgerMovement(750.50m, 0m),    // 750.50
            new LedgerMovement(0m, 250.50m),    // 500.00
        };

        var balances = LedgerBalanceCalculator.RunningBalances(0m, entries).ToArray();

        balances.Should().Equal(1500m, 3000m, 0m, 750.50m, 500m);
    }

    [Fact]
    public void Starts_from_a_non_zero_opening_balance()
    {
        // Opening balances are entered manually at go-live (spec assumption).
        var balances = LedgerBalanceCalculator
            .RunningBalances(5000m, [new LedgerMovement(0m, 2000m)])
            .ToArray();

        balances.Should().Equal(3000m);
    }

    [Fact]
    public void Fully_settling_a_balance_lands_exactly_on_zero()
    {
        var balance = LedgerBalanceCalculator.NextBalance(2000m, 0m, 2000m);

        balance.Should().Be(0m);
    }

    // --- overpayment must be deliberate (FR-022) ---

    [Fact]
    public void Rejects_a_payment_that_would_drive_the_balance_negative()
    {
        var act = () => LedgerBalanceCalculator.NextBalance(
            previousBalance: 500m,
            billAmount: 0m,
            paidAmount: 10_000m);

        act.Should().Throw<OverpaymentNotConfirmedException>();
    }

    [Fact]
    public void Allows_an_overpayment_when_explicitly_confirmed()
    {
        var balance = LedgerBalanceCalculator.NextBalance(
            previousBalance: 500m,
            billAmount: 0m,
            paidAmount: 800m,
            confirmOverpayment: true);

        balance.Should().Be(-300m);
    }

    // --- input guards ---

    [Fact]
    public void Rejects_a_negative_bill()
    {
        var act = () => LedgerBalanceCalculator.NextBalance(0m, -1m, 0m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    [Fact]
    public void Rejects_a_negative_payment()
    {
        var act = () => LedgerBalanceCalculator.NextBalance(0m, 0m, -1m);

        act.Should().Throw<BusinessRuleViolationException>();
    }

    // --- precision (research.md R5) ---

    [Fact]
    public void Keeps_paisa_precision_across_many_entries()
    {
        var entries = Enumerable.Range(0, 3).Select(_ => new LedgerMovement(0.10m, 0m)).ToArray();

        var balances = LedgerBalanceCalculator.RunningBalances(0m, entries).ToArray();

        balances[^1].Should().Be(0.30m);
    }
}
