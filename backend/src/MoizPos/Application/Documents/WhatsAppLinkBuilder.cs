using System.Globalization;
using System.Text;

namespace MoizPos.Application.Documents;

/// <summary>The outcome of building a share link, or why one could not be built.</summary>
public sealed record WhatsAppLink(string Url, string NormalisedNumber)
{
    public static WhatsAppLink? None => null;
}

/// <summary>
/// Builds a <c>wa.me</c> deep link addressed to a customer.
///
/// <para><b>A link, not an attachment.</b> The <c>wa.me</c> scheme carries a prefilled text
/// message and nothing else — no browser-based integration can push a PDF into WhatsApp on the
/// operator's behalf (research.md R2). So the message contains a link to the receipt, and the
/// operator taps Send.</para>
///
/// <para>Numbers are normalised to international form without a plus, which is what wa.me
/// expects: <c>923001234567</c>.</para>
/// </summary>
public static class WhatsAppLinkBuilder
{
    private const string CountryCode = "92";

    /// <summary>Pakistani mobile numbers are 10 digits after the country code.</summary>
    private const int NationalNumberLength = 10;

    /// <summary>
    /// Returns null when the number is missing or cannot be understood — the caller disables the
    /// send button and says why, rather than opening a broken WhatsApp link (FR-044).
    /// </summary>
    public static WhatsAppLink? Build(string? mobileNumber, string message, string? documentUrl = null)
    {
        var normalised = NormaliseNumber(mobileNumber);

        if (normalised is null)
        {
            return null;
        }

        var text = string.IsNullOrWhiteSpace(documentUrl)
            ? message
            : $"{message}\n\n{documentUrl}";

        // Straight to api.whatsapp.com, never through wa.me: wa.me only redirects here, and on
        // WhatsApp Desktop and Web that redirect turns every 4-byte emoji (🏪 📍 👤 🔔 …) into
        // "�". The text itself is correct UTF-8 either way; it is the hop that breaks it.
        var url = $"https://api.whatsapp.com/send?phone={normalised}&text={Uri.EscapeDataString(text)}";

        return new WhatsAppLink(url, normalised);
    }

    /// <summary>
    /// Turns what a shopkeeper actually types into the digits wa.me needs.
    ///
    /// Accepts 03001234567, +92 300 1234567, 0092-300-1234567 and 923001234567.
    /// </summary>
    public static string? NormaliseNumber(string? mobileNumber)
    {
        if (string.IsNullOrWhiteSpace(mobileNumber))
        {
            return null;
        }

        var digits = new StringBuilder(mobileNumber.Length);

        foreach (var character in mobileNumber)
        {
            if (char.IsDigit(character))
            {
                digits.Append(character);
            }
        }

        var value = digits.ToString();

        if (value.Length == 0)
        {
            return null;
        }

        // 0092... -> 92...
        if (value.StartsWith("00" + CountryCode, StringComparison.Ordinal))
        {
            value = value[2..];
        }

        // Reduce whatever form was typed to the 10-digit national number.
        var national = value switch
        {
            _ when value.Length == CountryCode.Length + NationalNumberLength
                   && value.StartsWith(CountryCode, StringComparison.Ordinal)
                => value[CountryCode.Length..],

            // 03001234567 — the trunk prefix a shopkeeper types.
            _ when value.Length == NationalNumberLength + 1 && value[0] == '0'
                => value[1..],

            _ when value.Length == NationalNumberLength => value,

            _ => null,
        };

        // Every Pakistani mobile number is 10 digits beginning with 3. Without this check a
        // 10-digit local number like 0300123456 (one digit short) would silently become
        // 920300123456 and open WhatsApp addressed to a stranger.
        if (national is null || national.Length != NationalNumberLength || national[0] != '3')
        {
            return null;
        }

        return CountryCode + national;
    }

}
