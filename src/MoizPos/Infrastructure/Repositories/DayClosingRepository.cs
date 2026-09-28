using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Services;
using MoizPos.Application.Time;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class DayClosingRepository : IDayClosingRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DayClosingRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<DayCashMovement> CashMovementAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Four questions, one round trip. Each one is deliberately narrow about what "cash" means.
        return await connection.QuerySingleAsync<DayCashMovement>(
            """
            SELECT
                -- What was actually handed over at the counter, not what was billed. A credit
                -- sale put no notes in the drawer, and a partly paid one put in only the part.
                --
                -- 'Partial' counts as cash: this shop's part payments are handed over in notes at
                -- the counter, and payment_method does not record how the paid portion arrived.
                -- Excluding it would understate what is expected and show a false surplus every
                -- time — which teaches the shopkeeper to ignore the difference.
                COALESCE((
                    SELECT SUM(amount_paid) FROM invoices
                    WHERE invoice_date_utc >= @startUtc AND invoice_date_utc < @endUtc
                      AND payment_method IN ('Cash', 'Partial')
                ), 0) AS CashSales,

                -- An old debt paid off today. Not a sale — the goods left weeks ago — but the
                -- notes are in the drawer tonight.
                COALESCE((
                    SELECT SUM(amount) FROM customer_payments
                    WHERE payment_date_utc >= @startUtc AND payment_date_utc < @endUtc
                      AND payment_method = 'Cash'
                ), 0) AS CashRecovery,

                -- Money handed back on a return. Only where the sale had actually been paid:
                -- a return against unpaid udhaar reduces a balance and moves no notes.
                COALESCE((
                    SELECT SUM(refund_due) FROM sale_returns
                    WHERE return_date_utc >= @startUtc AND return_date_utc < @endUtc
                ), 0) AS CashRefunds,

                -- Expenses taken out of the till. Rows with payment_source NULL predate the
                -- question and are deliberately absent: guessing at them would put an invented
                -- figure into a reconciliation whose whole value is that it is not invented.
                COALESCE((
                    SELECT SUM(amount) FROM expenses
                    WHERE expense_date_utc >= @startUtc AND expense_date_utc < @endUtc
                      AND payment_source = 'Till'
                ), 0) AS CashPaidOut,

                -- The other way notes leave the till, and the one that moves the largest
                -- amounts. Missing it reported a short for money paid out legitimately.
                COALESCE((
                    SELECT SUM(amount) FROM supplier_payments
                    WHERE payment_date_utc >= @startUtc AND payment_date_utc < @endUtc
                      AND payment_method = 'Cash'
                ), 0) AS CashToSuppliers;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task InsertAsync(
        DateOnly closingDate,
        decimal openingFloat,
        DayCashMovement movement,
        decimal expectedCash,
        decimal countedCash,
        decimal difference,
        string? note,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Every figure is written down as it stood tonight. Recomputing them later would let a
        // return taken tomorrow quietly rewrite what was counted this evening.
        await connection.ExecuteAsync(
            """
            INSERT INTO day_closings
                (closing_date, opening_float, cash_sales, cash_recovery, cash_refunds,
                 cash_paid_out, cash_to_suppliers, expected_cash, counted_cash, difference, note,
                 closed_by_user_id, closed_at_utc)
            VALUES
                (@closingDate, @openingFloat, @cashSales, @cashRecovery, @cashRefunds,
                 @cashPaidOut, @cashToSuppliers, @expectedCash, @countedCash, @difference, @note,
                 @userId, @nowUtc);
            """,
            new
            {
                closingDate = closingDate.ToDateTime(TimeOnly.MinValue),
                openingFloat,
                cashSales = movement.CashSales,
                cashRecovery = movement.CashRecovery,
                cashRefunds = movement.CashRefunds,
                cashPaidOut = movement.CashPaidOut,
                cashToSuppliers = movement.CashToSuppliers,
                expectedCash,
                countedCash,
                difference,
                note,
                userId,
                nowUtc,
            });
    }

    private const string ClosingColumns = """
        c.closing_date   AS ClosingDate,
        c.opening_float  AS OpeningFloat,
        c.cash_sales     AS CashSales,
        c.cash_recovery  AS CashRecovery,
        c.cash_refunds   AS CashRefunds,
        c.cash_paid_out  AS CashPaidOut,
        c.cash_to_suppliers AS CashToSuppliers,
        c.expected_cash  AS ExpectedCash,
        c.counted_cash   AS CountedCash,
        c.difference     AS Difference,
        c.note           AS Note,
        u.full_name      AS ClosedByUserName,
        c.closed_at_utc  AS ClosedAtUtc,
        TRUE             AS IsClosed
        """;

    public async Task<DayClosingDto?> FindByDateAsync(
        DateOnly closingDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<DayClosingDto>(
            $"""
             SELECT {ClosingColumns}
             FROM day_closings c
             JOIN users u ON u.id = c.closed_by_user_id
             WHERE c.closing_date = @closingDate
             LIMIT 1;
             """,
            new { closingDate = closingDate.ToDateTime(TimeOnly.MinValue) });
    }

    public async Task<IReadOnlyList<DayClosingDto>> RecentAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DayClosingDto>(
            $"""
             SELECT {ClosingColumns}
             FROM day_closings c
             JOIN users u ON u.id = c.closed_by_user_id
             ORDER BY c.closing_date DESC
             LIMIT @count;
             """,
            new { count });

        return rows.AsList();
    }
}
