using FluentAssertions;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Invoices;

/// <summary>
/// To WHOM a sale may leave money owing — the owner's two kinds of customer. (WHO may give it is
/// <see cref="UdhaarAuthority"/>.) Full udhaar needs a registered udhaar customer; a part payment
/// is open to anyone the shop can find again, which means a phone number.
/// </summary>
public sealed class CreditEligibilityTests
{
    [Fact]
    public void A_fully_paid_sale_asks_nothing_of_anyone() =>
        CreditEligibility.Refusal(amountPaid: 1000m, amountRemaining: 0m, isUdhaarCustomer: false, hasMobile: false)
            .Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    public void A_registered_udhaar_customer_may_owe_all_or_part(int paid) =>
        CreditEligibility.Refusal(paid, 1000m - paid, isUdhaarCustomer: true, hasMobile: false).Should().BeNull();

    [Fact]
    public void Full_udhaar_is_refused_to_anyone_not_registered() =>
        CreditEligibility.Refusal(0m, 1000m, isUdhaarCustomer: false, hasMobile: true)
            .Should().Contain("registered udhaar customers");

    [Fact]
    public void A_part_payment_is_allowed_to_anyone_with_a_phone_number() =>
        CreditEligibility.Refusal(400m, 600m, isUdhaarCustomer: false, hasMobile: true).Should().BeNull();

    [Fact]
    public void A_part_payment_with_no_phone_number_is_refused_because_it_could_never_be_collected() =>
        CreditEligibility.Refusal(400m, 600m, isUdhaarCustomer: false, hasMobile: false)
            .Should().Contain("phone number");
}
