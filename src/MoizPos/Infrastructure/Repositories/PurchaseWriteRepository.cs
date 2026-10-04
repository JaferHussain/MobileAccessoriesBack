using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class PurchaseWriteRepository : IPurchaseWriteRepository
{
    public async Task<ProductStockSnapshot?> LockProductAsync(
        IUnitOfWork unitOfWork,
        long productId,
        CancellationToken cancellationToken = default)
    {
        // FOR UPDATE holds the row until the transaction ends, so a concurrent purchase or sale
        // of the same product waits rather than overwriting our stock figure (research.md R4).
        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<ProductStockSnapshot>(
            """
            SELECT id AS Id,
                   name AS Name,
                   quantity_on_hand AS QuantityOnHand,
                   cost_price AS CostPrice,
                   retail_price AS RetailPrice,
                   wholesale_price AS WholesalePrice
            FROM products
            WHERE id = @productId
            FOR UPDATE;
            """,
            new { productId },
            unitOfWork.Transaction);
    }

    public async Task<SupplierBalanceSnapshot?> LockSupplierAsync(
        IUnitOfWork unitOfWork,
        long supplierId,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<SupplierBalanceSnapshot>(
            """
            SELECT id AS Id, name AS Name, payable_balance AS PayableBalance
            FROM suppliers
            WHERE id = @supplierId
            FOR UPDATE;
            """,
            new { supplierId },
            unitOfWork.Transaction);
    }

    public async Task<long> InsertPurchaseAsync(
        IUnitOfWork unitOfWork,
        long supplierId,
        long productId,
        DateTime purchaseDateUtc,
        decimal unitCost,
        int quantity,
        decimal total,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default,
        long? purchaseBillId = null)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO purchases
                (supplier_id, purchase_bill_id, product_id, purchase_date_utc, unit_cost, quantity, total,
                 user_id, created_at_utc)
            VALUES
                (@supplierId, @purchaseBillId, @productId, @purchaseDateUtc, @unitCost, @quantity, @total,
                 @userId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            new { supplierId, purchaseBillId, productId, purchaseDateUtc, unitCost, quantity, total, userId, nowUtc },
            unitOfWork.Transaction);
    }

    public async Task<long> InsertPurchaseBillAsync(
        IUnitOfWork unitOfWork,
        long supplierId,
        string? billNumber,
        DateOnly billDate,
        decimal total,
        string? note,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO purchase_bills (supplier_id, bill_number, bill_date, total, note, user_id, created_at_utc)
            VALUES (@supplierId, @billNumber, @billDate, @total, @note, @userId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            new { supplierId, billNumber, billDate = billDate.ToDateTime(TimeOnly.MinValue), total, note, userId, nowUtc },
            unitOfWork.Transaction);
    }

    public async Task<PurchaseBillDueSnapshot?> LockPurchaseBillAsync(
        IUnitOfWork unitOfWork, long billId, CancellationToken cancellationToken = default)
    {
        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<PurchaseBillDueSnapshot>(
            """
            SELECT b.id AS Id, b.supplier_id AS SupplierId, b.bill_date AS BillDate, b.total AS Total,
                   COALESCE((SELECT SUM(sp.amount) FROM supplier_payments sp WHERE sp.purchase_bill_id = b.id), 0) AS Paid,
                   COALESCE((SELECT SUM(r.total) FROM purchase_returns r
                             JOIN purchases p ON p.id = r.purchase_id
                             WHERE p.purchase_bill_id = b.id), 0) AS Returned
            FROM purchase_bills b
            WHERE b.id = @billId
            FOR UPDATE;
            """,
            new { billId },
            unitOfWork.Transaction);
    }

    public async Task UpdateProductStockAndPricingAsync(
        IUnitOfWork unitOfWork,
        long productId,
        int newQuantity,
        decimal newCostPrice,
        decimal newRetailPrice,
        decimal newWholesalePrice,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            UPDATE products
            SET quantity_on_hand = @newQuantity,
                cost_price = @newCostPrice,
                retail_price = @newRetailPrice,
                wholesale_price = @newWholesalePrice,
                updated_at_utc = @nowUtc
            WHERE id = @productId;
            """,
            new { productId, newQuantity, newCostPrice, newRetailPrice, newWholesalePrice, nowUtc },
            unitOfWork.Transaction);
    }

    public async Task UpdateSupplierPayableAsync(
        IUnitOfWork unitOfWork,
        long supplierId,
        decimal newPayable,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.Connection.ExecuteAsync(
            """
            UPDATE suppliers
            SET payable_balance = @newPayable, updated_at_utc = @nowUtc
            WHERE id = @supplierId;
            """,
            new { supplierId, newPayable, nowUtc },
            unitOfWork.Transaction);
    }

    public async Task<long> InsertSupplierPaymentAsync(
        IUnitOfWork unitOfWork,
        long supplierId,
        decimal amount,
        PaymentMethod paymentMethod,
        bool isOverpayment,
        string? note,
        long? shopAccountId,
        long userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default,
        DateTime? paymentDateUtc = null,
        long? purchaseBillId = null)
    {
        return await unitOfWork.Connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO supplier_payments
                (supplier_id, purchase_bill_id, amount, payment_date_utc, payment_method, shop_account_id, is_overpayment,
                 note, user_id, created_at_utc)
            VALUES
                (@supplierId, @purchaseBillId, @amount, @paymentDateUtc, @paymentMethod, @shopAccountId, @isOverpayment,
                 @note, @userId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                supplierId,
                purchaseBillId,
                amount,
                paymentDateUtc = paymentDateUtc ?? nowUtc,
                nowUtc,
                paymentMethod = paymentMethod.ToString(),
                isOverpayment,
                note,
                shopAccountId,
                userId,
            },
            unitOfWork.Transaction);
    }
}
