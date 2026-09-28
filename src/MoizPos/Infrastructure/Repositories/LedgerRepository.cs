using System.Globalization;
using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class LedgerRepository : ILedgerRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public LedgerRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<LedgerEntryRow> Items, int TotalItems)> ListForCustomerAsync(
        long customerId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = "WHERE l.customer_id = @customerId";

        if (fromUtc is not null)
        {
            filter += " AND l.entry_date_utc >= @fromUtc";
        }

        if (toUtc is not null)
        {
            filter += " AND l.entry_date_utc < @toUtc";
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // The reference number comes from whichever document the entry points at, so the ledger
        // reads like the shopkeeper's register: "INV-2026-000123" or "RCP-2026-000045".
        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT l.id             AS Id,
                    l.entry_date_utc AS EntryDateUtc,
                    l.entry_type     AS EntryType,
                    l.reference_id   AS ReferenceId,
                    COALESCE(i.invoice_number, p.receipt_number) AS ReferenceNumber,
                    l.bill_amount    AS BillAmount,
                    l.paid_amount    AS PaidAmount,
                    l.balance_after  AS BalanceAfter,
                    l.note           AS Note
             FROM ledger_entries l
             LEFT JOIN invoices i
                 ON l.entry_type = 'Invoice' AND i.id = l.reference_id
             LEFT JOIN customer_payments p
                 ON l.entry_type = 'Payment' AND p.id = l.reference_id
             {filter}
             ORDER BY l.entry_date_utc, l.id
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM ledger_entries l {filter};
             """,
            new { customerId, fromUtc, toUtc, limit = pageSize, offset = (page - 1) * pageSize });

        var items = (await reader.ReadAsync<LedgerEntryRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<CustomerSummaryRow> SummaryForCustomerAsync(
        long customerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleAsync<CustomerSummaryRow>(
            """
            SELECT
                COALESCE(SUM(l.bill_amount), 0) AS TotalPurchased,
                COALESCE(SUM(l.paid_amount), 0) AS TotalPaid,
                (SELECT outstanding_balance FROM customers WHERE id = @customerId) AS TotalOutstanding,
                (SELECT COUNT(*) FROM invoices WHERE customer_id = @customerId) AS InvoiceCount
            FROM ledger_entries l
            WHERE l.customer_id = @customerId;
            """,
            new { customerId });
    }

    public async Task<IReadOnlyList<LedgerMovementRow>> MovementsAsync(
        long customerId,
        long? throughPaymentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Ordered by id — the order entries were written. An opening balance is not back-dated, so
        // id order and time order agree.
        var rows = await connection.QueryAsync<LedgerMovementRow>(
            """
            SELECT entry_date_utc AS EntryDateUtc,
                   bill_amount    AS BillAmount,
                   paid_amount    AS PaidAmount
            FROM ledger_entries
            WHERE customer_id = @customerId
              AND (@throughPaymentId IS NULL OR id <= (
                    SELECT p.id FROM ledger_entries p
                    WHERE p.entry_type = 'Payment' AND p.reference_id = @throughPaymentId
                    ORDER BY p.id
                    LIMIT 1))
            ORDER BY id;
            """,
            new { customerId, throughPaymentId });

        return rows.AsList();
    }
}

/// <inheritdoc />
public sealed class CustomerPaymentWriteRepository : ICustomerPaymentWriteRepository
{
    public async Task<CustomerBalanceSnapshot?> LockCustomerAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<CustomerBalanceSnapshot>(
            """
            SELECT id AS Id, name AS Name, outstanding_balance AS OutstandingBalance
            FROM customers
            WHERE id = @customerId
            FOR UPDATE;
            """,
            new { customerId },
            unitOfWork.Transaction);
    }

    public async Task<(bool Exists, decimal? OpeningBalance, decimal OutstandingBalance)>
        LockForOpeningBalanceAsync(
            IUnitOfWork unitOfWork,
            long customerId,
            CancellationToken cancellationToken = default)
    {
        // FOR UPDATE for the same reason receiving a payment does it: a payment taken at the
        // counter while the owner is fixing an opening figure would otherwise read the same
        // starting balance and one would silently overwrite the other's result.
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<(decimal? Opening, decimal Outstanding)?>(
            """
            SELECT opening_balance AS Opening, outstanding_balance AS Outstanding
            FROM customers
            WHERE id = @customerId
            FOR UPDATE;
            """,
            new { customerId },
            unitOfWork.Transaction);

        return row is null ? (false, null, 0m) : (true, row.Value.Opening, row.Value.Outstanding);
    }

    public async Task SetOpeningBalanceAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        decimal openingBalance,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        // Deliberately does NOT touch outstanding_balance. That moves by the difference, applied
        // by the service through UpdateBalanceAsync — writing the figure in both places from here
        // is exactly how a correction would turn into a second debt (FR-070).
        await unitOfWork.Connection.ExecuteAsync(
            """
            UPDATE customers
            SET opening_balance = @openingBalance, updated_at_utc = @nowUtc
            WHERE id = @customerId;
            """,
            new { customerId, openingBalance, nowUtc },
            unitOfWork.Transaction);
    }

    public async Task<long> InsertPaymentAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        string receiptNumber,
        decimal amount,
        PaymentMethod paymentMethod,
        bool isOverpayment,
        string? note,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO customer_payments
                (customer_id, receipt_number, amount, payment_method, payment_date_utc,
                 is_overpayment, note, user_id, created_at_utc)
            VALUES
                (@customerId, @receiptNumber, @amount, @paymentMethod, @nowUtc,
                 @isOverpayment, @note, @userId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                customerId,
                receiptNumber,
                amount,
                paymentMethod = paymentMethod.ToString(),
                isOverpayment,
                note,
                userId,
                nowUtc,
            },
            unitOfWork.Transaction);
    }

    public async Task UpdateBalanceAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        decimal newBalance,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            UPDATE customers
            SET outstanding_balance = @newBalance, updated_at_utc = @nowUtc
            WHERE id = @customerId;
            """,
            new { customerId, newBalance, nowUtc },
            unitOfWork.Transaction);
    }

    public async Task InsertLedgerEntryAsync(
        IUnitOfWork unitOfWork,
        long customerId,
        DateTime entryDateUtc,
        LedgerEntryType entryType,
        long? referenceId,
        decimal billAmount,
        decimal paidAmount,
        decimal balanceAfter,
        long userId,
        DateTime nowUtc,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            INSERT INTO ledger_entries
                (customer_id, entry_date_utc, entry_type, reference_id, bill_amount, paid_amount,
                 balance_after, note, user_id, created_at_utc)
            VALUES
                (@customerId, @entryDateUtc, @entryType, @referenceId, @billAmount, @paidAmount,
                 @balanceAfter, @note, @userId, @nowUtc);
            """,
            new
            {
                customerId,
                entryDateUtc,
                entryType = entryType.ToString(),
                referenceId,
                billAmount,
                paidAmount,
                balanceAfter,
                note,
                userId,
                nowUtc,
            },
            unitOfWork.Transaction);
    }

    /// <summary>Next receipt number for the year, e.g. RCP-2026-000045.</summary>
    public async Task<string> NextReceiptNumberAsync(
        IUnitOfWork unitOfWork,
        int year,
        CancellationToken cancellationToken = default)
    {
        var prefix = $"RCP-{year}-";

        var lastNumber = await unitOfWork.Connection.ExecuteScalarAsync<string?>(
            """
            SELECT receipt_number FROM customer_payments
            WHERE receipt_number LIKE @pattern
            ORDER BY receipt_number DESC
            LIMIT 1
            FOR UPDATE;
            """,
            new { pattern = $"{prefix}%" },
            unitOfWork.Transaction);

        var next = 1;

        if (lastNumber is { Length: > 0 }
            && int.TryParse(
                lastNumber[prefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var lastSequence))
        {
            next = lastSequence + 1;
        }

        return prefix + next.ToString("D6", CultureInfo.InvariantCulture);
    }
}
