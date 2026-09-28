using System.Globalization;
using System.Text;
using MoizPos.Application.Calculations;

namespace MoizPos.Application.Documents;

/// <summary>Which app carries the message. They are written differently on purpose.</summary>
public enum MessageChannel
{
    /// <summary>Emoji, bold amounts, the receipt link, the shop's number and the software credit.</summary>
    WhatsApp,

    /// <summary>
    /// Plain text and the figures only — no link, no contact line, no credit, no emoji. One emoji
    /// switches an SMS to UCS-2 and cuts each charged part from 160 characters to 70, and older
    /// keypad phones show it as a box.
    /// </summary>
    Sms,
}

/// <summary>One line of a bill: what was bought, and how many.</summary>
public readonly record struct MessageItem(string ProductName, int Quantity);

/// <summary>The facts a bill's message states. Every figure is what the server recorded.</summary>
public sealed record InvoiceMessageFacts
{
    public required string InvoiceNumber { get; init; }

    public string? CustomerName { get; init; }

    public required DateOnly SaleDate { get; init; }

    public IReadOnlyList<MessageItem> Items { get; init; } = [];

    public required decimal Total { get; init; }

    /// <summary>
    /// What the invoice recorded as paid at the counter — never worked out as
    /// <c>total − remaining</c>, because a later return moves <c>remaining</c> without anything
    /// having been received.
    /// </summary>
    public required decimal AmountPaid { get; init; }

    public required decimal Remaining { get; init; }

    /// <summary>Null when nothing is owed on this bill.</summary>
    public UdhaarDue? Due { get; init; }
}

/// <summary>The facts a payment receipt's message states.</summary>
public sealed record ReceiptMessageFacts
{
    public required string ReceiptNumber { get; init; }

    public string? CustomerName { get; init; }

    public required DateOnly PaymentDate { get; init; }

    public required decimal AmountReceived { get; init; }

    /// <summary>
    /// The balance <b>recorded on the ledger entry for this payment</b> — never the customer's
    /// balance as it stands today. The two differ the moment the customer buys again, and a
    /// receipt that quietly restates itself contradicts the shop's own book.
    /// </summary>
    public required decimal BalanceAfter { get; init; }

    /// <summary>Null when the payment settled the account.</summary>
    public UdhaarDue? Due { get; init; }
}

/// <summary>The facts a payment reminder states: what is owed today, and by when.</summary>
public sealed record ReminderMessageFacts
{
    public required string CustomerName { get; init; }

    public required decimal Outstanding { get; init; }

    public required UdhaarDue Due { get; init; }
}

/// <summary>
/// What the customer actually reads when a bill, a receipt or a reminder is sent to them.
///
/// <para><b>The figures go in the message body, not only behind the link.</b> Most customers will
/// never tap a link. An acknowledgement that works only if opened acknowledges nothing — and this
/// matters most for a payment against udhaar, which is the single most disputed event in the
/// shop.</para>
///
/// <para><b>One wording, two renderings.</b> WhatsApp and SMS state the same facts in the same
/// order; only the decoration differs (see <see cref="MessageChannel"/>). Pure so it can be tested
/// without a database, per the constitution's preference for extracting calculation from I/O.</para>
///
/// <para><b>Nothing here may be derived by the caller.</b> Every figure is passed in from what the
/// server recorded. A message assembled from a screen's numbers can disagree with the ledger, and
/// a customer holding a message that contradicts the shop's own book is worse off than one holding
/// nothing.</para>
/// </summary>
public static class DocumentMessages
{
    /// <summary>
    /// The software's maker, printed last on every WhatsApp message and every PDF — at the owner's
    /// request. Never on SMS, where it would be paid for on every message sent.
    /// </summary>
    public const string SoftwareCredit =
        "This software created by Asyntex Consultancy\nContact: 0304-8063465 || 0301-2805749";

    /// <summary>How many products an SMS names before "+N more". WhatsApp names them all.</summary>
    private const int SmsItemsShown = 2;

    /// <summary>A bill: what was bought, what it came to and, on udhaar, what is owed and by when.</summary>
    /// <param name="documentUrl">The receipt link. WhatsApp only — an SMS never carries it.</param>
    public static string Invoice(
        MessageChannel channel, ShopDetails shop, InvoiceMessageFacts facts, string? documentUrl = null)
    {
        var message = new MessageWriter(channel);

        message.Header(shop);
        message.Name(facts.CustomerName);
        message.Line("🧾", $"Invoice: {facts.InvoiceNumber}");
        message.Line("📅", $"Date: {Date(facts.SaleDate)}");
        message.Items(facts.Items);
        message.Line("💰", $"Total Amount: {message.Bold(Rs(facts.Total))}");

        // Udhaar: all three figures, so the customer can check the sum without opening the link.
        // Paid in full: a balance line of "Rs 0" invites a question, so say it in words.
        if (facts.Remaining > 0m)
        {
            message.Line("✅", $"Received Amount: {Rs(facts.AmountPaid)}");
            message.Line("⏳", $"Remaining Amount: {message.Bold(Rs(facts.Remaining))}");
            message.DueLine(facts.Remaining, facts.Due);
        }
        else
        {
            message.Line(
                "✅",
                channel == MessageChannel.WhatsApp
                    ? "Paid in full. Thank you for shopping with us! 🙏"
                    : "Paid in full. Thank you!");
        }

        message.Footer(shop, documentUrl);

        return message.ToString();
    }

    /// <summary>A payment: what was owed before it, what was received, and what is left.</summary>
    /// <param name="documentUrl">The receipt link. WhatsApp only — an SMS never carries it.</param>
    public static string PaymentReceipt(
        MessageChannel channel, ShopDetails shop, ReceiptMessageFacts facts, string? documentUrl = null)
    {
        var message = new MessageWriter(channel);

        message.Header(shop);
        message.Name(facts.CustomerName);
        message.Line("🧾", $"Receipt: {facts.ReceiptNumber}");
        message.Line("📅", $"Date: {Date(facts.PaymentDate)}");

        // What was owed before this payment is the same ledger entry's figure: the balance after
        // it plus what was paid. The customer sees all three and can check the sum themselves.
        message.Line("💰", $"Total Amount: {Rs(facts.BalanceAfter + facts.AmountReceived)}");
        message.Line("✅", $"Received Amount: {message.Bold(Rs(facts.AmountReceived))}");

        // "Remaining: Rs 0" is true and reads like a fault. Nothing is owed — say that.
        if (facts.BalanceAfter > 0m)
        {
            message.Line("⏳", $"Remaining Amount: {message.Bold(Rs(facts.BalanceAfter))}");
            message.DueLine(facts.BalanceAfter, facts.Due);
        }
        else
        {
            message.Line("🎉", "Your account is now settled. Thank you!");
        }

        message.Footer(shop, documentUrl);

        return message.ToString();
    }

    /// <summary>
    /// A reminder, sent from the customer's register: what they owe today and by when. The one
    /// message that is about <b>now</b> rather than about a past sale or payment, so it is also the
    /// one that can say "overdue".
    /// </summary>
    public static string Reminder(MessageChannel channel, ShopDetails shop, ReminderMessageFacts facts)
    {
        var message = new MessageWriter(channel);

        message.Header(shop);
        message.Name(facts.CustomerName);
        message.Line("⏳", $"Remaining Amount: {message.Bold(Rs(facts.Outstanding))}");
        message.DueLine(facts.Outstanding, facts.Due);
        message.Line("🙏", "Kindly ignore if already paid.");
        message.Footer(shop, documentUrl: null);

        return message.ToString();
    }

    /// <summary>"Rs 1,000" for whole rupees, "Rs 1,000.50" only when there are paisa.</summary>
    private static string Rs(decimal amount) =>
        "Rs " + amount.ToString(
            amount == decimal.Truncate(amount) ? "N0" : "N2", CultureInfo.InvariantCulture);

    /// <summary>"01 Oct 2026" — read at a glance, where "2026-10-01" reads like a code.</summary>
    private static string Date(DateOnly date) =>
        date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>Builds one message, decorated or plain according to its channel.</summary>
    private sealed class MessageWriter
    {
        private readonly MessageChannel _channel;
        private readonly StringBuilder _text = new();

        public MessageWriter(MessageChannel channel) => _channel = channel;

        private bool IsWhatsApp => _channel == MessageChannel.WhatsApp;

        /// <summary>WhatsApp renders <c>*text*</c> bold; SMS would show the asterisks.</summary>
        public string Bold(string text) => IsWhatsApp ? $"*{text}*" : text;

        public void Header(ShopDetails shop)
        {
            if (IsWhatsApp)
            {
                _text.Append(CultureInfo.InvariantCulture, $"🏪 *{shop.Name}*\n📍 {shop.Location}\n");
            }
            else
            {
                _text.Append(CultureInfo.InvariantCulture, $"{shop.Name}, {shop.Location}\n");
            }
        }

        /// <summary>A walk-in has no name, and "Name: " with nothing after it reads worse than no line.</summary>
        public void Name(string? customerName)
        {
            if (IsWhatsApp)
            {
                _text.Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(customerName))
            {
                Line("👤", $"Name: {customerName.Trim()}");
            }
        }

        public void Line(string emoji, string text) =>
            _text.Append(IsWhatsApp ? $"{emoji} {text}\n" : $"{text}\n");

        public void Items(IReadOnlyList<MessageItem> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            // A long bill would turn one SMS into four; WhatsApp costs nothing extra per line.
            var shown = IsWhatsApp ? items : items.Take(SmsItemsShown).ToList();
            var names = string.Join(", ", shown.Select(item =>
                item.Quantity > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"{item.ProductName.Trim()} x{item.Quantity}")
                    : item.ProductName.Trim()));

            if (shown.Count < items.Count)
            {
                names += string.Create(CultureInfo.InvariantCulture, $" +{items.Count - shown.Count} more");
            }

            Line("📦", $"{(items.Count == 1 ? "Product" : "Products")}: {names}");
        }

        public void DueLine(decimal amount, UdhaarDue? due)
        {
            if (due is not { } when)
            {
                return;
            }

            var overdue = when.MonthsOverdue switch
            {
                0 => string.Empty,
                1 => " (1 month overdue)",
                var months => string.Create(CultureInfo.InvariantCulture, $" ({months} months overdue)"),
            };

            if (IsWhatsApp)
            {
                _text.Append('\n');
            }

            Line(
                when.MonthsOverdue > 0 ? "⚠️" : "🔔",
                $"Please pay your dues {Bold(Rs(amount))} by {Bold(Date(when.DueOn))}{overdue}");
        }

        /// <summary>WhatsApp: the link, the shop's number, then the credit. SMS: nothing.</summary>
        public void Footer(ShopDetails shop, string? documentUrl)
        {
            if (!IsWhatsApp)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(documentUrl))
            {
                _text.Append(CultureInfo.InvariantCulture, $"\n🔗 Please check your details here:\n{documentUrl}\n");
            }

            if (!string.IsNullOrWhiteSpace(shop.ContactNumber))
            {
                _text.Append(CultureInfo.InvariantCulture, $"\n📞 For any query, call us: {shop.ContactNumber.Trim()}\n");
            }

            _text.Append(CultureInfo.InvariantCulture, $"\n{SoftwareCredit}");
        }

        public override string ToString() => _text.ToString().TrimEnd('\n');
    }
}
