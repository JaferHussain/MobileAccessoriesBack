using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Enums;

namespace MoizPos.UnitTests.Documents;

/// <summary>
/// Which of the shop's accounts a payment can have come from. A JazzCash payment left a JazzCash
/// wallet; a bank transfer or a Raast payment left a bank account; cash left the drawer and no
/// account at all.
/// </summary>
public sealed class ShopAccountRulesTests
{
    [Theory]
    [InlineData(PaymentMethod.BankTransfer, ShopAccountType.Bank)]
    [InlineData(PaymentMethod.Raast, ShopAccountType.Bank)]
    [InlineData(PaymentMethod.JazzCash, ShopAccountType.JazzCash)]
    [InlineData(PaymentMethod.EasyPaisa, ShopAccountType.EasyPaisa)]
    public void Each_transfer_method_goes_through_one_kind_of_account(PaymentMethod method, ShopAccountType type)
    {
        ShopAccountRules.AccountTypeFor(method).Should().Be(type);
        ShopAccountRules.Accepts(type, method).Should().BeTrue();
    }

    [Theory]
    [InlineData(PaymentMethod.Cash)]
    [InlineData(PaymentMethod.Credit)]
    [InlineData(PaymentMethod.Partial)]
    public void Cash_credit_and_part_payment_go_through_no_account(PaymentMethod method)
    {
        ShopAccountRules.AccountTypeFor(method).Should().BeNull();
    }

    [Fact]
    public void A_JazzCash_payment_cannot_have_left_a_bank_account()
    {
        ShopAccountRules.Accepts(ShopAccountType.Bank, PaymentMethod.JazzCash).Should().BeFalse();
        ShopAccountRules.Accepts(ShopAccountType.EasyPaisa, PaymentMethod.BankTransfer).Should().BeFalse();
    }

    [Fact]
    public void A_refusal_says_what_would_fit_in_words()
    {
        ShopAccountRules.MismatchMessage("HBL Current", PaymentMethod.JazzCash)
            .Should().Contain("HBL Current").And.Contain("JazzCash");
    }
}
