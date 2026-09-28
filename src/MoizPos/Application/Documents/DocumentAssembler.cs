using MoizPos.Application.Abstractions;
using MoizPos.Application.Time;
using MoizPos.Domain.Entities;

namespace MoizPos.Application.Documents;

/// <summary>
/// Turns stored records into the shape a document needs (FR-042, FR-043).
///
/// Separate from rendering so the content can be unit-tested without producing a PDF — the
/// question "does the receipt show everything it must?" is answered here.
/// </summary>
public static class DocumentAssembler
{
    public static InvoiceDocument BuildInvoice(
        ShopDetails shop,
        Invoice invoice,
        IReadOnlyList<InvoiceItem> items,
        string? customerName,
        string? customerMobile,
        PeriodResolver periods)
    {
        return new InvoiceDocument
        {
            Shop = shop,
            InvoiceNumber = invoice.InvoiceNumber,
            IssuedAtLocal = periods.ToShopLocal(invoice.InvoiceDateUtc),
            CustomerName = string.IsNullOrWhiteSpace(customerName)
                ? "Walk-in customer"
                : customerName,
            CustomerMobile = customerMobile,
            Lines = items.Select(item => new InvoiceDocumentLine(
                item.ProductName,
                item.Quantity,
                item.UnitSalePrice,
                item.LineDiscount,
                item.LineTotal)).ToList(),
            Subtotal = invoice.Subtotal,
            OrderDiscount = invoice.OrderDiscount,
            Total = invoice.Total,
            AmountPaid = invoice.AmountPaid,
            AmountRemaining = invoice.AmountRemaining,
            PaymentMethod = invoice.PaymentMethod.ToString(),
        };
    }

    public static ReceiptDocument BuildReceipt(
        ShopDetails shop,
        CustomerPayment payment,
        string customerName,
        string? customerMobile,
        decimal balanceRemaining,
        PeriodResolver periods)
    {
        return new ReceiptDocument
        {
            Shop = shop,
            ReceiptNumber = payment.ReceiptNumber,
            IssuedAtLocal = periods.ToShopLocal(payment.PaymentDateUtc),
            CustomerName = customerName,
            CustomerMobile = customerMobile,
            AmountReceived = payment.Amount,
            BalanceRemaining = balanceRemaining,
            PaymentMethod = payment.PaymentMethod.ToString(),
            Note = payment.Note,
        };
    }
}
