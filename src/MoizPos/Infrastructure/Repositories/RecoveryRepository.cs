using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class RecoveryRepository : IRecoveryRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RecoveryRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<RecoveryCustomerRow>> OwingCustomersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<RecoveryCustomerRow>(
            """
            SELECT id                  AS Id,
                   name                AS Name,
                   mobile_number       AS MobileNumber,
                   credit_allowed      AS IsUdhaarCustomer,
                   outstanding_balance AS OutstandingBalance
            FROM customers
            WHERE is_active = TRUE AND outstanding_balance > 0
            ORDER BY id;
            """);

        return rows.AsList();
    }

    public async Task<IReadOnlyList<RecoveryMovementRow>> MovementsAsync(
        IReadOnlyList<long> customerIds, CancellationToken cancellationToken = default)
    {
        if (customerIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Ordered by id within each customer — the order entries were written, which is what
        // "oldest first" means here (an opening balance is not back-dated).
        var rows = await connection.QueryAsync<RecoveryMovementRow>(
            """
            SELECT l.customer_id                              AS CustomerId,
                   l.entry_date_utc                           AS EntryDateUtc,
                   CAST(l.entry_type AS CHAR)                 AS EntryType,
                   l.reference_id                             AS ReferenceId,
                   COALESCE(i.invoice_number, p.receipt_number) AS ReferenceNumber,
                   l.bill_amount                              AS BillAmount,
                   l.paid_amount                              AS PaidAmount
            FROM ledger_entries l
            LEFT JOIN invoices i          ON l.entry_type = 'Invoice' AND i.id = l.reference_id
            LEFT JOIN customer_payments p ON l.entry_type = 'Payment' AND p.id = l.reference_id
            WHERE l.customer_id IN @customerIds
            ORDER BY l.customer_id, l.id;
            """,
            new { customerIds });

        return rows.AsList();
    }
}
