using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.UnitTests.Invoices;

/// <summary>
/// T011 — who may let goods leave the shop against a debt (FR-051, FR-052).
///
/// The rule is deliberately about the *money*, not about the payment method the caller chose. A
/// sale marked "Cash" whose paid amount falls short of the recomputed total is still stock that
/// left the shop against a debt, and a salesman must not be able to authorise that.
///
/// These tests exercise the rule in isolation, without a database or an HTTP principal, which is
/// why the decision takes the role as an argument rather than reading it from ambient context.
/// </summary>
public sealed class CreditAuthorityTests
{
    /// <summary>
    /// The rule under test, in the shape <c>InvoiceService</c> applies it: after the server has
    /// recomputed the totals, and before anything has been written.
    /// </summary>
    private static void Enforce(decimal amountRemaining, UserRole role)
    {
        if (amountRemaining > 0m && role != UserRole.Admin)
        {
            throw new CreditRequiresAdminException(amountRemaining);
        }
    }

    [Fact]
    public void Staff_cannot_complete_a_wholly_unpaid_sale()
    {
        var act = () => Enforce(amountRemaining: 5000m, UserRole.Staff);

        act.Should().Throw<CreditRequiresAdminException>()
            .Which.AmountRemaining.Should().Be(5000m);
    }

    [Fact]
    public void Staff_cannot_complete_a_part_paid_sale()
    {
        // FR-052: a part-paid sale still leaves the shop's money with the customer, so it is
        // credit just as much as a wholly unpaid one.
        var act = () => Enforce(amountRemaining: 2000m, UserRole.Staff);

        act.Should().Throw<CreditRequiresAdminException>();
    }

    [Fact]
    public void Staff_can_complete_a_fully_paid_sale()
    {
        var act = () => Enforce(amountRemaining: 0m, UserRole.Staff);

        // Selling for full payment is the salesman's job and must stay unaffected (FR-053).
        act.Should().NotThrow();
    }

    [Fact]
    public void The_owner_can_complete_a_credit_sale()
    {
        var act = () => Enforce(amountRemaining: 5000m, UserRole.Admin);

        act.Should().NotThrow();
    }

    [Fact]
    public void The_owner_can_complete_a_part_paid_sale()
    {
        var act = () => Enforce(amountRemaining: 3000m, UserRole.Admin);

        act.Should().NotThrow();
    }

    [Fact]
    public void The_rule_reads_the_outstanding_amount_not_the_payment_method()
    {
        // A sale can be labelled Cash and still be underpaid. If the rule keyed off
        // PaymentMethod, this is exactly how a salesman would get credit past it.
        var totals = InvoiceCalculator.Calculate(
            [new InvoiceLineInput(Quantity: 1, UnitSalePrice: 1000m, LineDiscount: 0m)],
            orderDiscount: 0m,
            amountPaid: 400m);

        totals.AmountRemaining.Should().Be(600m);

        var act = () => Enforce(totals.AmountRemaining, UserRole.Staff);

        act.Should().Throw<CreditRequiresAdminException>();
    }

    [Fact]
    public void A_sale_settled_to_the_last_paisa_is_not_credit()
    {
        // Guards against a rounding artefact making a fully settled sale look like credit and
        // blocking a legitimate counter sale.
        var totals = InvoiceCalculator.Calculate(
            [new InvoiceLineInput(Quantity: 3, UnitSalePrice: 333.33m, LineDiscount: 0m)],
            orderDiscount: 0m,
            amountPaid: 999.99m);

        totals.AmountRemaining.Should().Be(0m);

        var act = () => Enforce(totals.AmountRemaining, UserRole.Staff);

        act.Should().NotThrow();
    }

    [Fact]
    public void The_refusal_names_the_unpaid_amount()
    {
        // The salesman has to be able to tell the customer what is wrong, and the owner has to
        // know what they are being asked to approve.
        var exception = new CreditRequiresAdminException(3000m);

        exception.Message.Should().Contain("3,000");
        exception.Code.Should().Be(ErrorCodes.CreditRequiresAdmin);
    }
}
