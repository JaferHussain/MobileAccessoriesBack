namespace MoizPos.Application.Calculations;

/// <summary>
/// To WHOM a sale may leave money owing — the owner's two kinds of customer. WHO may give it is
/// <see cref="UdhaarAuthority"/>; both are decided on the server-recomputed totals.
///
/// <list type="bullet">
///   <item>A registered <b>udhaar customer</b> (name, phone, ID card) may owe all or part.</item>
///   <item>Anyone else may <b>pay part</b> — a walk-in included — but only with a phone number:
///   a debt with no way to reach the debtor can never be collected.</item>
///   <item>Nobody else may take the whole bill on udhaar.</item>
/// </list>
/// </summary>
public static class CreditEligibility
{
    /// <returns>Why the sale is refused, in the shopkeeper's words — or null when it may go ahead.</returns>
    public static string? Refusal(decimal amountPaid, decimal amountRemaining, bool isUdhaarCustomer, bool hasMobile)
    {
        if (amountRemaining <= 0m || isUdhaarCustomer)
        {
            return null;
        }

        if (amountPaid <= 0m)
        {
            return "Full udhaar is only for registered udhaar customers. Take part of the payment, " +
                   "or register them under Udhaar customers first.";
        }

        return hasMobile
            ? null
            : "A part payment needs the customer's phone number, so the rest can be collected.";
    }
}
