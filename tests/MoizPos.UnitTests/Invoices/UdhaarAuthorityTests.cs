using FluentAssertions;
using MoizPos.Application.Calculations;
using MoizPos.Domain.Enums;

namespace MoizPos.UnitTests.Invoices;

/// <summary>
/// Who may let goods leave against a debt. The owner, always. A field salesman, only to a customer
/// the owner has marked as an udhaar customer. The counter, never.
/// </summary>
public sealed class UdhaarAuthorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_owner_may_give_udhaar_to_anyone(bool udhaarCustomer)
    {
        UdhaarAuthority.MayLeaveOwing(UserRole.Admin, sellerJob: null, udhaarCustomer).Should().BeTrue();
    }

    [Fact]
    public void A_field_salesman_may_give_udhaar_to_an_udhaar_customer()
    {
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, StaffJob.FieldSales, isUdhaarCustomer: true).Should().BeTrue();
    }

    [Fact]
    public void A_field_salesman_may_not_give_udhaar_to_anyone_else()
    {
        // A new customer, or one the owner has not marked: the owner's decision, not the salesman's.
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, StaffJob.FieldSales, isUdhaarCustomer: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_counter_may_never_give_udhaar(bool udhaarCustomer)
    {
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, StaffJob.Counter, udhaarCustomer).Should().BeFalse();
        UdhaarAuthority.MayLeaveOwing(UserRole.Staff, sellerJob: null, udhaarCustomer).Should().BeFalse();
    }
}
