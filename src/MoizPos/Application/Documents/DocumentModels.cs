namespace MoizPos.Application.Documents;

/// <summary>Shop identity printed on every document (FR-042).</summary>
public sealed record ShopDetails
{
    public string Name { get; init; } = "Moiz Mobile & Corporation";

    public string Location { get; init; } = "Danwran Lodhran";

    public string? ContactNumber { get; init; }
}

public sealed record InvoiceDocumentLine(
    string ProductName,
    int Quantity,
    decimal UnitSalePrice,
    decimal LineDiscount,
    decimal LineTotal);

/// <summary>
/// Everything an invoice PDF must show (FR-042). Assembled separately from rendering so the
/// content can be unit-tested without producing a file.
/// </summary>
public sealed record InvoiceDocument
{
    public ShopDetails Shop { get; init; } = new();

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateTime IssuedAtLocal { get; init; }

    public string CustomerName { get; init; } = "Walk-in customer";

    public string? CustomerMobile { get; init; }

    public IReadOnlyList<InvoiceDocumentLine> Lines { get; init; } = [];

    public decimal Subtotal { get; init; }

    public decimal OrderDiscount { get; init; }

    public decimal Total { get; init; }

    public decimal AmountPaid { get; init; }

    public decimal AmountRemaining { get; init; }

    public string PaymentMethod { get; init; } = string.Empty;

    public string FooterMessage { get; init; } = "Thank you for your business.";
}

/// <summary>Everything a payment receipt must show (FR-043).</summary>
public sealed record ReceiptDocument
{
    public ShopDetails Shop { get; init; } = new();

    public string ReceiptNumber { get; init; } = string.Empty;

    public DateTime IssuedAtLocal { get; init; }

    public string CustomerName { get; init; } = string.Empty;

    public string? CustomerMobile { get; init; }

    public decimal AmountReceived { get; init; }

    public decimal BalanceRemaining { get; init; }

    public string PaymentMethod { get; init; } = string.Empty;

    public string? Note { get; init; }

    public string FooterMessage { get; init; } = "Thank you for your payment.";
}
