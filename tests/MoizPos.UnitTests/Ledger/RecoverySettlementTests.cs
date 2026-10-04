using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Ledger;

/// <summary>
/// Which of a customer's bills are still open, and how much of each — payments settle the OLDEST
/// debt first, the same rule the due date and the commission already follow.
/// </summary>
public sealed class RecoverySettlementTests
{
    private static readonly DateOnly Day1 = new(2026, 8, 1);
    private static readonly DateOnly Day2 = new(2026, 8, 10);
    private static readonly DateOnly Day3 = new(2026, 8, 20);

    private static RecoveryMovement Bill(DateOnly on, decimal amount, decimal paidAtSale = 0m, string reference = "INV") =>
        new(on, "Invoice", 1, reference, amount, paidAtSale);

    private static RecoveryMovement Payment(DateOnly on, decimal amount) => new(on, "Payment", 2, "RCP", 0m, amount);

    [Fact]
    public void A_bill_nothing_was_paid_on_is_open_in_full()
    {
        var open = RecoverySettlement.OpenBills([Bill(Day1, 1000m)]);

        open.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            OnDate = Day1,
            BillAmount = 1000m,
            Remaining = 1000m,
            Status = OpenBillStatus.NotPaid,
        });
    }

    [Fact]
    public void A_bill_part_paid_at_the_counter_is_open_for_the_rest()
    {
        var open = RecoverySettlement.OpenBills([Bill(Day1, 1000m, paidAtSale: 400m)]);

        open.Single().Remaining.Should().Be(600m);
        open.Single().Status.Should().Be(OpenBillStatus.PartPaid);
    }

    [Fact]
    public void A_payment_settles_the_oldest_bill_first()
    {
        var open = RecoverySettlement.OpenBills([
            Bill(Day1, 1000m, reference: "OLD"),
            Bill(Day2, 500m, reference: "NEW"),
            Payment(Day3, 1200m),
        ]);

        // 1,200 clears the old 1,000 and 200 of the new one.
        open.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ReferenceNumber = "NEW",
            Remaining = 300m,
            Status = OpenBillStatus.PartPaid,
        });
    }

    [Fact]
    public void Money_paid_on_a_later_bill_still_settles_the_older_one_first()
    {
        // 300 handed over with the second sale goes against the first debt — the shop's one rule.
        var open = RecoverySettlement.OpenBills([
            Bill(Day1, 1000m, reference: "OLD"),
            Bill(Day2, 500m, paidAtSale: 300m, reference: "NEW"),
        ]);

        open.Select(bill => (bill.ReferenceNumber, bill.Remaining, bill.Status)).Should().Equal(
            ("OLD", 700m, OpenBillStatus.PartPaid),
            ("NEW", 500m, OpenBillStatus.NotPaid));
    }

    [Fact]
    public void A_settled_account_has_no_open_bills() =>
        RecoverySettlement.OpenBills([Bill(Day1, 1000m), Payment(Day2, 1000m)]).Should().BeEmpty();

    [Fact]
    public void A_correction_that_lowers_what_was_owed_counts_as_money_off_the_oldest_bill()
    {
        // An opening balance corrected downward is a negative bill: it reduces the debt like a payment.
        var open = RecoverySettlement.OpenBills([
            new RecoveryMovement(Day1, "OpeningBalance", null, null, 2000m, 0m),
            new RecoveryMovement(Day2, "Adjustment", null, null, -500m, 0m),
        ]);

        open.Single().Remaining.Should().Be(1500m);
    }
}
