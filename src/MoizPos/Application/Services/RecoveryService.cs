using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Time;

namespace MoizPos.Application.Services;

/// <summary>One bill still owed, in the shop's words.</summary>
public sealed record RecoveryBill(
    DateOnly OnDate, string EntryType, long? ReferenceId, string? ReferenceNumber, decimal BillAmount, decimal Remaining,
    OpenBillStatus Status);

/// <summary>One customer who owes money: how much, since when, by when, and which bills.</summary>
public sealed record RecoveryAccount(
    long CustomerId,
    string Name,
    string? MobileNumber,
    bool IsUdhaarCustomer,
    decimal Outstanding,
    DateOnly? UnpaidSince,
    DateOnly? DueOn,
    int MonthsOverdue,
    int NotPaidBills,
    int PartPaidBills,
    IReadOnlyList<RecoveryBill> OpenBills);

/// <summary>Everyone who owes the shop money, most overdue first, with the totals the page leads with.</summary>
public sealed record RecoveryReport(
    decimal TotalOwed, int CustomersOwing, int OverdueCustomers, decimal OverdueAmount, IReadOnlyList<RecoveryAccount> Accounts);

public interface IRecoveryService
{
    Task<RecoveryReport> ReportAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The Recovery page: everyone who owes, chased most-overdue first. Built from the ledger with the
/// same two rules the rest of the shop uses — payments settle the oldest debt first
/// (<see cref="RecoverySettlement"/>), and udhaar is due a month after the oldest purchase still
/// unpaid (<see cref="UdhaarDueDate"/>) — so the page, the reminder and the receipt all agree.
/// Two reads, however many customers owe.
/// </summary>
public sealed class RecoveryService : IRecoveryService
{
    private readonly IRecoveryRepository _recovery;
    private readonly PeriodResolver _periods;
    private readonly IClock _clock;

    public RecoveryService(IRecoveryRepository recovery, PeriodResolver periods, IClock clock)
    {
        _recovery = recovery;
        _periods = periods;
        _clock = clock;
    }

    public async Task<RecoveryReport> ReportAsync(CancellationToken cancellationToken = default)
    {
        var customers = await _recovery.OwingCustomersAsync(cancellationToken);
        var movements = (await _recovery.MovementsAsync(customers.Select(c => c.Id).ToList(), cancellationToken))
            .ToLookup(row => row.CustomerId);

        var today = ShopDate(_clock.UtcNow);

        var accounts = customers
            .Select(customer =>
            {
                var open = RecoverySettlement.OpenBills(movements[customer.Id].Select(row => new RecoveryMovement(
                    ShopDate(row.EntryDateUtc), row.EntryType, row.ReferenceId, row.ReferenceNumber, row.BillAmount, row.PaidAmount)));

                // The oldest open bill is the one the clock runs from.
                var unpaidSince = open.Count > 0 ? open[0].OnDate : (DateOnly?)null;
                var due = unpaidSince is { } since ? UdhaarDueDate.For(since, asOf: today) : (UdhaarDue?)null;

                return new RecoveryAccount(
                    customer.Id,
                    customer.Name,
                    customer.MobileNumber,
                    customer.IsUdhaarCustomer,
                    customer.OutstandingBalance,
                    unpaidSince,
                    due?.DueOn,
                    due?.MonthsOverdue ?? 0,
                    open.Count(bill => bill.Status == OpenBillStatus.NotPaid),
                    open.Count(bill => bill.Status == OpenBillStatus.PartPaid),
                    open.Select(bill => new RecoveryBill(
                        bill.OnDate, bill.EntryType, bill.ReferenceId, bill.ReferenceNumber, bill.BillAmount, bill.Remaining,
                        bill.Status)).ToList());
            })
            .OrderByDescending(account => account.MonthsOverdue)
            .ThenBy(account => account.UnpaidSince ?? DateOnly.MaxValue)
            .ThenByDescending(account => account.Outstanding)
            .ToList();

        var overdue = accounts.Where(account => account.MonthsOverdue > 0).ToList();

        return new RecoveryReport(
            accounts.Sum(account => account.Outstanding),
            accounts.Count,
            overdue.Count,
            overdue.Sum(account => account.Outstanding),
            accounts);
    }

    private DateOnly ShopDate(DateTime instantUtc) => DateOnly.FromDateTime(_periods.ToShopLocal(instantUtc));
}
