namespace MoizPos.Application.Calculations;

/// <summary>What moved a supplier's account.</summary>
public enum SupplierLedgerEntryType
{
    /// <summary>Goods bought — the shop owes more.</summary>
    Purchase,

    /// <summary>Goods sent back — the shop owes less.</summary>
    Return,

    /// <summary>Money paid to the supplier — the shop owes less.</summary>
    Payment,
}

/// <summary>
/// One purchase, return or payment as read from its own table. Init-only properties, never a
/// positional record: Dapper materialises it, and ids are unsigned in MySQL.
/// </summary>
public sealed record SupplierLedgerRow
{
    public SupplierLedgerEntryType EntryType { get; init; }

    /// <summary>The purchase, return or payment id.</summary>
    public long ReferenceId { get; init; }

    public DateTime EntryDateUtc { get; init; }

    /// <summary>The goods bought or sent back. Null for a payment.</summary>
    public string? ProductName { get; init; }

    public int? Quantity { get; init; }

    /// <summary>A return's own number (RTN-…). Null otherwise.</summary>
    public string? ReferenceNumber { get; init; }

    /// <summary>How a payment was made — Cash, BankTransfer… Null otherwise.</summary>
    public string? PaymentMethod { get; init; }

    /// <summary>A payment's note (a cheque or transaction number) or a return's reason.</summary>
    public string? Note { get; init; }

    public decimal BillAmount { get; init; }

    public decimal ReturnedAmount { get; init; }

    public decimal PaidAmount { get; init; }

    /// <summary>Whether a payment's proof is attached. Always false for goods.</summary>
    public bool HasProof { get; init; }

    /// <summary>The shop account a payment left, when one was named.</summary>
    public string? ShopAccountName { get; init; }
}

/// <summary>One line of the account, with what was owed once it had happened.</summary>
public sealed record SupplierLedgerEntry
{
    public SupplierLedgerEntryType EntryType { get; init; }

    public long ReferenceId { get; init; }

    public DateTime EntryDateUtc { get; init; }

    public string? ProductName { get; init; }

    public int? Quantity { get; init; }

    public string? ReferenceNumber { get; init; }

    public string? PaymentMethod { get; init; }

    public string? Note { get; init; }

    public decimal BillAmount { get; init; }

    public decimal ReturnedAmount { get; init; }

    public decimal PaidAmount { get; init; }

    public bool HasProof { get; init; }

    public string? ShopAccountName { get; init; }

    public decimal BalanceAfter { get; init; }
}

public readonly record struct SupplierLedgerTotals(decimal Purchased, decimal Returned, decimal Paid)
{
    /// <summary>Purchased − returned − paid: the payable balance, by the schema's own invariant.</summary>
    public decimal Owed => Purchased - Returned - Paid;
}

/// <summary>
/// A supplier's account, read the way the owner's own book reads.
///
/// <para><b>Derived, never stored.</b> Built from the purchases, returns and payments the shop
/// already records, so there is no second table to keep in step with them. Its last balance is
/// the payable balance by construction — the invariant <c>DataInvariantTests</c> asserts over
/// the whole database.</para>
///
/// <para>Pure, so the running balance is tested without a database.</para>
/// </summary>
public static class SupplierLedger
{
    public static IReadOnlyList<SupplierLedgerEntry> Build(IEnumerable<SupplierLedgerRow> rows)
    {
        var balance = 0m;

        // Three tables arrive grouped by kind; the account reads by date. On an identical
        // instant goods come in before they go back, and before money goes out, so the balance
        // never dips through zero for a line that happened "at the same time".
        return rows
            .OrderBy(row => row.EntryDateUtc)
            .ThenBy(row => row.EntryType)
            .ThenBy(row => row.ReferenceId)
            .Select(row =>
            {
                balance += row.BillAmount - row.ReturnedAmount - row.PaidAmount;

                return new SupplierLedgerEntry
                {
                    EntryType = row.EntryType,
                    ReferenceId = row.ReferenceId,
                    EntryDateUtc = row.EntryDateUtc,
                    ProductName = row.ProductName,
                    Quantity = row.Quantity,
                    ReferenceNumber = row.ReferenceNumber,
                    PaymentMethod = row.PaymentMethod,
                    Note = row.Note,
                    BillAmount = row.BillAmount,
                    ReturnedAmount = row.ReturnedAmount,
                    PaidAmount = row.PaidAmount,
                    HasProof = row.HasProof,
                    ShopAccountName = row.ShopAccountName,
                    BalanceAfter = balance,
                };
            })
            .ToList();
    }

    public static SupplierLedgerTotals Totals(IEnumerable<SupplierLedgerEntry> entries)
    {
        var list = entries.ToList();

        return new SupplierLedgerTotals(
            list.Sum(entry => entry.BillAmount),
            list.Sum(entry => entry.ReturnedAmount),
            list.Sum(entry => entry.PaidAmount));
    }
}
