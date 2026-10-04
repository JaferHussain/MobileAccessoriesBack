using Dapper;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class PurchaseBillRepository : IPurchaseBillRepository
{
    private const string Columns =
        """
        b.id AS Id, b.supplier_id AS SupplierId, s.name AS SupplierName, b.bill_number AS BillNumber,
        b.bill_date AS BillDate, b.total AS Total,
        COALESCE((SELECT SUM(sp.amount) FROM supplier_payments sp WHERE sp.purchase_bill_id = b.id), 0) AS Paid,
        COALESCE((SELECT SUM(r.total) FROM purchase_returns r
                  JOIN purchases rp ON rp.id = r.purchase_id
                  WHERE rp.purchase_bill_id = b.id), 0) AS Returned,
        (SELECT COUNT(*) FROM purchases p WHERE p.purchase_bill_id = b.id) AS ItemCount,
        (b.bill_image_path IS NOT NULL) AS HasBillImage,
        b.note AS Note, u.full_name AS RecordedBy
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public PurchaseBillRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<PurchaseBillRow>> ListAsync(long? supplierId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PurchaseBillRow>(
            $"""
             SELECT {Columns}
             FROM purchase_bills b
             JOIN suppliers s ON s.id = b.supplier_id
             JOIN users u ON u.id = b.user_id
             WHERE (@supplierId IS NULL OR b.supplier_id = @supplierId)
             ORDER BY b.bill_date DESC, b.id DESC
             LIMIT @limit;
             """,
            new { supplierId, limit });

        return rows.AsList();
    }

    public async Task<PurchaseBillRow?> FindAsync(long billId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<PurchaseBillRow>(
            $"""
             SELECT {Columns}
             FROM purchase_bills b
             JOIN suppliers s ON s.id = b.supplier_id
             JOIN users u ON u.id = b.user_id
             WHERE b.id = @billId;
             """,
            new { billId });
    }

    public async Task<IReadOnlyList<PurchaseBillLineRow>> LinesAsync(long billId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PurchaseBillLineRow>(
            """
            SELECT p.id AS PurchaseId, p.product_id AS ProductId, pr.name AS ProductName, p.quantity AS Quantity,
                   p.unit_cost AS UnitCost, p.total AS Total, p.returned_qty AS ReturnedQty
            FROM purchases p
            JOIN products pr ON pr.id = p.product_id
            WHERE p.purchase_bill_id = @billId
            ORDER BY p.id;
            """,
            new { billId });

        return rows.AsList();
    }

    public async Task<IReadOnlyList<PurchaseBillPaymentRow>> PaymentsAsync(long billId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PurchaseBillPaymentRow>(
            """
            SELECT id AS Id, amount AS Amount, CAST(payment_method AS CHAR) AS PaymentMethod,
                   payment_date_utc AS PaymentDateUtc, note AS Note,
                   (payment_proof_path IS NOT NULL) AS HasProof
            FROM supplier_payments
            WHERE purchase_bill_id = @billId
            ORDER BY payment_date_utc, id;
            """,
            new { billId });

        return rows.AsList();
    }
}
