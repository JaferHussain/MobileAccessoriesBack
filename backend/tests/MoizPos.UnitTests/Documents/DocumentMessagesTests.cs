using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Documents;

namespace MoizPos.UnitTests.Documents;

/// <summary>
/// The words a customer actually reads.
///
/// <para>Pure, so the wording can be argued about without a database in the way — and so the
/// cases that read badly, a settled account reported as "Remaining: Rs 0", or an SMS that
/// silently costs three times what it should, are caught here rather than by a customer.</para>
/// </summary>
public sealed class DocumentMessagesTests
{
    private const string Link = "https://shop.example/api/public/documents/abc123";

    private static readonly ShopDetails Shop = new()
    {
        Name = "Moiz Mobile & Corporation",
        Location = "Danwran Lodhran",
        ContactNumber = "0300 7194095",
    };

    private static readonly DateOnly FirstSeptember = new(2026, 9, 1);

    private static InvoiceMessageFacts Udhaar(decimal total = 1000m, decimal paid = 500m) => new()
    {
        InvoiceNumber = "INV-2026-000077",
        CustomerName = "Asif",
        SaleDate = FirstSeptember,
        Items = [new("Oppo 33W Charger", 1), new("Audionic Headphone", 2)],
        Total = total,
        AmountPaid = paid,
        Remaining = total - paid,
        Due = UdhaarDueDate.For(FirstSeptember, FirstSeptember),
    };

    private static InvoiceMessageFacts WalkIn() => new()
    {
        InvoiceNumber = "INV-1",
        SaleDate = FirstSeptember,
        Items = [new("Type-C Cable", 1)],
        Total = 350m,
        AmountPaid = 350m,
        Remaining = 0m,
    };

    private static ReceiptMessageFacts Payment(decimal received, decimal balanceAfter) => new()
    {
        ReceiptNumber = "RCP-2026-000012",
        CustomerName = "Asif",
        PaymentDate = new DateOnly(2026, 9, 5),
        AmountReceived = received,
        BalanceAfter = balanceAfter,
        Due = balanceAfter > 0m ? UdhaarDueDate.For(FirstSeptember, new DateOnly(2026, 9, 5)) : null,
    };

    // ================================================================
    //  A bill
    // ================================================================

    [Theory]
    [InlineData(MessageChannel.WhatsApp)]
    [InlineData(MessageChannel.Sms)]
    public void An_udhaar_bill_states_the_total_what_was_received_what_is_left_and_by_when(
        MessageChannel channel)
    {
        var message = Plain(DocumentMessages.Invoice(channel, Shop, Udhaar(), Link));

        message.Should().Contain("Name: Asif");
        message.Should().Contain("Invoice: INV-2026-000077");
        message.Should().Contain("Total Amount: Rs 1,000");
        message.Should().Contain("Received Amount: Rs 500");
        message.Should().Contain("Remaining Amount: Rs 500");

        // The owner's rule: bought on 1 September, due on 1 October.
        message.Should().Contain("Please pay your dues Rs 500 by 01 Oct 2026");
        message.Should().NotContain("overdue");
    }

    [Fact]
    public void A_full_udhaar_bill_says_plainly_that_nothing_was_received()
    {
        var message = DocumentMessages.Invoice(MessageChannel.Sms, Shop, Udhaar(paid: 0m));

        message.Should().Contain("Received Amount: Rs 0");
        message.Should().Contain("Remaining Amount: Rs 1,000");
    }

    [Fact]
    public void A_bill_names_what_was_bought()
    {
        DocumentMessages.Invoice(MessageChannel.WhatsApp, Shop, Udhaar())
            .Should().Contain("Products: Oppo 33W Charger, Audionic Headphone x2");

        DocumentMessages.Invoice(MessageChannel.Sms, Shop, WalkIn())
            .Should().Contain("Product: Type-C Cable");
    }

    [Fact]
    public void An_sms_names_two_products_and_counts_the_rest()
    {
        // A long bill would otherwise turn one SMS into four.
        var facts = Udhaar() with
        {
            Items = [new("Charger", 1), new("Cable", 1), new("Headphone", 1), new("Cover", 1)],
        };

        DocumentMessages.Invoice(MessageChannel.Sms, Shop, facts)
            .Should().Contain("Products: Charger, Cable +2 more");

        DocumentMessages.Invoice(MessageChannel.WhatsApp, Shop, facts)
            .Should().Contain("Charger, Cable, Headphone, Cover");
    }

    [Fact]
    public void A_fully_paid_bill_says_nothing_about_a_balance_or_a_due_date()
    {
        var message = DocumentMessages.Invoice(MessageChannel.WhatsApp, Shop, WalkIn(), Link);

        // Nothing is owed, so there is nothing to report. A "Remaining: Rs 0" line invites the
        // customer to wonder whether it should have been something else.
        message.Should().Contain("Total Amount: *Rs 350*");
        message.Should().Contain("Paid in full");
        message.Should().NotContain("Remaining");
        message.Should().NotContain("Received Amount");
        message.Should().NotContain("Please pay");
    }

    [Fact]
    public void A_walk_in_is_not_given_an_empty_name_line()
    {
        DocumentMessages.Invoice(MessageChannel.WhatsApp, Shop, WalkIn())
            .Should().NotContain("Name:");
    }

    [Fact]
    public void Paisa_are_shown_only_when_there_are_some()
    {
        var facts = Udhaar(total: 1000.50m, paid: 500m);

        var message = DocumentMessages.Invoice(MessageChannel.Sms, Shop, facts);

        message.Should().Contain("Total Amount: Rs 1,000.50");
        message.Should().Contain("Received Amount: Rs 500\n");
    }

    // ================================================================
    //  A payment
    // ================================================================

    [Fact]
    public void A_payment_states_what_was_owed_what_was_received_and_what_is_left()
    {
        var message = Plain(DocumentMessages.PaymentReceipt(
            MessageChannel.WhatsApp, Shop, Payment(received: 5000m, balanceAfter: 1650m), Link));

        message.Should().Contain("Name: Asif");

        // What was owed before this payment is the ledger's own figure: the balance after it plus
        // what was paid.
        message.Should().Contain("Total Amount: Rs 6,650");
        message.Should().Contain("Received Amount: Rs 5,000");
        message.Should().Contain("Remaining Amount: Rs 1,650");
        message.Should().Contain("Please pay your dues Rs 1,650 by 01 Oct 2026");
    }

    [Fact]
    public void A_settled_account_is_said_in_words_not_as_a_zero()
    {
        var message = DocumentMessages.PaymentReceipt(
            MessageChannel.WhatsApp, Shop, Payment(received: 1000m, balanceAfter: 0m));

        // "Remaining: Rs 0" is true and reads like a fault.
        message.Should().NotContain("Rs 0\n");
        message.Should().NotContain("Remaining");
        message.Should().NotContain("Please pay");
        message.Should().Contain("settled");
    }

    [Fact]
    public void A_customer_with_no_name_is_not_greeted_as_nobody()
    {
        var message = DocumentMessages.PaymentReceipt(
            MessageChannel.Sms, Shop, Payment(100m, 0m) with { CustomerName = null });

        message.Should().NotContain("Name:");
        message.Should().Contain("Received Amount: Rs 100");
    }

    // ================================================================
    //  A reminder
    // ================================================================

    [Fact]
    public void A_reminder_months_late_says_how_many_months_overdue()
    {
        var facts = new ReminderMessageFacts
        {
            CustomerName = "Asif",
            Outstanding = 1000m,
            Due = UdhaarDueDate.For(FirstSeptember, asOf: new DateOnly(2026, 10, 15)),
        };

        var message = Plain(DocumentMessages.Reminder(MessageChannel.WhatsApp, Shop, facts));

        // The owner's own example: not paid in October, so the date moves to 1 November.
        message.Should().Contain("Please pay your dues Rs 1,000 by 01 Nov 2026 (1 month overdue)");
        message.Should().Contain("⚠️");

        DocumentMessages.Reminder(
                MessageChannel.Sms, Shop,
                facts with { Due = UdhaarDueDate.For(FirstSeptember, new DateOnly(2026, 12, 15)) })
            .Should().Contain("(3 months overdue)");
    }

    [Fact]
    public void A_reminder_before_the_due_date_is_not_called_overdue()
    {
        var facts = new ReminderMessageFacts
        {
            CustomerName = "Asif",
            Outstanding = 1000m,
            Due = UdhaarDueDate.For(FirstSeptember, asOf: new DateOnly(2026, 9, 20)),
        };

        var message = DocumentMessages.Reminder(MessageChannel.WhatsApp, Shop, facts);

        message.Should().Contain("🔔");
        message.Should().NotContain("overdue");
    }

    // ================================================================
    //  The two channels
    // ================================================================

    [Fact]
    public void WhatsApp_carries_the_link_then_the_shop_number_and_ends_with_the_credit()
    {
        var message = DocumentMessages.Invoice(MessageChannel.WhatsApp, Shop, Udhaar(), Link);

        message.Should().StartWith("🏪 *Moiz Mobile & Corporation*\n📍 Danwran Lodhran");
        message.Should().Contain($"Please check your details here:\n{Link}");
        message.Should().Contain("For any query, call us: 0300 7194095");
        message.Should().EndWith(DocumentMessages.SoftwareCredit);

        message.IndexOf(Link, StringComparison.Ordinal).Should()
            .BeLessThan(message.IndexOf("0300 7194095", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatsApp_sets_the_amount_owed_in_bold()
    {
        DocumentMessages.Invoice(MessageChannel.WhatsApp, Shop, Udhaar())
            .Should().Contain("Please pay your dues *Rs 500* by *01 Oct 2026*");
    }

    [Fact]
    public void A_shop_with_no_number_on_file_has_no_empty_contact_line()
    {
        DocumentMessages.Invoice(MessageChannel.WhatsApp, Shop with { ContactNumber = " " }, Udhaar())
            .Should().NotContain("call us");
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("paid")]
    [InlineData("receipt")]
    [InlineData("reminder")]
    public void An_sms_carries_no_link_no_contact_line_no_credit_and_no_emoji(string kind)
    {
        var message = kind switch
        {
            "invoice" => DocumentMessages.Invoice(MessageChannel.Sms, Shop, Udhaar(), Link),
            "paid" => DocumentMessages.Invoice(MessageChannel.Sms, Shop, WalkIn(), Link),
            "receipt" => DocumentMessages.PaymentReceipt(MessageChannel.Sms, Shop, Payment(500m, 500m), Link),
            _ => DocumentMessages.Reminder(MessageChannel.Sms, Shop, new ReminderMessageFacts
            {
                CustomerName = "Asif",
                Outstanding = 500m,
                Due = UdhaarDueDate.For(FirstSeptember, FirstSeptember),
            }),
        };

        message.Should().StartWith("Moiz Mobile & Corporation, Danwran Lodhran");
        message.Should().NotContain("http");
        message.Should().NotContain("call us");
        message.Should().NotContain("Asyntex");
        message.Should().NotContain("*", "SMS shows WhatsApp's bold markers as literal asterisks");

        // One character outside the GSM alphabet re-encodes the whole SMS as UCS-2: 70 characters
        // a part instead of 160, so the shop pays two or three times over for every message.
        message.Should().MatchRegex(@"^[A-Za-z0-9 \n,.:()+\-!&'/]*$");
    }

    [Fact]
    public void An_udhaar_sms_fits_in_two_parts()
    {
        // 2 × 153 characters is two charged parts. The owner's typical udhaar bill must not cost more.
        DocumentMessages.Invoice(MessageChannel.Sms, Shop, Udhaar()).Length
            .Should().BeLessThanOrEqualTo(306);
    }

    /// <summary>The message with WhatsApp's decoration removed, so one assertion reads both channels.</summary>
    private static string Plain(string message) => message.Replace("*", string.Empty);
}
