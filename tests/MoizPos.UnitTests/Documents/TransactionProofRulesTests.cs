using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Documents;

/// <summary>
/// Which transactions take a proof. The owner's rule: every one except cash.
///
/// <para>Cash is its own proof — it was counted into or out of the drawer, and a "proof" there
/// would be evidence of nothing. Everything else — a transfer, JazzCash, EasyPaisa, Raast — left a
/// screenshot somewhere, and that screenshot is what settles a dispute.</para>
/// </summary>
public sealed class TransactionProofRulesTests
{
    [Theory]
    [InlineData(ProofKind.Sale)]
    [InlineData(ProofKind.CustomerPayment)]
    [InlineData(ProofKind.SupplierPayment)]
    [InlineData(ProofKind.Refund)]
    public void Cash_is_refused_a_proof_everywhere(ProofKind kind)
    {
        TransactionProofRules.RefusalFor(kind, "Cash").Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(ProofKind.Sale, "BankTransfer")]
    [InlineData(ProofKind.CustomerPayment, "JazzCash")]
    [InlineData(ProofKind.SupplierPayment, "EasyPaisa")]
    [InlineData(ProofKind.Refund, "Raast")]
    [InlineData(ProofKind.Expense, "Bank")]
    public void Every_other_way_of_moving_money_takes_one(ProofKind kind, string method)
    {
        TransactionProofRules.RefusalFor(kind, method).Should().BeNull();
    }

    [Fact]
    public void An_expense_paid_from_the_till_is_cash()
    {
        // Expenses record where the money came from, not a payment method: Till is the drawer.
        TransactionProofRules.RefusalFor(ProofKind.Expense, "Till").Should().Contain("till");
    }

    [Fact]
    public void A_return_that_refunded_nothing_has_nothing_to_prove()
    {
        // It only reduced what the customer owed — no money changed hands.
        TransactionProofRules.RefusalFor(ProofKind.Refund, null).Should().Contain("Nothing was refunded");
    }

    [Theory]
    [InlineData(ProofKind.Sale, false)]
    [InlineData(ProofKind.CustomerPayment, false)]
    [InlineData(ProofKind.Refund, false)]
    [InlineData(ProofKind.SupplierPayment, true)]
    [InlineData(ProofKind.Expense, true)]
    public void Supplier_payments_and_expenses_are_the_owners_alone(ProofKind kind, bool adminOnly)
    {
        // The salesman takes sales, recoveries and refunds, so he attaches their proofs. Supplier
        // payments and expenses are the owner's money and the owner's screens.
        TransactionProofRules.IsAdminOnly(kind).Should().Be(adminOnly);
    }

    [Theory]
    [InlineData("sale", ProofKind.Sale)]
    [InlineData("customer-payment", ProofKind.CustomerPayment)]
    [InlineData("supplier-payment", ProofKind.SupplierPayment)]
    [InlineData("refund", ProofKind.Refund)]
    [InlineData("expense", ProofKind.Expense)]
    public void Each_kind_has_one_name_in_a_url(string slug, ProofKind kind)
    {
        TransactionProofRules.TryParse(slug, out var parsed).Should().BeTrue();
        parsed.Should().Be(kind);
        TransactionProofRules.Slug(kind).Should().Be(slug);
    }

    [Fact]
    public void An_unknown_kind_is_not_guessed_at()
    {
        TransactionProofRules.TryParse("purchase", out _).Should().BeFalse();
    }
}
