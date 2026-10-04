namespace MoizPos.Application.Calculations;

/// <summary>Every kind of transaction that can carry a proof of payment.</summary>
public enum ProofKind
{
    /// <summary>A sale paid by other than cash. <c>invoices.payment_proof_path</c>.</summary>
    Sale,

    /// <summary>Udhaar recovered from a customer. <c>customer_payments.payment_proof_path</c>.</summary>
    CustomerPayment,

    /// <summary>A supplier paid. <c>supplier_payments.payment_proof_path</c>.</summary>
    SupplierPayment,

    /// <summary>Money handed back after a sale return. <c>sale_returns.refund_proof_path</c>.</summary>
    Refund,

    /// <summary>An expense paid from the bank. <c>expenses.payment_proof_path</c>.</summary>
    Expense,

    /// <summary>Money a field salesman handed over by transfer. <c>salesman_handovers.payment_proof_path</c>.</summary>
    SalesmanHandover,

    /// <summary>Commission paid to a salesman by transfer. <c>commission_payouts.payment_proof_path</c>.</summary>
    CommissionPayout,

    /// <summary>
    /// A photo of a supplier's bill. <c>purchase_bills.bill_image_path</c>. Not a payment — the
    /// bill itself — so it is never refused for how it was paid, and never on Proof missing.
    /// </summary>
    PurchaseBill,
}

/// <summary>
/// Which transactions take a proof — the owner's rule: <b>every one except cash</b>.
///
/// <para>Cash is its own proof: it was counted into or out of the drawer, and a "proof" there would
/// be evidence of nothing. Every other way money moves left a screenshot somewhere, and that
/// screenshot is what settles a dispute. Pure, so the rule is tested without a database.</para>
/// </summary>
public static class TransactionProofRules
{
    private static readonly Dictionary<string, ProofKind> Slugs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sale"] = ProofKind.Sale,
        ["customer-payment"] = ProofKind.CustomerPayment,
        ["supplier-payment"] = ProofKind.SupplierPayment,
        ["refund"] = ProofKind.Refund,
        ["expense"] = ProofKind.Expense,
        ["salesman-handover"] = ProofKind.SalesmanHandover,
        ["commission-payout"] = ProofKind.CommissionPayout,
        ["purchase-bill"] = ProofKind.PurchaseBill,
    };

    /// <summary>The kind's name in a URL: <c>/api/proofs/{slug}/{id}</c>.</summary>
    public static string Slug(ProofKind kind) => Slugs.First(pair => pair.Value == kind).Key;

    public static bool TryParse(string? slug, out ProofKind kind) =>
        Slugs.TryGetValue(slug ?? string.Empty, out kind);

    /// <summary>
    /// Supplier payments, expenses and salesman handovers are the owner's money and the owner's
    /// screens — the owner records a handover, never the salesman. Sales, recoveries and refunds
    /// are taken by the salesman, so he attaches their proofs.
    /// </summary>
    public static bool IsAdminOnly(ProofKind kind) =>
        kind is ProofKind.SupplierPayment or ProofKind.Expense or ProofKind.SalesmanHandover or ProofKind.CommissionPayout
            or ProofKind.PurchaseBill;

    /// <summary>
    /// Why this transaction cannot take a proof, in words for the shopkeeper — or null when it can.
    /// </summary>
    /// <param name="method">
    /// How the money moved, as stored: a payment method (<c>Cash</c>, <c>BankTransfer</c>…), an
    /// expense's source (<c>Till</c> or <c>Bank</c>), or null for a return that refunded nothing.
    /// </param>
    public static string? RefusalFor(ProofKind kind, string? method) => kind switch
    {
        ProofKind.Refund when method is null =>
            "Nothing was refunded on this return — it only reduced what the customer owed.",

        ProofKind.Expense when method == "Till" =>
            "An expense paid from the till needs no proof — the money left the drawer.",

        ProofKind.Sale when method == "Cash" =>
            "A Cash sale needs no payment proof — the money was taken at the counter.",

        ProofKind.CustomerPayment when method == "Cash" =>
            "A cash payment needs no proof — it was counted into the drawer.",

        ProofKind.SupplierPayment when method == "Cash" =>
            "A cash payment to a supplier needs no proof — it was paid from the drawer.",

        ProofKind.Refund when method == "Cash" =>
            "A cash refund needs no proof — it was paid from the drawer.",

        ProofKind.SalesmanHandover when method == "Cash" =>
            "A cash handover needs no proof — it was counted into the drawer.",

        ProofKind.CommissionPayout when method == "Cash" =>
            "Commission paid in cash needs no proof — it was paid from the drawer.",

        _ => null,
    };
}
