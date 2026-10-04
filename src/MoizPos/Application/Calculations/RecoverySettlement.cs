namespace MoizPos.Application.Calculations;

/// <summary>One movement in a customer's ledger, on the shop-local day it happened.</summary>
/// <param name="EntryType">Invoice, Payment, SaleReturn, OpeningBalance or Adjustment.</param>
/// <param name="ReferenceNumber">The invoice or receipt number, where there is one.</param>
public readonly record struct RecoveryMovement(
    DateOnly OnDate, string EntryType, long? ReferenceId, string? ReferenceNumber, decimal BillAmount, decimal PaidAmount);

public enum OpenBillStatus
{
    /// <summary>Nothing has gone against it yet.</summary>
    NotPaid,

    /// <summary>Some of it is paid; the rest is still owed.</summary>
    PartPaid,
}

/// <summary>A bill still owed, and how much of it.</summary>
public sealed record OpenBill(
    DateOnly OnDate, string EntryType, long? ReferenceId, string? ReferenceNumber, decimal BillAmount, decimal Remaining,
    OpenBillStatus Status);

/// <summary>
/// Which of a customer's bills are still open — <b>payments settle the oldest debt first</b>, the
/// rule <see cref="UdhaarDueDate"/> and the commission already follow. A customer's account is
/// one balance, not a set of invoices with their own; this only says which bills that balance is
/// made of, so the shop can say "the 10 August bill, Rs 300 left".
///
/// <para>Pure, so the rule can be argued about without a database in the way.</para>
/// </summary>
public static class RecoverySettlement
{
    /// <param name="movements">The customer's ledger, in the order it was written.</param>
    public static IReadOnlyList<OpenBill> OpenBills(IEnumerable<RecoveryMovement> movements)
    {
        var ordered = movements.ToList();

        // Everything that ever reduced the debt: payments and returns, money paid at a sale, and
        // a correction that lowered what was owed (a negative bill).
        var credit = ordered.Sum(movement => movement.PaidAmount)
                     + ordered.Where(movement => movement.BillAmount < 0m).Sum(movement => -movement.BillAmount);

        var open = new List<OpenBill>();

        foreach (var bill in ordered.Where(movement => movement.BillAmount > 0m))
        {
            var applied = Math.Min(credit, bill.BillAmount);
            credit -= applied;

            var remaining = bill.BillAmount - applied;

            if (remaining > 0m)
            {
                open.Add(new OpenBill(
                    bill.OnDate, bill.EntryType, bill.ReferenceId, bill.ReferenceNumber, bill.BillAmount, remaining,
                    applied > 0m ? OpenBillStatus.PartPaid : OpenBillStatus.NotPaid));
            }
        }

        return open;
    }
}
