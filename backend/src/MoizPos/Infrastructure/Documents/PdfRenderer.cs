using System.Globalization;
using MoizPos.Application.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MoizPos.Infrastructure.Documents;

public interface IPdfRenderer
{
    byte[] RenderInvoice(InvoiceDocument document);

    byte[] RenderReceipt(ReceiptDocument document);
}

/// <summary>
/// Renders documents with QuestPDF (Community licence — research.md R3).
///
/// Layout is fluent rather than coordinate-based so a long invoice pages correctly on its own;
/// the content itself is decided in <see cref="DocumentAssembler"/> and tested there.
/// </summary>
public sealed class PdfRenderer : IPdfRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static string Money(decimal value) => $"Rs {value.ToString("N2", Culture)}";

    public byte[] RenderInvoice(InvoiceDocument model)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A5);
                page.Margin(14);
                page.DefaultTextStyle(text => text.FontSize(9));

                page.Header().Element(header => Header(header, model.Shop, "INVOICE", model.InvoiceNumber, model.IssuedAtLocal));

                page.Content().PaddingVertical(8).Column(column =>
                {
                    column.Spacing(8);

                    column.Item().Text($"Customer: {model.CustomerName}").SemiBold();

                    if (!string.IsNullOrWhiteSpace(model.CustomerMobile))
                    {
                        column.Item().Text($"Mobile: {model.CustomerMobile}");
                    }

                    column.Item().Element(container => LineTable(container, model.Lines));

                    column.Item().AlignRight().Column(totals =>
                    {
                        totals.Spacing(2);

                        TotalRow(totals, "Subtotal", Money(model.Subtotal));

                        if (model.OrderDiscount > 0m)
                        {
                            TotalRow(totals, "Bill discount", $"-{Money(model.OrderDiscount)}");
                        }

                        TotalRow(totals, "Total", Money(model.Total), bold: true);
                        TotalRow(totals, $"Paid ({model.PaymentMethod})", Money(model.AmountPaid));

                        if (model.AmountRemaining > 0m)
                        {
                            TotalRow(totals, "Balance due", Money(model.AmountRemaining), bold: true);
                        }
                    });
                });

                page.Footer().Element(footer => Footer(footer, model.FooterMessage));
            });
        }).GeneratePdf();
    }

    public byte[] RenderReceipt(ReceiptDocument model)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A6);
                page.Margin(14);
                page.DefaultTextStyle(text => text.FontSize(9));

                page.Header().Element(header =>
                    Header(header, model.Shop, "PAYMENT RECEIPT", model.ReceiptNumber, model.IssuedAtLocal));

                page.Content().PaddingVertical(8).Column(column =>
                {
                    column.Spacing(6);

                    column.Item().Text($"Received from: {model.CustomerName}").SemiBold();

                    if (!string.IsNullOrWhiteSpace(model.CustomerMobile))
                    {
                        column.Item().Text($"Mobile: {model.CustomerMobile}");
                    }

                    column.Item().PaddingTop(6).Column(totals =>
                    {
                        totals.Spacing(2);

                        TotalRow(totals, $"Amount received ({model.PaymentMethod})",
                            Money(model.AmountReceived), bold: true);

                        TotalRow(totals, "Balance remaining", Money(model.BalanceRemaining), bold: true);
                    });

                    if (!string.IsNullOrWhiteSpace(model.Note))
                    {
                        column.Item().PaddingTop(4).Text($"Note: {model.Note}").Italic();
                    }
                });

                page.Footer().Element(footer => Footer(footer, model.FooterMessage));
            });
        }).GeneratePdf();
    }

    private static void Header(
        IContainer container, ShopDetails shop, string title, string number, DateTime issuedAtLocal)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(shop.Name).FontSize(13).Bold();
                    left.Item().Text(shop.Location).FontSize(8);

                    if (!string.IsNullOrWhiteSpace(shop.ContactNumber))
                    {
                        left.Item().Text(shop.ContactNumber).FontSize(8);
                    }
                });

                row.ConstantItem(150).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(title).FontSize(11).Bold();
                    right.Item().AlignRight().Text(number).FontSize(9);
                    right.Item().AlignRight()
                        .Text(issuedAtLocal.ToString("dd MMM yyyy, HH:mm", Culture)).FontSize(8);
                });
            });

            column.Item().PaddingTop(6).LineHorizontal(1);
        });
    }

    private static void LineTable(IContainer container, IReadOnlyList<InvoiceDocumentLine> lines)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(4);
                columns.RelativeColumn(1);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Item");
                HeaderCell(header.Cell(), "Qty", right: true);
                HeaderCell(header.Cell(), "Rate", right: true);
                HeaderCell(header.Cell(), "Disc.", right: true);
                HeaderCell(header.Cell(), "Amount", right: true);
            });

            foreach (var line in lines)
            {
                table.Cell().PaddingVertical(2).Text(line.ProductName);
                table.Cell().PaddingVertical(2).AlignRight().Text(line.Quantity.ToString(Culture));
                table.Cell().PaddingVertical(2).AlignRight().Text(Money(line.UnitSalePrice));
                table.Cell().PaddingVertical(2).AlignRight()
                    .Text(line.LineDiscount > 0m ? Money(line.LineDiscount) : "-");
                table.Cell().PaddingVertical(2).AlignRight().Text(Money(line.LineTotal));
            }
        });

        static void HeaderCell(IContainer cell, string text, bool right = false)
        {
            var styled = cell.BorderBottom(1).PaddingVertical(3);

            (right ? styled.AlignRight() : styled).Text(text).SemiBold();
        }
    }

    private static void TotalRow(ColumnDescriptor column, string label, string value, bool bold = false)
    {
        column.Item().Row(row =>
        {
            var labelText = row.RelativeItem().AlignRight().PaddingRight(10).Text(label);
            var valueText = row.ConstantItem(90).AlignRight().Text(value);

            if (bold)
            {
                labelText.SemiBold();
                valueText.SemiBold();
            }
        });
    }

    private static void Footer(IContainer container, string message)
    {
        container.Column(column =>
        {
            column.Item().LineHorizontal(1);
            column.Item().PaddingTop(4).AlignCenter().Text(message).FontSize(8).Italic();
            column.Item().PaddingTop(2).AlignCenter()
                .Text(DocumentMessages.SoftwareCredit.Replace('\n', ' ')).FontSize(7);
        });
    }
}
