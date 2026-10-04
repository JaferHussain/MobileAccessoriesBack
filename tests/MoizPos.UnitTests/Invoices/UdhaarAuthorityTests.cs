using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Enums;

namespace MoizPos.UnitTests.Invoices;

/// <summary>
/// WHO may let goods leave against a debt. The owner, always. A field salesman, only to one of the
/// owner's udhaar customers. The counter shopkeeper may take a PART payment — never the whole bill
/// on udhaar. (To WHOM is <see cref="CreditEligibility"/>.)
/// </summary>
public sealed class UdhaarAuthorityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_owner_may_leave_money_owing_either_way(bool udhaarCustomer, bool partPayment) =>
        UdhaarAuthority.MayLeaveOwing(UserRole.Admin, sellerJob: null, udhaarCustomer, partPayment).Should().BeTrue();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_field_salesman_may_give_udhaar_or_take_part_to_an_udhaar_customer(bool partPayment) =>
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, StaffJob.FieldSales, isUdhaarCustomer: true, partPayment)
            .Should().BeTrue();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_field_salesman_may_not_leave_anything_owing_with_anyone_else(bool partPayment) =>
        // A walk-in, or a customer the owner has not registered: the owner's decision, not his.
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, StaffJob.FieldSales, isUdhaarCustomer: false, partPayment)
            .Should().BeFalse();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_counter_may_take_a_part_payment(bool udhaarCustomer)
    {
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, StaffJob.Counter, udhaarCustomer, isPartPayment: true).Should().BeTrue();
        // Staff created before jobs existed work the counter.
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, sellerJob: null, udhaarCustomer, isPartPayment: true).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_counter_may_never_take_the_whole_bill_on_udhaar(bool udhaarCustomer)
    {
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, StaffJob.Counter, udhaarCustomer, isPartPayment: false).Should().BeFalse();
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, sellerJob: null, udhaarCustomer, isPartPayment: false).Should().BeFalse();
    }
}
