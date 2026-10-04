using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class SalesmanCashRepository : ISalesmanCashRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SalesmanCashRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<SalesmanCashMovementRow>> MovementsAsync(
        long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Exactly the cash day close leaves out of the drawer (in_field = TRUE), so the two always
        // agree: what is not in the drawer is in his hand, until he hands it over. 'Partial' counts
        // as cash, as it does at the counter.
        var rows = await connection.QueryAsync<SalesmanCashMovementRow>(
            """
            SELECT * FROM (
                SELECT 'Sale' AS Kind, i.id AS ReferenceId, CAST(i.invoice_number AS CHAR) AS Reference,
                       i.invoice_date_utc AS EntryDateUtc, i.amount_paid AS Amount,
                       CAST(i.payment_method AS CHAR) AS Method, CAST(c.name AS CHAR) AS Detail,
                       FALSE AS HasProof
                FROM invoices i
                LEFT JOIN customers c ON c.id = i.customer_id
                WHERE i.user_id = @userId AND i.in_field = TRUE
                  AND i.payment_method IN ('Cash', 'Partial') AND i.amount_paid > 0

                UNION ALL
                SELECT 'Recovery', cp.id, CAST(cp.receipt_number AS CHAR), cp.payment_date_utc, cp.amount,
                       CAST(cp.payment_method AS CHAR), CAST(c.name AS CHAR), FALSE
                FROM customer_payments cp
                JOIN customers c ON c.id = cp.customer_id
                WHERE cp.user_id = @userId AND cp.in_field = TRUE AND cp.payment_method = 'Cash'

                UNION ALL
                SELECT 'Refund', r.id, CAST(r.return_number AS CHAR), r.return_date_utc, r.refund_due,
                       CAST(r.refund_method AS CHAR), CAST(CONCAT('Against ', i.invoice_number) AS CHAR), FALSE
                FROM sale_returns r
                JOIN invoices i ON i.id = r.invoice_id
                WHERE r.user_id = @userId AND r.in_field = TRUE AND r.refund_method = 'Cash' AND r.refund_due > 0

                UNION ALL
                SELECT 'Handover', h.id, CAST(NULL AS CHAR), h.received_at_utc, h.amount,
                       CAST(h.payment_method AS CHAR),
                       CAST(CONCAT('Received by ', u.full_name, COALESCE(CONCAT(' · ', h.note), '')) AS CHAR),
                       (h.payment_proof_path IS NOT NULL)
                FROM salesman_handovers h
                JOIN users u ON u.id = h.received_by_user_id
                WHERE h.user_id = @userId
            ) movements
            ORDER BY movements.EntryDateUtc DESC, movements.ReferenceId DESC;
            """,
            new { userId });

        return rows.AsList();
    }

    public async Task<long> InsertHandoverAsync(
        long userId, decimal amount, PaymentMethod method, string? note, long receivedByUserId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO salesman_handovers
                (user_id, amount, payment_method, note, received_at_utc, received_by_user_id, created_at_utc)
            VALUES (@userId, @amount, @method, @note, @nowUtc, @receivedByUserId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            // The enum spelled out: Dapper would otherwise write its int, which the ENUM rejects.
            new { userId, amount, method = method.ToString(), note, receivedByUserId, nowUtc });
    }
}
