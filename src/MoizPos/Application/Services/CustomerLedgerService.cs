using System.Globalization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Customers;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

public sealed record ReceivePaymentRequest
{
    public long CustomerId { get; init; }

    public decimal Amount { get; init; }

    public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.Cash;

    public string? Note { get; init; }

    /// <summary>Required when the amount exceeds what is owed (FR-022).</summary>
    public bool ConfirmOverpayment { get; init; }
}

public sealed record ReceivePaymentResult(
    long PaymentId,
    string ReceiptNumber,
    decimal Amount,
    decimal BalanceAfter);

public interface ICustomerLedgerService
{
    Task<ReceivePaymentResult> ReceivePaymentAsync(
        ReceivePaymentRequest request, long userId, CancellationToken cancellationToken = default);

    Task<PagedResult<LedgerEntryRow>> LedgerAsync(
        long customerId, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<CustomerSummaryRow> SummaryAsync(long customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what a customer already owed before the software was in use, or corrects that
    /// figure (FR-065 … FR-071). A second call is a correction, never an additional debt.
    /// </summary>
    Task<OpeningBalanceResult> SetOpeningBalanceAsync(
        long customerId, SetOpeningBalanceRequest request, long userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The udhaar register.
///
/// Receiving a payment is one transaction: record the payment, reduce the customer's balance,
/// and append the ledger entry that explains it. The running balance follows the shopkeeper's
/// own rule — previous balance plus new bill minus new payment (FR-020, FR-021).
/// </summary>
public sealed class CustomerLedgerService : ICustomerLedgerService
{
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICustomerPaymentWriteRepository _payments;
    private readonly ILedgerRepository _ledger;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public CustomerLedgerService(
        IUnitOfWorkFactory unitOfWorkFactory,
        ICustomerPaymentWriteRepository payments,
        ILedgerRepository ledger,
        IAuditWriter audit,
        IClock clock)
    {
        _unitOfWorkFactory = unitOfWorkFactory;
        _payments = payments;
        _ledger = ledger;
        _audit = audit;
        _clock = clock;
    }

    public async Task<ReceivePaymentResult> ReceivePaymentAsync(
        ReceivePaymentRequest request,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (request.Amount <= 0m)
        {
            throw new BusinessRuleViolationException("Payment amount must be greater than zero.");
        }

        var nowUtc = _clock.UtcNow;

        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        var customer = await _payments.LockCustomerAsync(uow, request.CustomerId, cancellationToken)
            ?? throw new NotFoundException("Customer", request.CustomerId);

        // Throws OverpaymentNotConfirmedException unless the caller deliberately allowed it, so a
        // balance never goes negative by accident (FR-022).
        var newBalance = LedgerBalanceCalculator.NextBalance(
            customer.OutstandingBalance,
            billAmount: 0m,
            paidAmount: request.Amount,
            confirmOverpayment: request.ConfirmOverpayment);

        var receiptNumber = await _payments.NextReceiptNumberAsync(uow, nowUtc.Year, cancellationToken);

        var paymentId = await _payments.InsertPaymentAsync(
            uow, request.CustomerId, receiptNumber, request.Amount, request.PaymentMethod,
            isOverpayment: newBalance < 0m, request.Note, userId, nowUtc, cancellationToken);

        await _payments.UpdateBalanceAsync(
            uow, request.CustomerId, newBalance, nowUtc, cancellationToken);

        await _payments.InsertLedgerEntryAsync(
            uow, request.CustomerId, nowUtc, LedgerEntryType.Payment, paymentId,
            billAmount: 0m, paidAmount: request.Amount, balanceAfter: newBalance,
            userId, nowUtc, note: request.Note, cancellationToken);

        await _audit.RecordAsync(
            uow, "Customer", request.CustomerId, "outstanding_balance",
            customer.OutstandingBalance.ToString(CultureInfo.InvariantCulture),
            newBalance.ToString(CultureInfo.InvariantCulture),
            "Payment", userId, nowUtc, cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return new ReceivePaymentResult(paymentId, receiptNumber, request.Amount, newBalance);
    }

    public async Task<OpeningBalanceResult> SetOpeningBalanceAsync(
        long customerId,
        SetOpeningBalanceRequest request,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNow;

        await using var uow = await _unitOfWorkFactory.BeginAsync(cancellationToken);

        var (exists, existing, outstanding) =
            await _payments.LockForOpeningBalanceAsync(uow, customerId, cancellationToken);

        if (!exists)
        {
            throw new NotFoundException("Customer", customerId);
        }

        OpeningBalanceRules.Validate(request.Amount, existing, request.Reason);

        var amount = OpeningBalanceRules.Normalize(request.Amount);
        var wasCorrection = OpeningBalanceRules.IsCorrection(existing);

        // The DIFFERENCE, not the requested amount. Applying the amount would add the debt a
        // second time and silently double what the customer owes (FR-070).
        var delta = OpeningBalanceRules.Delta(existing, amount);
        var newBalance = outstanding + delta;

        await _payments.SetOpeningBalanceAsync(uow, customerId, amount, nowUtc, cancellationToken);

        if (delta != 0m)
        {
            await _payments.UpdateBalanceAsync(uow, customerId, newBalance, nowUtc, cancellationToken);

            // A first recording is the carried-forward entry itself; a correction is appended as
            // an Adjustment so the original entry survives untouched (FR-071). Either way the
            // register's own rule holds: balance_after = previous + bill − paid.
            await _payments.InsertLedgerEntryAsync(
                uow,
                customerId,
                nowUtc,
                wasCorrection ? LedgerEntryType.Adjustment : LedgerEntryType.OpeningBalance,
                referenceId: null,
                billAmount: delta > 0m ? delta : 0m,
                paidAmount: delta < 0m ? -delta : 0m,
                balanceAfter: newBalance,
                userId,
                nowUtc,
                note: NoteFor(wasCorrection, existing, amount, request.Reason),
                cancellationToken);
        }

        await _audit.RecordAsync(
            uow, "Customer", customerId, "opening_balance",
            existing?.ToString(CultureInfo.InvariantCulture),
            amount.ToString(CultureInfo.InvariantCulture),
            wasCorrection ? "OpeningBalanceCorrected" : "OpeningBalanceRecorded",
            userId, nowUtc, cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return new OpeningBalanceResult
        {
            CustomerId = customerId,
            OpeningBalance = amount,
            PreviousOpeningBalance = existing,
            OutstandingBalance = newBalance,
            WasCorrection = wasCorrection,
        };
    }

    /// <summary>
    /// What the ledger will show for this entry. A correction states both figures, so the reader
    /// can see what changed without cross-referencing the audit trail.
    /// </summary>
    private static string? NoteFor(
        bool wasCorrection, decimal? existing, decimal amount, string? reason)
    {
        if (!wasCorrection)
        {
            return string.IsNullOrWhiteSpace(reason) ? "Brought forward" : reason.Trim();
        }

        return $"Corrected from {existing:N2} to {amount:N2}. {reason?.Trim()}".Trim();
    }

    public async Task<PagedResult<LedgerEntryRow>> LedgerAsync(
        long customerId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<LedgerEntryRow>.Normalize(page, pageSize);

        var (items, total) = await _ledger.ListForCustomerAsync(
            customerId, fromUtc, toUtc, normalizedPage, normalizedSize, cancellationToken);

        return new PagedResult<LedgerEntryRow>(items, normalizedPage, normalizedSize, total);
    }

    public Task<CustomerSummaryRow> SummaryAsync(
        long customerId,
        CancellationToken cancellationToken = default) =>
        _ledger.SummaryForCustomerAsync(customerId, cancellationToken);
}
