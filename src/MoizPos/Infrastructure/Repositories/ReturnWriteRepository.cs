using System.Globalization;
using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class ReturnWriteRepository : IReturnWriteRepository
{
    public async Task<InvoiceSnapshot?> LockInvoiceAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<InvoiceSnapshot>(
            """
            SELECT id AS Id, invoice_number AS InvoiceNumber, customer_id AS CustomerId,
                   subtotal AS Subtotal, order_discount AS OrderDiscount,
                   total AS Total, amount_paid AS AmountPaid,
                   amount_remaining AS AmountRemaining, net_amount AS NetAmount
            FROM invoices
            WHERE id = @invoiceId
            FOR UPDATE;
            """,
            new { invoiceId },
            unitOfWork.Transaction);
    }

    public async Task<IReadOnlyList<InvoiceItemSnapshot>> LockInvoiceItemsAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        IReadOnlyList<long> invoiceItemIds,
        CancellationToken cancellationToken = default)
    {
        var rows = await unitOfWork.Connection.QueryAsync<InvoiceItemSnapshot>(
            """
            SELECT id AS Id, invoice_id AS InvoiceId, product_id AS ProductId,
                   product_name AS ProductName, quantity AS Quantity, returned_qty AS ReturnedQty,
                   unit_sale_price AS UnitSalePrice, unit_cost_price AS UnitCostPrice,
                   line_discount AS LineDiscount, line_total AS LineTotal
            FROM invoice_items
            WHERE invoice_id = @invoiceId AND id IN @invoiceItemIds
            ORDER BY id
            FOR UPDATE;
            """,
            new { invoiceId, invoiceItemIds },
            unitOfWork.Transaction);

        return rows.AsList();
    }

    public async Task<PurchaseSnapshot?> LockPurchaseAsync(
        IUnitOfWork unitOfWork,
        long purchaseId,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<PurchaseSnapshot>(
            """
            SELECT id AS Id, supplier_id AS SupplierId, product_id AS ProductId,
                   quantity AS Quantity, returned_qty AS ReturnedQty, unit_cost AS UnitCost
            FROM purchases
            WHERE id = @purchaseId
            FOR UPDATE;
            """,
            new { purchaseId },
            unitOfWork.Transaction);
    }

    public async Task<long> InsertSaleReturnAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        string returnNumber,
        DateTime returnDateUtc,
        decimal totalAmount,
        decimal refundDue,
        string? reason,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO sale_returns
                (invoice_id, return_number, return_date_utc, total_amount, refund_due, reason,
                 user_id, created_at_utc)
            VALUES
                (@invoiceId, @returnNumber, @returnDateUtc, @totalAmount, @refundDue, @reason,
                 @userId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                invoiceId, returnNumber, returnDateUtc, totalAmount, refundDue, reason, userId, nowUtc,
            },
            unitOfWork.Transaction);
    }

    public async Task InsertSaleReturnItemsAsync(
        IUnitOfWork unitOfWork,
        long saleReturnId,
        IReadOnlyList<SaleReturnItemToWrite> items,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            INSERT INTO sale_return_items
                (sale_return_id, invoice_item_id, product_id, quantity, unit_sale_price,
                 unit_refund_price, unit_cost_price, discount_total, line_total)
            VALUES
                (@saleReturnId, @InvoiceItemId, @ProductId, @Quantity, @UnitSalePrice,
                 @UnitRefundPrice, @UnitCostPrice, @DiscountTotal, @LineTotal);
            """,
            items.Select(item => new
            {
                saleReturnId,
                item.InvoiceItemId,
                item.ProductId,
                item.Quantity,
                item.UnitSalePrice,
                item.UnitRefundPrice,
                item.UnitCostPrice,
                item.DiscountTotal,
                item.LineTotal,
            }),
            unitOfWork.Transaction);
    }

    public async Task IncrementInvoiceItemReturnedAsync(
        IUnitOfWork unitOfWork,
        long invoiceItemId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            UPDATE invoice_items
            SET returned_qty = returned_qty + @quantity
            WHERE id = @invoiceItemId;
            """,
            new { invoiceItemId, quantity },
            unitOfWork.Transaction);
    }

    public async Task ReduceInvoiceNetAmountAsync(
        IUnitOfWork unitOfWork,
        long invoiceId,
        decimal newNetAmount,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            "UPDATE invoices SET net_amount = @newNetAmount WHERE id = @invoiceId;",
            new { invoiceId, newNetAmount },
            unitOfWork.Transaction);
    }

    public async Task<long> InsertPurchaseReturnAsync(
        IUnitOfWork unitOfWork,
        long purchaseId,
        long supplierId,
        long productId,
        string returnNumber,
        DateTime returnDateUtc,
        int quantity,
        decimal unitCost,
        decimal total,
        string? reason,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO purchase_returns
                (purchase_id, supplier_id, product_id, return_number, return_date_utc, quantity,
                 unit_cost, total, reason, user_id, created_at_utc)
            VALUES
                (@purchaseId, @supplierId, @productId, @returnNumber, @returnDateUtc, @quantity,
                 @unitCost, @total, @reason, @userId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                purchaseId, supplierId, productId, returnNumber, returnDateUtc, quantity,
                unitCost, total, reason, userId, nowUtc,
            },
            unitOfWork.Transaction);
    }

    public async Task IncrementPurchaseReturnedAsync(
        IUnitOfWork unitOfWork,
        long purchaseId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            "UPDATE purchases SET returned_qty = returned_qty + @quantity WHERE id = @purchaseId;",
            new { purchaseId, quantity },
            unitOfWork.Transaction);
    }

    /// <summary>Next return number for the year, e.g. SRT-2026-000012 or PRT-2026-000003.</summary>
    public async Task<string> NextReturnNumberAsync(
        IUnitOfWork unitOfWork,
        string prefix,
        int year,
        CancellationToken cancellationToken = default)
    {
        var table = prefix == "SRT" ? "sale_returns" : "purchase_returns";
        var fullPrefix = $"{prefix}-{year}-";

        var lastNumber = await unitOfWork.Connection.ExecuteScalarAsync<string?>(
            $"""
             SELECT return_number FROM {table}
             WHERE return_number LIKE @pattern
             ORDER BY return_number DESC
             LIMIT 1
             FOR UPDATE;
             """,
            new { pattern = $"{fullPrefix}%" },
            unitOfWork.Transaction);

        var next = 1;

        if (lastNumber is { Length: > 0 }
            && int.TryParse(
                lastNumber[fullPrefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var lastSequence))
        {
            next = lastSequence + 1;
        }

        return fullPrefix + next.ToString("D6", CultureInfo.InvariantCulture);
    }
}
