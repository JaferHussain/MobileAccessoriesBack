using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class ExpenseRepository : IExpenseRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ExpenseRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<ExpenseCategory>> ListCategoriesAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ExpenseCategory>(
            """
            SELECT id AS Id, name AS Name, is_active AS IsActive
            FROM expense_categories
            WHERE @includeInactive OR is_active = TRUE
            ORDER BY is_active DESC, name;
            """,
            new { includeInactive });

        return rows.AsList();
    }

    public async Task<bool> CategoryNameTakenAsync(
        string name, long? exceptId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM expense_categories WHERE name = @name AND (@exceptId IS NULL OR id <> @exceptId));",
            new { name, exceptId });
    }

    public async Task RenameCategoryAsync(long id, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync("UPDATE expense_categories SET name = @name WHERE id = @id;", new { id, name });
    }

    public async Task SetCategoryActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE expense_categories SET is_active = @isActive WHERE id = @id;", new { id, isActive });
    }

    public async Task<long> CreateCategoryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO expense_categories (name, is_active, created_at_utc)
            VALUES (@name, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new { name });
    }

    public async Task<(IReadOnlyList<ExpenseRow> Items, int TotalItems)> SearchAsync(
        long? categoryId,
        DateRangeUtc? range,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = "WHERE 1 = 1";

        if (categoryId is not null)
        {
            filter += " AND e.category_id = @categoryId";
        }

        if (range is not null)
        {
            filter += " AND e.expense_date_utc >= @startUtc AND e.expense_date_utc < @endUtc";
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT e.id               AS Id,
                    e.category_id      AS CategoryId,
                    c.name             AS CategoryName,
                    e.amount           AS Amount,
                    e.expense_date_utc AS ExpenseDateUtc,
                    e.payment_source   AS PaymentSource,
                    e.payment_method   AS PaymentMethod,
                    (e.payment_proof_path IS NOT NULL) AS HasProof,
                    e.shop_account_id  AS ShopAccountId,
                    sa.name            AS ShopAccountName,
                    e.transaction_id   AS TransactionId,
                    e.note             AS Note
             FROM expenses e
             JOIN expense_categories c ON c.id = e.category_id
             LEFT JOIN shop_accounts sa ON sa.id = e.shop_account_id
             {filter}
             ORDER BY e.expense_date_utc DESC, e.id DESC
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM expenses e {filter};
             """,
            new
            {
                categoryId,
                startUtc = range?.StartUtc,
                endUtc = range?.EndUtc,
                limit = pageSize,
                offset = (page - 1) * pageSize,
            });

        var items = (await reader.ReadAsync<ExpenseRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<long> CreateAsync(
        long categoryId,
        decimal amount,
        DateTime expenseDateUtc,
        PaymentSource paymentSource,
        PaymentMethod? paymentMethod,
        long? shopAccountId,
        string? transactionId,
        string? note,
        long userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO expenses
                (category_id, amount, payment_source, payment_method, shop_account_id, transaction_id,
                 expense_date_utc, note, user_id, created_at_utc)
            VALUES (@categoryId, @amount, @paymentSource, @paymentMethod, @shopAccountId, @transactionId,
                    @expenseDateUtc, @note, @userId,
                    UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            // Spelled out, like every other enum column in this schema: no enum type handler is
            // registered, so passing the value straight through writes its underlying int and the
            // ENUM column rejects it.
            new
            {
                categoryId,
                amount,
                paymentSource = paymentSource.ToString(),
                paymentMethod = paymentMethod?.ToString(),
                shopAccountId,
                transactionId,
                expenseDateUtc,
                note,
                userId,
            });
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync("DELETE FROM expenses WHERE id = @id;", new { id });
    }

    public async Task<decimal> SumForPeriodAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Half-open range: an expense belongs to exactly one period (FR-034).
        //
        // Commission paid to the salesman is a cost of the shop like any other, so it comes off net
        // profit here, in the period it was PAID — and SumByCategoryAsync lists it as its own line,
        // so the category breakdown always adds up to this total.
        return await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE((SELECT SUM(amount) FROM expenses
                             WHERE expense_date_utc >= @startUtc AND expense_date_utc < @endUtc), 0)
                 + COALESCE((SELECT SUM(amount) FROM commission_payouts
                             WHERE paid_at_utc >= @startUtc AND paid_at_utc < @endUtc), 0);
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });
    }

    public async Task<IReadOnlyList<(string Category, decimal Total)>> SumByCategoryAsync(
        DateRangeUtc range,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<(string, decimal)>(
            """
            SELECT Category, Total FROM (
                SELECT c.name AS Category, COALESCE(SUM(e.amount), 0) AS Total
                FROM expenses e
                JOIN expense_categories c ON c.id = e.category_id
                WHERE e.expense_date_utc >= @startUtc AND e.expense_date_utc < @endUtc
                GROUP BY c.name

                UNION ALL

                -- Its own line, so the breakdown adds up to SumForPeriodAsync.
                SELECT 'Salesman commission', SUM(amount)
                FROM commission_payouts
                WHERE paid_at_utc >= @startUtc AND paid_at_utc < @endUtc
                HAVING SUM(amount) > 0
            ) costs
            ORDER BY Total DESC;
            """,
            new { startUtc = range.StartUtc, endUtc = range.EndUtc });

        return rows.AsList();
    }
}
