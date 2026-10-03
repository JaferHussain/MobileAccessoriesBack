using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Ledger;

/// <summary>
/// A supplier's account, read the way the owner's own book reads: what was bought, what went
/// back, what was paid, and what is owed after each.
///
/// <para>Worked out from the purchases, returns and payments the shop already records — there is
/// no second table to keep in step. The last balance is, by construction, the payable balance:
/// purchases − returns − payments, the invariant the database suite already asserts.</para>
/// </summary>
public sealed class SupplierLedgerTests
{
    private static readonly DateTime Sep1 = new(2026, 9, 1, 5, 0, 0, DateTimeKind.Utc);

    private static SupplierLedgerRow Purchase(int day, decimal total, long id = 1) => new()
    {
        EntryType = SupplierLedgerEntryType.Purchase,
        ReferenceId = id,
        EntryDateUtc = Sep1.AddDays(day - 1),
        BillAmount = total,
    };

    private static SupplierLedgerRow Return(int day, decimal total, long id = 1) => new()
    {
        EntryType = SupplierLedgerEntryType.Return,
        ReferenceId = id,
        EntryDateUtc = Sep1.AddDays(day - 1),
        ReturnedAmount = total,
    };

    private static SupplierLedgerRow Payment(int day, decimal amount, long id = 1) => new()
    {
        EntryType = SupplierLedgerEntryType.Payment,
        ReferenceId = id,
        EntryDateUtc = Sep1.AddDays(day - 1),
        PaidAmount = amount,
    };

    [Fact]
    public void Each_line_carries_what_is_owed_after_it()
    {
        // The owner's own worked example: buy 100,000, send 10,000 back, pay 20,000.
        var entries = SupplierLedger.Build([Purchase(1, 100_000m), Return(10, 10_000m), Payment(15, 20_000m)]);

        entries.Select(entry => entry.BalanceAfter).Should().Equal(100_000m, 90_000m, 70_000m);
    }

    [Fact]
    public void Lines_are_put_in_date_order_whatever_order_they_were_read_in()
    {
        // Read from three tables, so they arrive grouped by kind, not by date.
        var entries = SupplierLedger.Build([Payment(15, 20_000m), Purchase(1, 100_000m), Return(10, 10_000m)]);

        entries.Select(entry => entry.EntryType).Should().Equal(
            SupplierLedgerEntryType.Purchase, SupplierLedgerEntryType.Return, SupplierLedgerEntryType.Payment);
        entries[^1].BalanceAfter.Should().Be(70_000m);
    }

    [Fact]
    public void On_the_same_moment_goods_come_in_before_money_goes_out()
    {
        // Two rows stamped identically must still read sensibly: a payment listed before the
        // purchase it paid for would show the balance dipping below zero for one line.
        var sameMoment = Sep1;
        var entries = SupplierLedger.Build(
        [
            Payment(1, 500m) with { EntryDateUtc = sameMoment },
            Purchase(1, 500m) with { EntryDateUtc = sameMoment },
        ]);

        entries[0].EntryType.Should().Be(SupplierLedgerEntryType.Purchase);
        entries.Select(entry => entry.BalanceAfter).Should().Equal(500m, 0m);
    }

    [Fact]
    public void A_confirmed_overpayment_shows_the_supplier_owing_the_shop()
    {
        SupplierLedger.Build([Purchase(1, 1_000m), Payment(2, 1_500m)])[^1]
            .BalanceAfter.Should().Be(-500m);
    }

    [Fact]
    public void Totals_add_up_to_the_balance()
    {
        var entries = SupplierLedger.Build(
            [Purchase(1, 100_000m, 1), Purchase(5, 50_000m, 2), Return(10, 10_000m), Payment(15, 20_000m)]);

        var totals = SupplierLedger.Totals(entries);

        totals.Purchased.Should().Be(150_000m);
        totals.Returned.Should().Be(10_000m);
        totals.Paid.Should().Be(20_000m);
        totals.Owed.Should().Be(120_000m);
        totals.Owed.Should().Be(totals.Purchased - totals.Returned - totals.Paid);
    }

    [Fact]
    public void A_supplier_with_no_dealings_has_an_empty_account()
    {
        SupplierLedger.Build([]).Should().BeEmpty();
        SupplierLedger.Totals([]).Owed.Should().Be(0m);
    }
}
