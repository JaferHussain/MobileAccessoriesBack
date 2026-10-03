using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class TeamRepository : ITeamRepository
{
    /// <summary>
    /// The shop's local day for an instant. Asia/Karachi is +05:00 all year (no daylight saving),
    /// so a fixed offset reads the same calendar day PeriodResolver does — and needs no time-zone
    /// tables on the server, which MariaDB does not always have.
    /// </summary>
    private const string KarachiDay = "DATE(DATE_ADD({0}, INTERVAL 5 HOUR))";

    private const string TransferMethods = "('BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast')";

    private readonly IDbConnectionFactory _connectionFactory;

    public TeamRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<TeamMemberRow>> MembersAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Every active person, the owner included — a card of zeroes is itself worth seeing.
        var rows = await connection.QueryAsync<TeamMemberRow>(
            """
            SELECT u.id                               AS UserId,
                   u.full_name                        AS FullName,
                   CAST(u.role AS CHAR)               AS Role,
                   CAST(u.job AS CHAR)                AS Job,
                   COALESCE(s.invoice_count, 0)       AS InvoiceCount,
                   COALESCE(s.total_sales, 0)         AS TotalSales,
                   COALESCE(s.received, 0)            AS ReceivedAtSale,
                   COALESCE(s.credit, 0)              AS CreditGiven,
                   COALESCE(s.discount, 0)            AS DiscountGiven,
                   COALESCE(r.return_count, 0)        AS ReturnCount,
                   COALESCE(r.return_value, 0)        AS ReturnValue,
                   COALESCE(p.collected, 0)           AS UdhaarCollected,
                   l.last_login                       AS LastLoginUtc,
                   -- The cash a field salesman is holding right now, all-time: what day close
                   -- left out of the drawer, less what he has handed over (SalesmanCashService).
                   COALESCE((SELECT SUM(amount_paid) FROM invoices
                             WHERE user_id = u.id AND in_field = TRUE AND payment_method IN ('Cash', 'Partial')), 0)
                 + COALESCE((SELECT SUM(amount) FROM customer_payments
                             WHERE user_id = u.id AND in_field = TRUE AND payment_method = 'Cash'), 0)
                 - COALESCE((SELECT SUM(refund_due) FROM sale_returns
                             WHERE user_id = u.id AND in_field = TRUE AND refund_method = 'Cash'), 0)
                 - COALESCE((SELECT SUM(amount) FROM salesman_handovers WHERE user_id = u.id), 0)
                                                      AS CashInHand,
                   COALESCE((SELECT SUM(quantity) FROM salesman_stock WHERE user_id = u.id), 0) AS StockUnits
            FROM users u
            LEFT JOIN (
                SELECT i.user_id,
                       COUNT(*)                                        AS invoice_count,
                       SUM(i.net_amount)                               AS total_sales,
                       SUM(i.amount_paid)                              AS received,
                       SUM(i.amount_remaining)                         AS credit,
                       SUM(i.order_discount + COALESCE(d.line_discounts, 0)) AS discount
                FROM invoices i
                LEFT JOIN (
                    SELECT invoice_id, SUM(line_discount) AS line_discounts
                    FROM invoice_items
                    GROUP BY invoice_id
                ) d ON d.invoice_id = i.id
                WHERE i.invoice_date_utc >= @startUtc AND i.invoice_date_utc < @endUtc
                GROUP BY i.user_id
            ) s ON s.user_id = u.id
            LEFT JOIN (
                SELECT user_id, COUNT(*) AS return_count, SUM(total_amount) AS return_value
                FROM sale_returns
                WHERE return_date_utc >= @startUtc AND return_date_utc < @endUtc
                GROUP BY user_id
            ) r ON r.user_id = u.id
            LEFT JOIN (
                SELECT user_id, SUM(amount) AS collected
                FROM customer_payments
                WHERE payment_date_utc >= @startUtc AND payment_date_utc < @endUtc
                GROUP BY user_id
            ) p ON p.user_id = u.id
            LEFT JOIN (
                SELECT user_id, MAX(logged_in_at_utc) AS last_login
                FROM user_logins
                GROUP BY user_id
            ) l ON l.user_id = u.id
            WHERE u.is_active = TRUE
            ORDER BY (u.role = 'Admin') DESC, u.full_name;
            """,
            new { startUtc, endUtc });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<TeamActivityRow>> ActivityAsync(
        long userId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Everything this one person did, from the tables that already record who did it. Text
        // columns are CAST so the halves of the UNION agree on MariaDB as well as MySQL.
        var rows = await connection.QueryAsync<TeamActivityRow>(
            """
            SELECT * FROM (
                SELECT 'Sale' AS Kind, i.id AS ReferenceId, CAST(i.invoice_number AS CHAR) AS Reference,
                       i.invoice_date_utc AS EntryDateUtc, i.net_amount AS Amount,
                       CAST(i.payment_method AS CHAR) AS Method, CAST(c.name AS CHAR) AS Detail
                FROM invoices i
                LEFT JOIN customers c ON c.id = i.customer_id
                WHERE i.user_id = @userId

                UNION ALL
                SELECT 'Return', r.id, CAST(r.return_number AS CHAR), r.return_date_utc, r.total_amount,
                       CAST(r.refund_method AS CHAR), CAST(CONCAT('Against ', i.invoice_number) AS CHAR)
                FROM sale_returns r
                JOIN invoices i ON i.id = r.invoice_id
                WHERE r.user_id = @userId

                UNION ALL
                SELECT 'Recovery', cp.id, CAST(cp.receipt_number AS CHAR), cp.payment_date_utc, cp.amount,
                       CAST(cp.payment_method AS CHAR), CAST(c.name AS CHAR)
                FROM customer_payments cp
                JOIN customers c ON c.id = cp.customer_id
                WHERE cp.user_id = @userId

                UNION ALL
                SELECT 'SupplierPayment', sp.id, CAST(sp.note AS CHAR), sp.payment_date_utc, sp.amount,
                       CAST(sp.payment_method AS CHAR), CAST(s.name AS CHAR)
                FROM supplier_payments sp
                JOIN suppliers s ON s.id = sp.supplier_id
                WHERE sp.user_id = @userId

                UNION ALL
                SELECT 'Expense', e.id, CAST(ec.name AS CHAR), e.expense_date_utc, e.amount,
                       CAST(COALESCE(e.payment_method, e.payment_source) AS CHAR), CAST(e.note AS CHAR)
                FROM expenses e
                JOIN expense_categories ec ON ec.id = e.category_id
                WHERE e.user_id = @userId

                UNION ALL
                SELECT 'Purchase', pu.id, CAST(pr.name AS CHAR), pu.purchase_date_utc, pu.total,
                       CAST(NULL AS CHAR), CAST(CONCAT(pu.quantity, ' from ', s.name) AS CHAR)
                FROM purchases pu
                JOIN products pr ON pr.id = pu.product_id
                JOIN suppliers s ON s.id = pu.supplier_id
                WHERE pu.user_id = @userId

                UNION ALL
                SELECT 'SignIn', ul.id, CAST(ul.ip_address AS CHAR), ul.logged_in_at_utc, NULL,
                       CAST(NULL AS CHAR), CAST(ul.user_agent AS CHAR)
                FROM user_logins ul
                WHERE ul.user_id = @userId
            ) activity
            WHERE activity.EntryDateUtc >= @startUtc AND activity.EntryDateUtc < @endUtc
            ORDER BY activity.EntryDateUtc DESC, activity.ReferenceId DESC
            LIMIT 500;
            """,
            new { userId, startUtc, endUtc });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<WatchItemRow>> WatchItemsAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var returnDay = string.Format(System.Globalization.CultureInfo.InvariantCulture, KarachiDay, "r.return_date_utc");
        var saleDay = string.Format(System.Globalization.CultureInfo.InvariantCulture, KarachiDay, "i.invoice_date_utc");

        var rows = await connection.QueryAsync<WatchItemRow>(
            $"""
             SELECT * FROM (
                 -- Money taken by transfer with nothing to show for it yet.
                 SELECT 'TransferWithoutProof' AS Kind, i.id AS ReferenceId,
                        CAST(i.invoice_number AS CHAR) AS Reference, i.invoice_date_utc AS EntryDateUtc,
                        i.user_id AS UserId, CAST(u.full_name AS CHAR) AS UserName, i.amount_paid AS Amount,
                        CAST(i.payment_method AS CHAR) AS Detail
                 FROM invoices i
                 JOIN users u ON u.id = i.user_id
                 WHERE i.payment_method IN {TransferMethods} AND i.payment_proof_path IS NULL
                   AND i.invoice_date_utc >= @startUtc AND i.invoice_date_utc < @endUtc

                 UNION ALL
                 -- Someone taking back, the same day, a sale they made themselves.
                 SELECT 'SameDayReturn', r.id, CAST(r.return_number AS CHAR), r.return_date_utc,
                        r.user_id, CAST(u.full_name AS CHAR), r.total_amount,
                        CAST(CONCAT('Sold and returned the same day: ', i.invoice_number) AS CHAR)
                 FROM sale_returns r
                 JOIN invoices i ON i.id = r.invoice_id
                 JOIN users u ON u.id = r.user_id
                 WHERE r.user_id = i.user_id AND {returnDay} = {saleDay}
                   AND r.return_date_utc >= @startUtc AND r.return_date_utc < @endUtc

                 UNION ALL
                 -- Money leaving the shop by transfer, rather than across the counter.
                 SELECT 'TransferRefund', r.id, CAST(r.return_number AS CHAR), r.return_date_utc,
                        r.user_id, CAST(u.full_name AS CHAR), r.refund_due, CAST(r.refund_method AS CHAR)
                 FROM sale_returns r
                 JOIN users u ON u.id = r.user_id
                 WHERE r.refund_method IN {TransferMethods} AND r.refund_due > 0
                   AND r.return_date_utc >= @startUtc AND r.return_date_utc < @endUtc
             ) watch
             ORDER BY watch.EntryDateUtc DESC
             LIMIT 500;
             """,
            new { startUtc, endUtc });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<DiscountedSaleRow>> DiscountedSalesAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Line discounts are already inside the subtotal, so what the goods were listed at is the
        // subtotal plus them; the discount given is them plus the whole-bill discount.
        var rows = await connection.QueryAsync<DiscountedSaleRow>(
            """
            SELECT i.id AS InvoiceId, i.invoice_number AS InvoiceNumber, i.invoice_date_utc AS InvoiceDateUtc,
                   i.user_id AS UserId, u.full_name AS UserName,
                   i.subtotal + COALESCE(d.line_discounts, 0)       AS Gross,
                   i.order_discount + COALESCE(d.line_discounts, 0) AS Discount
            FROM invoices i
            JOIN users u ON u.id = i.user_id
            LEFT JOIN (
                SELECT invoice_id, SUM(line_discount) AS line_discounts
                FROM invoice_items
                GROUP BY invoice_id
            ) d ON d.invoice_id = i.id
            WHERE i.invoice_date_utc >= @startUtc AND i.invoice_date_utc < @endUtc
              AND (i.order_discount > 0 OR COALESCE(d.line_discounts, 0) > 0);
            """,
            new { startUtc, endUtc });

        return rows.AsList();
    }
}
