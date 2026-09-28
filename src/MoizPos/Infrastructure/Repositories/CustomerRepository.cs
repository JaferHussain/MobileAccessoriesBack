using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class CustomerRepository : ICustomerRepository
{
    private const string SelectColumns = """
        id                  AS Id,
        name                AS Name,
        mobile_number       AS MobileNumber,
        address             AS Address,
        sale_type           AS SaleType,
        outstanding_balance AS OutstandingBalance,
        is_active           AS IsActive
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public CustomerRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<Customer> Items, int TotalItems)> SearchAsync(
        string? search,
        bool withBalanceOnly,
        SaleType? saleType,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = "WHERE is_active = TRUE";

        if (!string.IsNullOrWhiteSpace(search))
        {
            filter += " AND (name LIKE @search OR mobile_number LIKE @search)";
        }

        if (withBalanceOnly)
        {
            filter += " AND outstanding_balance <> 0";
        }

        if (saleType is not null)
        {
            filter += " AND sale_type = @saleType";
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT {SelectColumns}
             FROM customers
             {filter}
             ORDER BY name, id
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM customers {filter};
             """,
            new
            {
                search = $"%{search?.Trim()}%",
                saleType = saleType?.ToString(),
                limit = pageSize,
                offset = (page - 1) * pageSize,
            });

        var items = (await reader.ReadAsync<Customer>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<Customer?> FindByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Customer>(
            $"SELECT {SelectColumns} FROM customers WHERE id = @id LIMIT 1;", new { id });
    }

    public async Task<long> CreateAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO customers (name, mobile_number, address, sale_type, outstanding_balance, is_active, created_at_utc)
            VALUES (@Name, @MobileNumber, @Address, @saleType, 0, TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                customer.Name,
                customer.MobileNumber,
                customer.Address,

                // Dapper writes an unmapped enum as its underlying int by default, which the
                // sale_type CHECK constraint rejects outright — the column is a string, like
                // every other enum column in this schema (see payment_method, sale_type on
                // invoices), so it is spelled out explicitly here, the same way.
                saleType = customer.SaleType.ToString(),
            });
    }

    public async Task UpdateAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // outstanding_balance is deliberately absent: it moves only inside invoice, payment and
        // sale-return transactions, never by editing a customer's contact details.
        await connection.ExecuteAsync(
            """
            UPDATE customers
            SET name = @Name,
                mobile_number = @MobileNumber,
                address = @Address,
                sale_type = @saleType,
                updated_at_utc = UTC_TIMESTAMP(6)
            WHERE id = @Id;
            """,
            new
            {
                customer.Id,
                customer.Name,
                customer.MobileNumber,
                customer.Address,
                saleType = customer.SaleType.ToString(),
            });
    }
}

/// <inheritdoc />
public sealed class InvoiceReadRepository : IInvoiceReadRepository
{
    private const string InvoiceColumns = """
        i.id               AS Id,
        i.invoice_number   AS InvoiceNumber,
        i.customer_id      AS CustomerId,
        i.invoice_date_utc AS InvoiceDateUtc,
        i.subtotal         AS Subtotal,
        i.order_discount   AS OrderDiscount,
        i.total            AS Total,
        i.amount_paid      AS AmountPaid,
        i.amount_remaining AS AmountRemaining,
        i.net_amount       AS NetAmount,
        i.payment_method   AS PaymentMethod,
        i.payment_proof_path AS PaymentProofPath,
        i.payment_account_number AS PaymentAccountNumber,
        i.payment_transaction_id AS PaymentTransactionId,
        i.user_id          AS UserId
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public InvoiceReadRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task SetPaymentProofPathAsync(
        long id,
        string paymentProofPath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE invoices SET payment_proof_path = @paymentProofPath WHERE id = @id;",
            new { id, paymentProofPath });
    }

    public async Task<InvoiceWithItems?> FindByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT {InvoiceColumns}, c.name AS CustomerName
             FROM invoices i
             LEFT JOIN customers c ON c.id = i.customer_id
             WHERE i.id = @id;

             SELECT id AS Id, invoice_id AS InvoiceId, product_id AS ProductId,
                    product_name AS ProductName, quantity AS Quantity,
                    unit_sale_price AS UnitSalePrice, line_discount AS LineDiscount,
                    unit_cost_price AS UnitCostPrice, line_total AS LineTotal,
                    returned_qty AS ReturnedQty
             FROM invoice_items
             WHERE invoice_id = @id
             ORDER BY id;
             """,
            new { id });

        var invoice = await reader.ReadSingleOrDefaultAsync<InvoiceWithCustomerName>();

        if (invoice is null)
        {
            return null;
        }

        var items = (await reader.ReadAsync<InvoiceItem>()).AsList();

        return new InvoiceWithItems(invoice.ToInvoice(), items, invoice.CustomerName);
    }

    public async Task<(IReadOnlyList<InvoiceListRow> Items, int TotalItems)> SearchAsync(
        long? customerId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = "WHERE 1 = 1";

        if (customerId is not null)
        {
            filter += " AND i.customer_id = @customerId";
        }

        if (fromUtc is not null)
        {
            filter += " AND i.invoice_date_utc >= @fromUtc";
        }

        if (toUtc is not null)
        {
            filter += " AND i.invoice_date_utc < @toUtc";
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await using var reader = await connection.QueryMultipleAsync(
            $"""
             SELECT i.id               AS Id,
                    i.invoice_number   AS InvoiceNumber,
                    i.invoice_date_utc AS InvoiceDateUtc,
                    i.customer_id      AS CustomerId,
                    -- Joined, not invented: a walk-in comes back NULL and the screen words it.
                    c.name             AS CustomerName,
                    i.sale_type        AS SaleType,
                    i.total            AS Total,
                    i.amount_paid      AS AmountPaid,
                    i.amount_remaining AS AmountRemaining,
                    i.net_amount       AS NetAmount,
                    i.payment_method   AS PaymentMethod
             FROM invoices i
             LEFT JOIN customers c ON c.id = i.customer_id
             {filter}
             ORDER BY i.invoice_date_utc DESC, i.id DESC
             LIMIT @limit OFFSET @offset;

             SELECT COUNT(*) FROM invoices i {filter};
             """,
            new { customerId, fromUtc, toUtc, limit = pageSize, offset = (page - 1) * pageSize });

        var items = (await reader.ReadAsync<InvoiceListRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    /// <summary>Flat row shape for the joined read; mapped to the entity plus the customer name.</summary>
    private sealed record InvoiceWithCustomerName
    {
        public long Id { get; init; }

        public string InvoiceNumber { get; init; } = string.Empty;

        public long? CustomerId { get; init; }

        public string? CustomerName { get; init; }

        public DateTime InvoiceDateUtc { get; init; }

        public decimal Subtotal { get; init; }

        public decimal OrderDiscount { get; init; }

        public decimal Total { get; init; }

        public decimal AmountPaid { get; init; }

        public decimal AmountRemaining { get; init; }

        public decimal NetAmount { get; init; }

        public PaymentMethod PaymentMethod { get; init; }

        public string? PaymentProofPath { get; init; }

        public string? PaymentAccountNumber { get; init; }

        public string? PaymentTransactionId { get; init; }

        public long UserId { get; init; }

        // Hand-written, so every field added to Invoice must be added HERE too — a new column
        // that reaches the SELECT but not this method is dropped in silence, which is exactly
        // how payment_proof_path first came back null on a proof that was stored correctly.
        public Invoice ToInvoice() => new()
        {
            Id = Id,
            InvoiceNumber = InvoiceNumber,
            CustomerId = CustomerId,
            InvoiceDateUtc = InvoiceDateUtc,
            Subtotal = Subtotal,
            OrderDiscount = OrderDiscount,
            Total = Total,
            AmountPaid = AmountPaid,
            AmountRemaining = AmountRemaining,
            NetAmount = NetAmount,
            PaymentMethod = PaymentMethod,
            PaymentProofPath = PaymentProofPath,
            PaymentAccountNumber = PaymentAccountNumber,
            PaymentTransactionId = PaymentTransactionId,
            UserId = UserId,
        };
    }
}
