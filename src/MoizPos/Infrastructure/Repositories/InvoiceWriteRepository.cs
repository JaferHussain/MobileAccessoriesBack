using System.Globalization;
using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class InvoiceWriteRepository : IInvoiceWriteRepository
{
    public async Task<IReadOnlyList<ProductStockSnapshot>> LockProductsAsync(
        IUnitOfWork unitOfWork,
        IReadOnlyList<long> productIds,
        CancellationToken cancellationToken = default)
    {
        // ORDER BY id inside the locking read: MySQL acquires the row locks in that order, so two
        // concurrent multi-line sales sharing products cannot deadlock (research.md R4).
        var rows = await unitOfWork.Connection.QueryAsync<ProductStockSnapshot>(
            """
            SELECT id AS Id,
                   name AS Name,
                   quantity_on_hand AS QuantityOnHand,
                   cost_price AS CostPrice,
                   retail_price AS RetailPrice,
                   wholesale_price AS WholesalePrice
            FROM products
            WHERE id IN @productIds
            ORDER BY id
            FOR UPDATE;
            """,
            new { productIds },
            unitOfWork.Transaction);

        return rows.AsList();
    }

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

    public async Task<long> InsertInvoiceAsync(
        IUnitOfWork unitOfWork,
        string invoiceNumber,
        long? customerId,
        DateTime invoiceDateUtc,
        SaleType saleType,
        decimal subtotal,
        decimal orderDiscount,
        decimal total,
        decimal amountPaid,
        decimal amountRemaining,
        PaymentMethod paymentMethod,
        string? paymentAccountNumber,
        string? paymentTransactionId,
        string? idempotencyKey,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO invoices
                (invoice_number, customer_id, invoice_date_utc, sale_type, subtotal, order_discount, total,
                 amount_paid, amount_remaining, net_amount, payment_method,
                 payment_account_number, payment_transaction_id, idempotency_key,
                 user_id, created_at_utc)
            VALUES
                (@invoiceNumber, @customerId, @invoiceDateUtc, @saleType, @subtotal, @orderDiscount, @total,
                 @amountPaid, @amountRemaining, @total, @paymentMethod,
                 @paymentAccountNumber, @paymentTransactionId, @idempotencyKey,
                 @userId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                invoiceNumber,
                customerId,
                invoiceDateUtc,
                // Stored by name, so a row read straight from the database says "Wholesale".
                saleType = saleType.ToString(),
                subtotal,
                orderDiscount,
                total,
                amountPaid,
                amountRemaining,
                paymentMethod = paymentMethod.ToString(),
                paymentAccountNumber,
                paymentTransactionId,
                idempotencyKey,
                userId,
                nowUtc,
            },
            unitOfWork.Transaction);
    }

    public async Task InsertInvoiceItemsAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        IReadOnlyList<InvoiceItemToWrite> items,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            INSERT INTO invoice_items
                (invoice_id, product_id, product_name, quantity, unit_sale_price, line_discount,
                 unit_cost_price, line_total)
            VALUES
                (@invoiceId, @ProductId, @ProductName, @Quantity, @UnitSalePrice, @LineDiscount,
                 @UnitCostPrice, @LineTotal);
            """,
            items.Select(item => new
            {
                invoiceId,
                item.ProductId,
                item.ProductName,
                item.Quantity,
                item.UnitSalePrice,
                item.LineDiscount,
                item.UnitCostPrice,
                item.LineTotal,
            }),
            unitOfWork.Transaction);
    }

    public async Task UpdateProductQuantityAsync(
        IUnitOfWork unitOfWork,
        long productId,
        int newQuantity,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            UPDATE products
            SET quantity_on_hand = @newQuantity, updated_at_utc = @nowUtc
            WHERE id = @productId;
            """,
            new { productId, newQuantity, nowUtc },
            unitOfWork.Transaction);
    }

    public async Task UpdateCustomerBalanceAsync(
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
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            INSERT INTO ledger_entries
                (customer_id, entry_date_utc, entry_type, reference_id, bill_amount, paid_amount,
                 balance_after, user_id, created_at_utc)
            VALUES
                (@customerId, @entryDateUtc, @entryType, @referenceId, @billAmount, @paidAmount,
                 @balanceAfter, @userId, @nowUtc);
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
                userId,
                nowUtc,
            },
            unitOfWork.Transaction);
    }

    public async Task<long?> FindByIdempotencyKeyAsync(
        IUnitOfWork unitOfWork,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM invoices WHERE idempotency_key = @idempotencyKey LIMIT 1;",
            new { idempotencyKey },
            unitOfWork.Transaction);
    }

    /// <summary>
    /// Next number for the year, e.g. INV-2026-000123.
    ///
    /// Read inside the caller's transaction, so two simultaneous sales cannot both claim the same
    /// number — the second waits on the first's locks and sees the committed maximum. Gaps are
    /// acceptable: a rolled-back sale simply does not use its number.
    /// </summary>
    public async Task<string> NextInvoiceNumberAsync(
        IUnitOfWork unitOfWork,
        int year,
        CancellationToken cancellationToken = default)
    {
        var prefix = $"INV-{year}-";

        var lastNumber = await unitOfWork.Connection.ExecuteScalarAsync<string?>(
            """
            SELECT invoice_number FROM invoices
            WHERE invoice_number LIKE @pattern
            ORDER BY invoice_number DESC
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
