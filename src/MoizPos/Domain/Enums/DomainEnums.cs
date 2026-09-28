namespace MoizPos.Domain.Enums;

/// <summary>Roles recognised by the system. Admin sees cost and profit; Staff never does.</summary>
public enum UserRole
{
    Admin = 1,
    Staff = 2,
}

/// <summary>
/// Whether a sale was made over the counter or in bulk to another shopkeeper.
///
/// Recorded on the invoice rather than inferred from the price charged: a discounted retail sale
/// and a wholesale sale can reach the same figure, and the owner's day-end split has to be right.
/// </summary>
public enum SaleType
{
    Retail = 1,
    Wholesale = 2,
}

/// <summary>How a sale or payment was settled (FR-014).</summary>
public enum PaymentMethod
{
    Cash = 1,
    BankTransfer = 2,
    JazzCash = 3,
    EasyPaisa = 4,
    Raast = 5,

    /// <summary>Wholly on credit (udhaar) — nothing paid at the counter.</summary>
    Credit = 6,

    /// <summary>Split settlement: part paid now, remainder on credit.</summary>
    Partial = 7,
}

/// <summary>Why a product's quantity changed (FR-005).</summary>
public enum StockMovementReason
{
    Purchase = 1,
    Sale = 2,
    SaleReturn = 3,
    PurchaseReturn = 4,
    Adjustment = 5,
}

/// <summary>The kind of movement recorded in a customer's ledger (FR-020).</summary>
public enum LedgerEntryType
{
    Invoice = 1,
    Payment = 2,
    SaleReturn = 3,
    Adjustment = 4,

    /// <summary>
    /// What the customer already owed before this software was in use, carried over from the
    /// shop's paper register (FR-065). Appended, so no persisted value is renumbered.
    /// </summary>
    OpeningBalance = 5,
}

/// <summary>Document kinds that can be rendered as a PDF and shared (FR-042, FR-043).</summary>
public enum DocumentType
{
    Invoice = 1,
    PaymentReceipt = 2,
}

/// <summary>Reporting period presets offered by the dashboard (FR-035).</summary>
public enum DashboardPeriod
{
    Today = 1,
    ThisMonth = 2,
    ThisYear = 3,
}

/// <summary>Rollup granularity for profit and sales reports (FR-033).</summary>
public enum ReportGrouping
{
    Day = 1,
    Week = 2,
    Month = 3,
    Year = 4,
}

/// <summary>
/// Where an expense's money came from.
///
/// <para>Only <see cref="Till"/> leaves the cash drawer, so only it is subtracted at day close.
/// Money paid from the bank never entered the drawer and must not make it look short.</para>
/// </summary>
public enum PaymentSource
{
    /// <summary>Notes taken out of the drawer.</summary>
    Till = 1,

    /// <summary>Paid from the shop's account; the drawer never saw it.</summary>
    Bank = 2,
}
