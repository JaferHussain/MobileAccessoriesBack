using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>One product line's commission — how it was earned, and whether it has been yet.</summary>
public sealed record CommissionLine(
    long InvoiceId,
    string InvoiceNumber,
    DateOnly SaleDate,
    string? CustomerName,
    string ProductName,
    int UnitsSold,
    int UnitsReturned,
    decimal BaseUnitPrice,
    decimal SoldAtUnitPrice,
    decimal ExtraPerUnit,
    decimal RatePercent,
    decimal Commission,
    decimal Earned,
    decimal Pending,
    /// <summary>Earned, Pending, or PartEarned (udhaar partly recovered).</summary>
    string Status,
    /// <summary>The day the sale was paid for in full — when its commission was earned.</summary>
    DateOnly? EarnedOn);

public sealed record CommissionPayout(
    long Id, decimal Amount, string PaymentMethod, string? Note, DateTime PaidAtUtc, string RecordedBy, bool HasProof = false);

/// <summary>A salesman's commission account. All-time figures: Earned − PaidOut = Owed.</summary>
public sealed record CommissionStatement(
    long UserId,
    string FullName,
    string? Job,
    decimal TotalCommission,
    decimal Earned,
    decimal Pending,
    decimal PaidOut,
    decimal Owed,
    IReadOnlyList<CommissionLine> Lines,
    IReadOnlyList<CommissionPayout> Payouts,
    /// <summary>Set only in answer to a payout just recorded — so its proof can be attached straight after.</summary>
    long? PayoutId = null);

public interface ICommissionService
{
    Task<CommissionStatement> StatementAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>Records commission paid to a salesman. Never more than he is owed.</summary>
    Task<CommissionStatement> PayAsync(
        long userId, decimal amount, PaymentMethod method, string? note, long paidByUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A field salesman's commission — the owner's rule: half of whatever he sells above the owner's
/// price, earned once the customer has paid for it.
///
/// <para><b>Earned is worked out, never stored.</b> A customer's payments settle their oldest
/// debt first (<see cref="Commission.Settle"/>), across every sale they owe for — so a sale is
/// earned the day the udhaar on it is recovered, and nothing can drift from the ledger. Only what
/// the owner has PAID the salesman is stored.</para>
/// </summary>
public sealed class CommissionService : ICommissionService
{
    private readonly ICommissionRepository _commission;
    private readonly IUserRepository _users;
    private readonly PeriodResolver _periods;
    private readonly IClock _clock;

    public CommissionService(ICommissionRepository commission, IUserRepository users, PeriodResolver periods, IClock clock)
    {
        _commission = commission;
        _users = users;
        _periods = periods;
        _clock = clock;
    }

    public async Task<CommissionStatement> StatementAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User", userId);

        var rows = await _commission.LinesAsync(userId, cancellationToken);
        var settlements = await SettlementsAsync(rows, cancellationToken);

        var lines = rows.Select(row =>
        {
            var soldAt = ReturnPricing.EffectiveUnitPrice(row.LineTotal, row.Quantity, row.InvoiceSubtotal, row.InvoiceTotal);
            var commission = Commission.ForLine(soldAt, row.BaseUnitPrice, row.Quantity - row.ReturnedQty, row.CommissionRate);
            var settlement = settlements.GetValueOrDefault(row.InvoiceId, new DebtSettlement(1m, ShopDate(row.InvoiceDateUtc)));
            var earned = Commission.Earned(commission, settlement.Share);

            return new CommissionLine(
                row.InvoiceId,
                row.InvoiceNumber,
                ShopDate(row.InvoiceDateUtc),
                row.CustomerName,
                row.ProductName,
                row.Quantity,
                row.ReturnedQty,
                row.BaseUnitPrice,
                soldAt,
                Math.Max(0m, soldAt - row.BaseUnitPrice),
                row.CommissionRate,
                commission,
                earned,
                commission - earned,
                earned == commission ? "Earned" : earned == 0m ? "Pending" : "PartEarned",
                earned == commission && commission > 0m ? settlement.SettledOn : null);
        }).ToList();

        var payouts = (await _commission.PayoutsAsync(userId, cancellationToken))
            .Select(payout => new CommissionPayout(
                payout.Id, payout.Amount, payout.PaymentMethod, payout.Note, payout.PaidAtUtc, payout.RecordedBy, payout.HasProof))
            .ToList();

        var totalCommission = lines.Sum(line => line.Commission);
        var earnedTotal = lines.Sum(line => line.Earned);
        var paidOut = payouts.Sum(payout => payout.Amount);

        return new CommissionStatement(
            user.Id,
            user.FullName,
            user.Job?.ToString(),
            totalCommission,
            earnedTotal,
            totalCommission - earnedTotal,
            paidOut,
            earnedTotal - paidOut,
            lines,
            payouts);
    }

    public async Task<CommissionStatement> PayAsync(
        long userId, decimal amount, PaymentMethod method, string? note, long paidByUserId,
        CancellationToken cancellationToken = default)
    {
        if (method is PaymentMethod.Credit or PaymentMethod.Partial)
        {
            throw new BusinessRuleViolationException("Commission is paid in cash or by transfer.");
        }

        var statement = await StatementAsync(userId, cancellationToken);

        // Only what he has earned: commission on udhaar not yet recovered is not his yet.
        if (amount > statement.Owed)
        {
            throw new BusinessRuleViolationException(
                $"{statement.FullName} is owed Rs {statement.Owed:N2} in earned commission. " +
                "Commission on udhaar that has not been recovered is not earned yet.");
        }

        var payoutId = await _commission.InsertPayoutAsync(
            userId, amount, method, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), paidByUserId, _clock.UtcNow,
            cancellationToken);

        return await StatementAsync(userId, cancellationToken) with { PayoutId = payoutId };
    }

    /// <summary>How much of each of this salesman's sales has been paid, oldest debt first per customer.</summary>
    private async Task<IReadOnlyDictionary<long, DebtSettlement>> SettlementsAsync(
        IReadOnlyList<CommissionLineRow> rows, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, DebtSettlement>();

        // A walk-in sale cannot be left owing, so it was paid in full at the counter.
        foreach (var row in rows.Where(row => row.CustomerId is null))
        {
            result[row.InvoiceId] = new DebtSettlement(1m, ShopDate(row.InvoiceDateUtc));
        }

        var customerIds = rows.Where(row => row.CustomerId is not null).Select(row => row.CustomerId!.Value).Distinct().ToList();

        if (customerIds.Count == 0)
        {
            return result;
        }

        var debts = await _commission.DebtsAsync(customerIds, cancellationToken);
        var credits = await _commission.CreditsAsync(customerIds, cancellationToken);

        foreach (var customerId in customerIds)
        {
            var settled = Commission.Settle(
                debts.Where(debt => debt.CustomerId == customerId)
                    .Select(debt => new CommissionDebt(debt.DebtId, ShopDate(debt.OnDateUtc), debt.Amount, debt.PaidAtSale)),
                credits.Where(credit => credit.CustomerId == customerId)
                    .Select(credit => new CommissionCredit(ShopDate(credit.OnDateUtc), credit.Amount)));

            foreach (var (debtId, settlement) in settled)
            {
                result[debtId] = settlement;
            }
        }

        return result;
    }

    private DateOnly ShopDate(DateTime instantUtc) => DateOnly.FromDateTime(_periods.ToShopLocal(instantUtc));
}
