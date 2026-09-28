using MoizPos.Domain.Enums;

namespace MoizPos.Domain.Entities;

/// <summary>A person who signs in. See data-model.md §1.</summary>
public sealed class User
{
    public long Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A party the shop buys from. See data-model.md §3.</summary>
public sealed class Supplier
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? ContactNumber { get; set; }

    public string? Address { get; set; }

    /// <summary>Stored exactly as entered — the supplier's own paperwork sets the format.</summary>
    public string? Cnic { get; set; }

    public string? Email { get; set; }

    // Read together when paying an invoice, which is why they are named and grouped as a set.
    public string? BankName { get; set; }

    public string? BankAccountTitle { get; set; }

    public string? BankAccountNumber { get; set; }

    public string? Notes { get; set; }

    /// <summary>Running total of what the shop owes. Maintained only inside transactions.</summary>
    public decimal PayableBalance { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A product category — "Cables", "Chargers", "Covers". Maintained by the owner as its own
/// module so a rename happens in one place instead of across every product row.
/// </summary>
public sealed class Category
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A product brand — "Samsung", "Anker", "Generic". Same reasoning as <see cref="Category"/>:
/// free text produced "Samsung", "samsung" and "Samsng" as three separate brands.
/// </summary>
public sealed class Brand
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// True when the owner has marked this brand as locally made (FR-087a). Defaults to false, so
    /// a brand is Imported until the owner says otherwise.
    /// </summary>
    public bool IsLocal { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One distinct sellable variant. Each variant is its own row — five kinds of USB cable are five
/// products, not one product with options (FR-001). See data-model.md §4.
/// </summary>
public sealed class Product
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The category this product belongs to. Required — every product is filed somewhere.</summary>
    public long CategoryId { get; set; }

    /// <summary>Optional: unbranded generic stock is normal in this trade.</summary>
    public long? BrandId { get; set; }

    public string? Model { get; set; }

    public string? Barcode { get; set; }

    public string? ImagePath { get; set; }

    /// <summary>
    /// The latest purchase cost. Overwritten on every purchase and applied to ALL stock on hand,
    /// per the owner's latest-cost rule (FR-011a). Never exposed to a Staff principal (FR-040).
    /// </summary>
    public decimal CostPrice { get; set; }

    public decimal WholesalePrice { get; set; }

    public decimal RetailPrice { get; set; }


    public int QuantityOnHand { get; set; }

    public int MinStockThreshold { get; set; }

    public long? SupplierId { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>An append-only record of why a product's quantity changed. See data-model.md §5.</summary>
public sealed class StockMovement
{
    public long Id { get; set; }

    public long ProductId { get; set; }

    /// <summary>Signed: positive for stock in, negative for stock out.</summary>
    public int ChangeQty { get; set; }

    public int ResultingQty { get; set; }

    public StockMovementReason Reason { get; set; }

    public long? ReferenceId { get; set; }

    public long UserId { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A person the shop sells to. See data-model.md §6.</summary>
public sealed class Customer
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? MobileNumber { get; set; }

    public string? Address { get; set; }

    /// <summary>
    /// A standing label the owner sets (FR-101, feature 004), defaulting to Retail. Never
    /// derived from invoices: a wholesale party's occasional counter purchase must not
    /// reclassify them, and a customer with no sales yet must not have a type invented.
    /// </summary>
    public SaleType SaleType { get; set; } = SaleType.Retail;

    /// <summary>Always equals the latest ledger entry's BalanceAfter (invariant 2).</summary>
    public decimal OutstandingBalance { get; set; }

    /// <summary>
    /// What this customer already owed before the software was in use, carried over from the
    /// shop's paper register (FR-065). Included in <see cref="OutstandingBalance"/>, not added on
    /// top of it. Null means none was ever recorded, which is not the same as a recorded zero.
    /// </summary>
    public decimal? OpeningBalance { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>One sale. All monetary fields are recomputed server-side. See data-model.md §7.</summary>
public sealed class Invoice
{
    public long Id { get; set; }

    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>Null only for a fully paid walk-in sale (FR-017).</summary>
    public long? CustomerId { get; set; }

    public DateTime InvoiceDateUtc { get; set; }

    /// <summary>Counter sale or bulk sale to another shopkeeper. Drives the day-end split.</summary>
    public SaleType SaleType { get; set; } = SaleType.Retail;

    public decimal Subtotal { get; set; }

    public decimal OrderDiscount { get; set; }

    public decimal Total { get; set; }

    public decimal AmountPaid { get; set; }

    public decimal AmountRemaining { get; set; }

    /// <summary>Total less the value of any sale returns (FR-024).</summary>
    public decimal NetAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    /// <summary>
    /// A screenshot backing a non-cash payment (feature 008). Null is ordinary and permanent:
    /// the picture is optional, and cash sales never have one.
    /// </summary>
    public string? PaymentProofPath { get; set; }

    /// <summary>
    /// The CUSTOMER's account — where a non-cash payment came from (migration 0021). Optional
    /// and permanently nullable: the counter must not wait while somebody hunts for it. A cash
    /// sale never has one, because money in the drawer came from no account.
    /// </summary>
    public string? PaymentAccountNumber { get; set; }

    /// <summary>Their reference for that transfer. Same rules as the account number.</summary>
    public string? PaymentTransactionId { get; set; }

    public long UserId { get; set; }
}

/// <summary>One product on an invoice. See data-model.md §8.</summary>
public sealed class InvoiceItem
{
    public long Id { get; set; }

    public long InvoiceId { get; set; }

    public long ProductId { get; set; }

    /// <summary>Snapshot, so renaming or deactivating a product leaves old invoices readable.</summary>
    public string ProductName { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal UnitSalePrice { get; set; }

    public decimal LineDiscount { get; set; }

    /// <summary>
    /// Snapshot of the product's cost at the moment of sale. This is what makes profit history
    /// stable: a later purchase changing the product's cost must never rewrite it (FR-011c).
    /// </summary>
    public decimal UnitCostPrice { get; set; }

    public decimal LineTotal { get; set; }

    public int ReturnedQty { get; set; }
}

/// <summary>One movement in a customer's account. Append-only. See data-model.md §9.</summary>
public sealed class LedgerEntry
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public DateTime EntryDateUtc { get; set; }

    public LedgerEntryType EntryType { get; set; }

    public long? ReferenceId { get; set; }

    public decimal BillAmount { get; set; }

    public decimal PaidAmount { get; set; }

    /// <summary>Previous balance + BillAmount − PaidAmount (FR-020).</summary>
    public decimal BalanceAfter { get; set; }

    public long UserId { get; set; }
}

/// <summary>Money received from a customer against their balance. See data-model.md §10.</summary>
public sealed class CustomerPayment
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public string ReceiptNumber { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public DateTime PaymentDateUtc { get; set; }

    /// <summary>True only when the caller explicitly confirmed an overpayment (FR-022).</summary>
    public bool IsOverpayment { get; set; }

    public string? Note { get; set; }

    public long UserId { get; set; }
}

/// <summary>Goods received from a supplier. See data-model.md §11.</summary>
public sealed class Purchase
{
    public long Id { get; set; }

    public long SupplierId { get; set; }

    public long ProductId { get; set; }

    /// <summary>Joined for display — "Return item" and the Purchases screen both need the
    /// exact product name, not just its id.</summary>
    public string ProductName { get; set; } = string.Empty;

    public DateTime PurchaseDateUtc { get; set; }

    public decimal UnitCost { get; set; }

    public int Quantity { get; set; }

    public decimal Total { get; set; }

    public int ReturnedQty { get; set; }

    public long UserId { get; set; }
}

/// <summary>An outgoing cost that reduces net profit (FR-030). See data-model.md §15.</summary>
public sealed class Expense
{
    public long Id { get; set; }

    public long CategoryId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>
    /// Where the money came from. Nullable only because rows predating migration 0022 have no
    /// answer recorded; every new expense must state one, and the drawer counts only Till.
    /// </summary>
    public PaymentSource? PaymentSource { get; set; }

    public DateTime ExpenseDateUtc { get; set; }

    public string? Note { get; set; }

    public long UserId { get; set; }
}

/// <summary>Who changed what stock or balance value, from what to what, and when (FR-041).</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public long EntityId { get; set; }

    public string FieldName { get; set; } = string.Empty;

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string Action { get; set; } = string.Empty;

    public long UserId { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
