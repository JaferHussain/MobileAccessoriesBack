using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class SupplierLedgerRepository : ISupplierLedgerRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SupplierLedgerRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<SupplierLedgerRow>> RowsAsync(
        long supplierId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // The same three tables, and the same three columns, that the payable invariant sums:
        // purchases.total − purchase_returns.total − supplier_payments.amount. Reading anything
        // else would let the account disagree with the balance it is meant to explain.
        //
        // Every text column is CAST to CHAR so the three halves of the UNION agree on type and
        // collation on MariaDB as well as MySQL.
        var rows = await connection.QueryAsync<SupplierLedgerRow>(
            """
            SELECT 'Purchase'                 AS EntryType,
                   p.id                       AS ReferenceId,
                   p.purchase_date_utc        AS EntryDateUtc,
                   CAST(pr.name AS CHAR)      AS ProductName,
                   p.quantity                 AS Quantity,
                   CAST(NULL AS CHAR)         AS ReferenceNumber,
                   CAST(NULL AS CHAR)         AS PaymentMethod,
                   CAST(NULL AS CHAR)         AS Note,
                   p.total                    AS BillAmount,
                   0                          AS ReturnedAmount,
                   0                          AS PaidAmount,
                   FALSE                      AS HasProof,
                   CAST(NULL AS CHAR)         AS ShopAccountName
            FROM purchases p
            JOIN products pr ON pr.id = p.product_id
            WHERE p.supplier_id = @supplierId

            UNION ALL

            SELECT 'Return',
                   r.id,
                   r.return_date_utc,
                   CAST(pr.name AS CHAR),
                   r.quantity,
                   CAST(r.return_number AS CHAR),
                   CAST(NULL AS CHAR),
                   CAST(r.reason AS CHAR),
                   0,
                   r.total,
                   0,
                   FALSE,
                   CAST(NULL AS CHAR)
            FROM purchase_returns r
            JOIN products pr ON pr.id = r.product_id
            WHERE r.supplier_id = @supplierId

            UNION ALL

            SELECT 'Payment',
                   sp.id,
                   sp.payment_date_utc,
                   CAST(NULL AS CHAR),
                   NULL,
                   CAST(NULL AS CHAR),
                   CAST(sp.payment_method AS CHAR),
                   CAST(sp.note AS CHAR),
                   0,
                   0,
                   sp.amount,
                   (sp.payment_proof_path IS NOT NULL),
                   CAST(sa.name AS CHAR)
            FROM supplier_payments sp
            LEFT JOIN shop_accounts sa ON sa.id = sp.shop_account_id
            WHERE sp.supplier_id = @supplierId;
            """,
            new { supplierId });

        return rows.AsList();
    }
}
