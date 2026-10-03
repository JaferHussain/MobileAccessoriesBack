using MoizPos.Domain.Enums;

namespace MoizPos.Application.Calculations;

/// <summary>
/// Who may let goods leave against a debt (FR-051, extended by migration 0034).
///
/// <para>The owner, always. A field salesman, only to a customer the <b>owner</b> has marked as an
/// udhaar customer — he sells in the market to the shops the owner already gives credit to, but
/// deciding who gets credit stays the owner's. The counter, never.</para>
///
/// <para>Applied in <c>InvoiceService</c> to the SERVER-recomputed amount left owing, before the
/// first write, exactly as the original rule was — never to the request's own figures.</para>
/// </summary>
public static class UdhaarAuthority
{
    public static bool MayLeaveOwing(UserRole role, StaffJob? sellerJob, bool isUdhaarCustomer) =>
        role == UserRole.Admin || (sellerJob == StaffJob.FieldSales && isUdhaarCustomer);
}
