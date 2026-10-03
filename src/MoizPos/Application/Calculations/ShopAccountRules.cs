using MoizPos.Domain.Enums;

namespace MoizPos.Application.Calculations;

/// <summary>
/// Which of the shop's accounts a payment can have come from. Pure, so the rule is tested without a
/// database, and shared by every screen that records one — expenses and supplier payments today.
/// </summary>
public static class ShopAccountRules
{
    /// <summary>
    /// The kind of account a payment method moves money through, or null when it moves none — cash
    /// left the drawer; credit and part payment describe money not yet paid.
    /// </summary>
    public static ShopAccountType? AccountTypeFor(PaymentMethod method) => method switch
    {
        // Raast moves money between bank accounts, so it leaves a bank account like a transfer.
        PaymentMethod.BankTransfer or PaymentMethod.Raast => ShopAccountType.Bank,
        PaymentMethod.JazzCash => ShopAccountType.JazzCash,
        PaymentMethod.EasyPaisa => ShopAccountType.EasyPaisa,
        _ => null,
    };

    public static bool Accepts(ShopAccountType type, PaymentMethod method) => AccountTypeFor(method) == type;

    public static string MismatchMessage(string accountName, PaymentMethod method) =>
        AccountTypeFor(method) is { } expected
            ? $"{accountName} cannot carry a {Label(method)} payment. Choose a {Label(expected)} account."
            : $"A {Label(method)} payment does not go through an account — leave the account empty.";

    private static string Label(PaymentMethod method) => method switch
    {
        PaymentMethod.BankTransfer => "bank transfer",
        _ => method.ToString(),
    };

    private static string Label(ShopAccountType type) => type == ShopAccountType.Bank ? "bank" : type.ToString();
}
