using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Time;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>
/// One supplier's account for a period.
/// </summary>
/// <param name="OpeningBalance">
/// What was owed before <c>From</c> — the balance the listed period opens on. Zero when no range
/// was asked for, because then the list starts at the supplier's first dealing.
/// </param>
/// <param name="TotalPurchased">All-time, not just the period, so the three totals always explain
/// <c>PayableBalance</c>.</param>
public sealed record SupplierLedgerResult(
    long SupplierId,
    string SupplierName,
    decimal PayableBalance,
    decimal TotalPurchased,
    decimal TotalReturned,
    decimal TotalPaid,
    DateOnly? From,
    DateOnly? To,
    decimal OpeningBalance,
    IReadOnlyList<SupplierLedgerEntry> Entries);

public interface ISupplierLedgerService
{
    Task<SupplierLedgerResult> LedgerAsync(
        long supplierId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);
}

/// <summary>
/// A supplier's account, worked out from what the shop already records. Admin-only at the
/// controller: it carries purchase cost and what the shop owes (FR-040).
/// </summary>
public sealed class SupplierLedgerService : ISupplierLedgerService
{
    private readonly ISupplierRepository _suppliers;
    private readonly ISupplierLedgerRepository _ledger;
    private readonly PeriodResolver _periods;

    public SupplierLedgerService(
        ISupplierRepository suppliers, ISupplierLedgerRepository ledger, PeriodResolver periods)
    {
        _suppliers = suppliers;
        _ledger = ledger;
        _periods = periods;
    }

    public async Task<SupplierLedgerResult> LedgerAsync(
        long supplierId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        var supplier = await _suppliers.FindByIdAsync(supplierId, cancellationToken)
            ?? throw new NotFoundException("Supplier", supplierId);

        if (from is { } start && to is { } end && end < start)
        {
            throw new BusinessRuleViolationException("The end date cannot be before the start date.");
        }

        // The whole history first, so every line's balance is the true running figure — a range
        // only chooses which lines to SHOW, never where the arithmetic starts.
        var all = SupplierLedger.Build(await _ledger.RowsAsync(supplierId, cancellationToken));
        var totals = SupplierLedger.Totals(all);

        // Each end resolved on its own: an open end means "from the first dealing" or "up to now",
        // and shop-local days become half-open UTC instants so a line lands in exactly one period.
        var startUtc = from is { } first ? _periods.ResolveLocalDateRange(first, first).StartUtc : DateTime.MinValue;
        var endUtc = to is { } last ? _periods.ResolveLocalDateRange(last, last).EndUtc : DateTime.MaxValue;

        var before = all.LastOrDefault(entry => entry.EntryDateUtc < startUtc);
        var shown = all.Where(entry => entry.EntryDateUtc >= startUtc && entry.EntryDateUtc < endUtc).ToList();

        return new SupplierLedgerResult(
            supplier.Id,
            supplier.Name,
            supplier.PayableBalance,
            totals.Purchased,
            totals.Returned,
            totals.Paid,
            from,
            to,
            before?.BalanceAfter ?? 0m,
            shown);
    }
}
