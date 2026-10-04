using MoizPos.Domain.Enums;

namespace MoizPos.Application.Calculations;

/// <summary>
/// WHO may let goods leave against a debt (FR-051, extended by migration 0034 and the owner's later
/// decision on part payments). To WHOM is <see cref="CreditEligibility"/>.
///
/// <list type="bullet">
///   <item>The owner, always.</item>
///   <item>A field salesman, only to one of the owner's registered udhaar customers — he sells in the
///   market to the shops the owner already gives credit to, but deciding who gets credit stays the
///   owner's.</item>
///   <item>The counter shopkeeper may take a <b>part payment</b> — some money now, the rest owed —
///   but never the whole bill on udhaar. That stays the owner's.</item>
/// </list>
///
/// <para>Applied in <c>InvoiceService</c> to the SERVER-recomputed totals, before the first write —
/// never to the request's own figures. "Part payment" means the recomputed amount paid is above
/// zero, whatever the sale is labelled.</para>
/// </summary>
public static class UdhaarAuthority
{
    public static bool MayLeaveOwing(UserRole role, StaffJob? sellerJob, bool isUdhaarCustomer, bool isPartPayment) =>
        role == UserRole.Admin
        || (sellerJob == StaffJob.FieldSales ? isUdhaarCustomer : isPartPayment);
}
