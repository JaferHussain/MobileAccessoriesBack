using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class TransactionProofRepository : ITransactionProofRepository
{
    /// <summary>
    /// Where each kind keeps its proof, and what says how its money moved. Fixed strings, never
    /// built from input — the kind is an enum, so no request can name a table.
    /// </summary>
    private static (string Table, string ProofColumn, string MethodColumn) Where(ProofKind kind) => kind switch
    {
        ProofKind.Sale => ("invoices", "payment_proof_path", "payment_method"),
        ProofKind.CustomerPayment => ("customer_payments", "payment_proof_path", "payment_method"),
        ProofKind.SupplierPayment => ("supplier_payments", "payment_proof_path", "payment_method"),
        ProofKind.Refund => ("sale_returns", "refund_proof_path", "refund_method"),
        ProofKind.Expense => ("expenses", "payment_proof_path", "payment_source"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of proof."),
    };

    private readonly IDbConnectionFactory _connectionFactory;

    public TransactionProofRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<ProofTarget?> FindAsync(
        ProofKind kind, long id, CancellationToken cancellationToken = default)
    {
        var (table, proofColumn, methodColumn) = Where(kind);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ProofTarget>(
            $"""
            SELECT CAST({methodColumn} AS CHAR) AS Method, {proofColumn} AS ProofPath
            FROM {table}
            WHERE id = @id
            LIMIT 1;
            """,
            new { id });
    }

    public async Task SetProofPathAsync(
        ProofKind kind, long id, string relativePath, CancellationToken cancellationToken = default)
    {
        var (table, proofColumn, _) = Where(kind);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            $"UPDATE {table} SET {proofColumn} = @relativePath WHERE id = @id;",
            new { id, relativePath });
    }

    public async Task<IReadOnlyList<MissingProofRow>> MissingAsync(
        DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // The owner's rule, table by table: every way money moves except cash. A sale on Credit
        // took no money, and Partial is counted as cash at this counter (see Counting the drawer),
        // so only the four transfer methods need a screenshot. A return that refunded nothing
        // has no refund to prove. Text columns are CAST so the halves agree on MariaDB too.
        var rows = await connection.QueryAsync<MissingProofRow>(
            """
            SELECT * FROM (
                SELECT 'Sale'                          AS Kind,
                       i.id                            AS ReferenceId,
                       CAST(i.invoice_number AS CHAR)  AS Reference,
                       i.invoice_date_utc              AS EntryDateUtc,
                       CAST(c.name AS CHAR)            AS Party,
                       CAST(i.payment_method AS CHAR)  AS Method,
                       i.amount_paid                   AS Amount
                FROM invoices i
                LEFT JOIN customers c ON c.id = i.customer_id
                WHERE i.payment_method IN ('BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast')
                  AND i.payment_proof_path IS NULL

                UNION ALL

                SELECT 'CustomerPayment',
                       cp.id,
                       CAST(cp.receipt_number AS CHAR),
                       cp.payment_date_utc,
                       CAST(c.name AS CHAR),
                       CAST(cp.payment_method AS CHAR),
                       cp.amount
                FROM customer_payments cp
                JOIN customers c ON c.id = cp.customer_id
                WHERE cp.payment_method IN ('BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast')
                  AND cp.payment_proof_path IS NULL

                UNION ALL

                SELECT 'SupplierPayment',
                       sp.id,
                       CAST(sp.note AS CHAR),
                       sp.payment_date_utc,
                       CAST(s.name AS CHAR),
                       CAST(sp.payment_method AS CHAR),
                       sp.amount
                FROM supplier_payments sp
                JOIN suppliers s ON s.id = sp.supplier_id
                WHERE sp.payment_method IN ('BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast')
                  AND sp.payment_proof_path IS NULL

                UNION ALL

                SELECT 'Refund',
                       r.id,
                       CAST(r.return_number AS CHAR),
                       r.return_date_utc,
                       CAST(c.name AS CHAR),
                       CAST(r.refund_method AS CHAR),
                       r.refund_due
                FROM sale_returns r
                JOIN invoices i ON i.id = r.invoice_id
                LEFT JOIN customers c ON c.id = i.customer_id
                WHERE r.refund_method IN ('BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast')
                  AND r.refund_due > 0
                  AND r.refund_proof_path IS NULL

                UNION ALL

                SELECT 'Expense',
                       e.id,
                       CAST(ec.name AS CHAR),
                       e.expense_date_utc,
                       CAST(e.note AS CHAR),
                       CAST(COALESCE(e.payment_method, 'Bank') AS CHAR),
                       e.amount
                FROM expenses e
                JOIN expense_categories ec ON ec.id = e.category_id
                WHERE e.payment_source = 'Bank'
                  AND e.payment_proof_path IS NULL
            ) missing
            WHERE (@fromUtc IS NULL OR missing.EntryDateUtc >= @fromUtc)
              AND (@toUtc   IS NULL OR missing.EntryDateUtc <  @toUtc)
            ORDER BY missing.EntryDateUtc DESC, missing.Kind, missing.ReferenceId DESC
            LIMIT 500;
            """,
            new { fromUtc, toUtc });

        return rows.AsList();
    }
}
