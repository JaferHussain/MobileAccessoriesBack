using FluentAssertions;
using MoizPos.Application.Documents;
using MoizPos.Application.Time;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;

namespace MoizPos.UnitTests.Documents;

/// <summary>
/// T150 / T152 — the document must show everything FR-042 and FR-043 require, and its totals
/// must equal the invoice's. Checked on the assembled model rather than by reading a PDF.
/// </summary>
public sealed class InvoiceDocumentTests
{
    private static readonly PeriodResolver Periods = new();

    private static readonly ShopDetails Shop = new() { ContactNumber = "03001112233" };

    private static Invoice SampleInvoice() => new()
    {
        Id = 1,
        InvoiceNumber = "INV-2026-000123",
        InvoiceDateUtc = new DateTime(2026, 9, 9, 8, 30, 0, DateTimeKind.Utc),
        Subtotal = 4900m,
        OrderDiscount = 400m,
        Total = 4500m,
        AmountPaid = 1000m,
        AmountRemaining = 3500m,
        NetAmount = 4500m,
        PaymentMethod = PaymentMethod.Partial,
    };

    private static List<InvoiceItem> SampleItems() =>
    [
        new()
        {
            Id = 1,
            ProductName = "Type-C Braided 2m",
            Quantity = 2,
            UnitSalePrice = 1500m,
            LineDiscount = 100m,
            UnitCostPrice = 800m,
            LineTotal = 2900m,
        },
        new()
        {
            Id = 2,
            ProductName = "Earbuds Pro",
            Quantity = 1,
            UnitSalePrice = 2000m,
            LineDiscount = 0m,
            UnitCostPrice = 1400m,
            LineTotal = 2000m,
        },
    ];

    private static InvoiceDocument Build(string? customerName = "Bilal", string? mobile = "03001234567") =>
        DocumentAssembler.BuildInvoice(
            Shop, SampleInvoice(), SampleItems(), customerName, mobile, Periods);

    [Fact]
    public void Carries_the_shop_identity()
    {
        var document = Build();

        document.Shop.Name.Should().Be("Moiz Mobile & Corporation");
        document.Shop.Location.Should().Be("Danwran Lodhran");
        document.Shop.ContactNumber.Should().Be("03001112233");
    }

    [Fact]
    public void Carries_the_invoice_number_and_the_customer()
    {
        var document = Build();

        document.InvoiceNumber.Should().Be("INV-2026-000123");
        document.CustomerName.Should().Be("Bilal");
        document.CustomerMobile.Should().Be("03001234567");
    }

    [Fact]
    public void Shows_the_time_the_customer_was_at_the_counter()
    {
        var document = Build();

        // 08:30 UTC is 13:30 in Karachi — the shopkeeper should recognise the time.
        document.IssuedAtLocal.Hour.Should().Be(13);
        document.IssuedAtLocal.Date.Should().Be(new DateTime(2026, 9, 9));
    }

    [Fact]
    public void Names_a_walk_in_customer_rather_than_leaving_it_blank()
    {
        Build(customerName: null).CustomerName.Should().Be("Walk-in customer");
        Build(customerName: "  ").CustomerName.Should().Be("Walk-in customer");
    }

    [Fact]
    public void Lists_every_line_with_quantity_rate_and_discount()
    {
        var document = Build();

        document.Lines.Should().HaveCount(2);

        var first = document.Lines[0];
        first.ProductName.Should().Be("Type-C Braided 2m");
        first.Quantity.Should().Be(2);
        first.UnitSalePrice.Should().Be(1500m);
        first.LineDiscount.Should().Be(100m);
        first.LineTotal.Should().Be(2900m);
    }

    [Fact]
    public void Carries_every_monetary_total()
    {
        var document = Build();

        document.Subtotal.Should().Be(4900m);
        document.OrderDiscount.Should().Be(400m);
        document.Total.Should().Be(4500m);
        document.AmountPaid.Should().Be(1000m);
        document.AmountRemaining.Should().Be(3500m);
    }

    [Fact]
    public void Line_totals_sum_to_the_subtotal()
    {
        var document = Build();

        document.Lines.Sum(l => l.LineTotal).Should().Be(document.Subtotal);
    }

    [Fact]
    public void Total_equals_subtotal_less_the_order_discount()
    {
        var document = Build();

        (document.Subtotal - document.OrderDiscount).Should().Be(document.Total);
    }

    [Fact]
    public void Paid_plus_remaining_equals_the_total()
    {
        var document = Build();

        (document.AmountPaid + document.AmountRemaining).Should().Be(document.Total);
    }

    [Fact]
    public void Carries_a_thank_you_footer()
    {
        Build().FooterMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Never_carries_cost_or_profit()
    {
        // The customer's copy must not reveal what the shop paid (FR-040).
        var properties = typeof(InvoiceDocument).GetProperties()
            .Concat(typeof(InvoiceDocumentLine).GetProperties())
            .Select(p => p.Name);

        properties.Should().NotContain(name =>
            name.Contains("Cost", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Profit", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ReceiptDocumentTests
{
    private static readonly PeriodResolver Periods = new();

    private static ReceiptDocument Build(decimal amount = 1500m, decimal balance = 500m) =>
        DocumentAssembler.BuildReceipt(
            new ShopDetails(),
            new CustomerPayment
            {
                Id = 1,
                ReceiptNumber = "RCP-2026-000045",
                Amount = amount,
                PaymentMethod = PaymentMethod.Cash,
                PaymentDateUtc = new DateTime(2026, 9, 9, 8, 30, 0, DateTimeKind.Utc),
                Note = "Paid at shop",
            },
            "Bilal",
            "03001234567",
            balance,
            Periods);

    [Fact]
    public void Carries_the_receipt_number_and_customer()
    {
        var receipt = Build();

        receipt.ReceiptNumber.Should().Be("RCP-2026-000045");
        receipt.CustomerName.Should().Be("Bilal");
        receipt.CustomerMobile.Should().Be("03001234567");
    }

    [Fact]
    public void States_what_was_paid_and_what_remains()
    {
        // The owner's worked example: 1,500 received leaves 500 outstanding.
        var receipt = Build(1500m, 500m);

        receipt.AmountReceived.Should().Be(1500m);
        receipt.BalanceRemaining.Should().Be(500m);
    }

    [Fact]
    public void Shows_a_settled_balance_as_zero()
    {
        Build(2000m, 0m).BalanceRemaining.Should().Be(0m);
    }

    [Fact]
    public void Carries_the_shop_identity_and_a_footer()
    {
        var receipt = Build();

        receipt.Shop.Name.Should().Be("Moiz Mobile & Corporation");
        receipt.Shop.Location.Should().Be("Danwran Lodhran");
        receipt.FooterMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Carries_the_note_and_the_method()
    {
        var receipt = Build();

        receipt.Note.Should().Be("Paid at shop");
        receipt.PaymentMethod.Should().Be("Cash");
    }
}
