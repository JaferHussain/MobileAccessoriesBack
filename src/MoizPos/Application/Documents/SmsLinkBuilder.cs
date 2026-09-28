namespace MoizPos.Application.Documents;

/// <summary>
/// Builds an <c>sms:</c> deep link addressed to a customer.
///
/// <para><b>The same idea as <see cref="WhatsAppLinkBuilder"/>, through a different app.</b>
/// Neither sends anything: both prepare a message in an app the shopkeeper already has on the
/// counter device, and they tap Send. So the shop holds no messaging account, registers no sender
/// id, and pays nothing per message — the cost is whatever their own SIM plan charges.</para>
///
/// <para><b>Why SMS is the fallback and not the default.</b> An SMS is 160 characters; the shop
/// name, the figures and a share link will not fit, so a send splits into two or three parts and
/// is charged accordingly. WhatsApp has no such limit. SMS exists for the customer who does not
/// use WhatsApp.</para>
///
/// <para>Number normalisation is deliberately shared with the WhatsApp builder rather than
/// duplicated: a number that is good enough for one channel is good enough for the other, and two
/// copies of that rule would eventually disagree about what a valid number looks like.</para>
/// </summary>
public static class SmsLinkBuilder
{
    /// <summary>
    /// Returns null when the number is missing or cannot be understood — the caller disables the
    /// send button and says why, rather than opening a messaging app addressed to nobody.
    /// </summary>
    public static string? Build(string? mobileNumber, string message, string? documentUrl = null)
    {
        var normalised = WhatsAppLinkBuilder.NormaliseNumber(mobileNumber);

        if (normalised is null)
        {
            return null;
        }

        var text = string.IsNullOrWhiteSpace(documentUrl)
            ? message
            : $"{message}\n\n{documentUrl}";

        // The leading '+' is what makes the number unambiguous to a dialler that may be roaming
        // or configured for another country; wa.me wants it omitted, sms: wants it present.
        return $"sms:+{normalised}?body={Uri.EscapeDataString(text)}";
    }
}
