using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class CommissionRepository : ICommissionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public CommissionRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<CommissionLineRow>> LinesAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<CommissionLineRow>(
            """
            SELECT ii.id               AS InvoiceItemId,
                   i.id                AS InvoiceId,
                   i.invoice_number    AS InvoiceNumber,
                   i.invoice_date_utc  AS InvoiceDateUtc,
                   i.customer_id       AS CustomerId,
                   c.name              AS CustomerName,
                   ii.product_name     AS ProductName,
                   ii.quantity         AS Quantity,
                   ii.returned_qty     AS ReturnedQty,
                   ii.line_total       AS LineTotal,
                   ii.base_unit_price  AS BaseUnitPrice,
                   ii.commission_rate  AS CommissionRate,
                   i.subtotal          AS InvoiceSubtotal,
                   i.total             AS InvoiceTotal,
                   i.net_amount        AS InvoiceNet,
                   i.amount_paid       AS InvoiceAmountPaid
            FROM invoice_items ii
            JOIN invoices i ON i.id = ii.invoice_id
            LEFT JOIN customers c ON c.id = i.customer_id
            WHERE i.user_id = @userId
              AND ii.base_unit_price IS NOT NULL
              AND ii.commission_rate IS NOT NULL
            ORDER BY i.invoice_date_utc DESC, ii.id;
            """,
            new { userId });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<CustomerDebtRow>> DebtsAsync(
        IReadOnlyCollection<long> customerIds, CancellationToken cancellationToken = default)
    {
        if (customerIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Sales at what they are worth now (returns already taken off), with what was paid at the
        // counter; and amounts brought forward from the paper register, which are older than any
        // sale and settled first. A ledger id is negated so it can never collide with an invoice id.
        var rows = await connection.QueryAsync<CustomerDebtRow>(
            """
            SELECT i.customer_id AS CustomerId, i.id AS DebtId, i.invoice_date_utc AS OnDateUtc,
                   i.net_amount AS Amount, LEAST(i.amount_paid, i.net_amount) AS PaidAtSale
            FROM invoices i
            WHERE i.customer_id IN @customerIds

            UNION ALL

            SELECT l.customer_id, -CAST(l.id AS SIGNED), l.entry_date_utc, l.bill_amount, 0
            FROM ledger_entries l
            WHERE l.customer_id IN @customerIds
              AND l.entry_type IN ('OpeningBalance', 'Adjustment')
              AND l.bill_amount > 0;
            """,
            new { customerIds });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<CustomerCreditRow>> CreditsAsync(
        IReadOnlyCollection<long> customerIds, CancellationToken cancellationToken = default)
    {
        if (customerIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Money the customer paid later — and a correction that lowered an amount brought forward,
        // which settles their debt the same way a payment does.
        var rows = await connection.QueryAsync<CustomerCreditRow>(
            """
            SELECT cp.customer_id AS CustomerId, cp.payment_date_utc AS OnDateUtc, cp.amount AS Amount
            FROM customer_payments cp
            WHERE cp.customer_id IN @customerIds

            UNION ALL

            SELECT l.customer_id, l.entry_date_utc, l.paid_amount
            FROM ledger_entries l
            WHERE l.customer_id IN @customerIds
              AND l.entry_type = 'Adjustment'
              AND l.paid_amount > 0;
            """,
            new { customerIds });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<CommissionPayoutRow>> PayoutsAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<CommissionPayoutRow>(
            """
            SELECT p.id AS Id, p.amount AS Amount, CAST(p.payment_method AS CHAR) AS PaymentMethod,
                   p.note AS Note, p.paid_at_utc AS PaidAtUtc, u.full_name AS RecordedBy,
                   (p.payment_proof_path IS NOT NULL) AS HasProof
            FROM commission_payouts p
            JOIN users u ON u.id = p.recorded_by_user_id
            WHERE p.user_id = @userId
            ORDER BY p.paid_at_utc DESC, p.id DESC;
            """,
            new { userId });

        return rows.AsList();
    }

    public async Task<long> InsertPayoutAsync(
        long userId, decimal amount, PaymentMethod method, string? note, long recordedByUserId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO commission_payouts
                (user_id, amount, payment_method, note, paid_at_utc, recorded_by_user_id, created_at_utc)
            VALUES (@userId, @amount, @method, @note, @nowUtc, @recordedByUserId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            // The enum spelled out: Dapper would otherwise write its int, which the ENUM rejects.
            new { userId, amount, method = method.ToString(), note, recordedByUserId, nowUtc });
    }
}
