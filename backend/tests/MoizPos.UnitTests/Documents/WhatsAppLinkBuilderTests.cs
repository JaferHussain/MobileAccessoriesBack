using FluentAssertions;
using MoizPos.Application.Documents;

namespace MoizPos.UnitTests.Documents;

/// <summary>
/// T155 — number normalisation and link construction (FR-044).
///
/// Getting a number wrong means opening WhatsApp addressed to a stranger, so anything the
/// builder cannot vouch for returns null and the send button stays disabled.
/// </summary>
public sealed class WhatsAppNumberTests
{
    [Theory]
    [InlineData("03001234567", "923001234567")]      // as a shopkeeper types it
    [InlineData("0300 1234567", "923001234567")]     // with a space
    [InlineData("0300-123-4567", "923001234567")]    // with dashes
    [InlineData("+92 300 1234567", "923001234567")]  // international
    [InlineData("+923001234567", "923001234567")]
    [InlineData("923001234567", "923001234567")]     // already normalised
    [InlineData("0092 300 1234567", "923001234567")] // 00 prefix
    [InlineData("(0300) 1234567", "923001234567")]   // brackets
    public void Normalises_a_pakistani_mobile_number(string input, string expected)
    {
        WhatsAppLinkBuilder.NormaliseNumber(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("12345")]              // too short
    [InlineData("0300123456")]         // one digit short
    [InlineData("030012345678")]       // one digit long
    [InlineData("+1 555 123 4567")]    // not a Pakistani number
    public void Refuses_a_number_it_cannot_vouch_for(string? input)
    {
        WhatsAppLinkBuilder.NormaliseNumber(input).Should().BeNull();
    }
}

public sealed class WhatsAppLinkTests
{
    private static readonly ShopDetails Shop = new();

    [Fact]
    public void Builds_a_direct_whatsapp_link_addressed_to_the_customer()
    {
        var link = WhatsAppLinkBuilder.Build("03001234567", "Hello");

        link.Should().NotBeNull();
        link!.Url.Should().StartWith("https://api.whatsapp.com/send?phone=923001234567&text=");
        link.NormalisedNumber.Should().Be("923001234567");
    }

    [Fact]
    public void Never_goes_through_the_wa_me_redirect_that_breaks_emoji()
    {
        // wa.me redirects to api.whatsapp.com, and on WhatsApp Desktop and Web that hop mangles
        // every 4-byte emoji (🏪 📍 👤 🔔 …) into "�". Seen on the shop's own messages.
        WhatsAppLinkBuilder.Build("03001234567", "Hello")!.Url.Should().NotContain("wa.me");
    }

    [Fact]
    public void Carries_four_byte_emoji_as_their_exact_utf8_bytes()
    {
        var link = WhatsAppLinkBuilder.Build("03001234567", "🏪 Shop ⚠️ 🙏")!;

        // 🏪 is U+1F3EA: four UTF-8 bytes, escaped as one character, never as two broken halves.
        link.Url.Should().Contain("%F0%9F%8F%AA");
        Uri.UnescapeDataString(link.Url.Split("text=")[^1]).Should().Be("🏪 Shop ⚠️ 🙏");
    }

    [Fact]
    public void Returns_nothing_when_there_is_no_number_on_file()
    {
        // FR-044: the UI disables the send action and states the reason.
        WhatsAppLinkBuilder.Build(null, "Hello").Should().BeNull();
        WhatsAppLinkBuilder.Build("rubbish", "Hello").Should().BeNull();
    }

    [Fact]
    public void Url_encodes_the_message()
    {
        var link = WhatsAppLinkBuilder.Build("03001234567", "Total: Rs 1,100.00 & thanks");

        link!.Url.Should().NotContain(" ");
        link.Url.Should().NotContain("&thanks");
        link.Url.Should().Contain("%20");
    }

    [Fact]
    public void Appends_the_document_link_to_the_message()
    {
        var link = WhatsAppLinkBuilder.Build(
            "03001234567", "Your receipt:", "https://shop.example/api/public/documents/abc123");

        Uri.UnescapeDataString(link!.Url).Should()
            .Contain("https://shop.example/api/public/documents/abc123");
    }

    [Fact]
    public void Works_without_a_document_link()
    {
        var link = WhatsAppLinkBuilder.Build("03001234567", "Hello", documentUrl: null);

        link.Should().NotBeNull();
    }
}
