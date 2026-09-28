using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Services;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class DocumentTokenRepository : IDocumentTokenRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DocumentTokenRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<long> CreateAsync(
        string tokenHash,
        DocumentType documentType,
        long referenceId,
        DateTime expiresAtUtc,
        long createdByUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO document_tokens
                (token_hash, document_type, reference_id, expires_at_utc,
                 created_by_user_id, created_at_utc)
            VALUES
                (@tokenHash, @documentType, @referenceId, @expiresAtUtc,
                 @createdByUserId, @nowUtc);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                tokenHash,
                documentType = documentType.ToString(),
                referenceId,
                expiresAtUtc,
                createdByUserId,
                nowUtc,
            });
    }

    /// <summary>The columns the owner reads. token_hash is deliberately absent.</summary>
    private const string SummaryColumns = """
        t.id                AS Id,
        t.created_at_utc    AS CreatedAtUtc,
        u.full_name         AS CreatedByUserName,
        t.expires_at_utc    AS ExpiresAtUtc,
        t.revoked_at_utc    AS RevokedAtUtc,
        t.last_accessed_utc AS LastAccessedAtUtc,
        t.access_count      AS AccessCount,
        (t.revoked_at_utc IS NULL AND t.expires_at_utc > @nowUtc) AS IsUsable
        """;

    public async Task<IReadOnlyList<ShareLinkSummary>> ListForDocumentAsync(
        DocumentType documentType,
        long referenceId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ShareLinkSummary>(
            $"""
             SELECT {SummaryColumns}
             FROM document_tokens t
             JOIN users u ON u.id = t.created_by_user_id
             WHERE t.document_type = @documentType AND t.reference_id = @referenceId
             ORDER BY t.id DESC;
             """,
            new { documentType = documentType.ToString(), referenceId, nowUtc });

        return rows.AsList();
    }

    public async Task<ShareLinkSummary?> FindByIdAsync(
        long id,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ShareLinkSummary>(
            $"""
             SELECT {SummaryColumns}
             FROM document_tokens t
             JOIN users u ON u.id = t.created_by_user_id
             WHERE t.id = @id
             LIMIT 1;
             """,
            new { id, nowUtc });
    }

    public async Task<StoredDocumentToken?> FindAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<StoredDocumentToken>(
            """
            SELECT id AS Id, document_type AS DocumentType, reference_id AS ReferenceId,
                   expires_at_utc AS ExpiresAtUtc, revoked_at_utc AS RevokedAtUtc
            FROM document_tokens
            WHERE token_hash = @tokenHash
            LIMIT 1;
            """,
            new { tokenHash });
    }

    public async Task RecordAccessAsync(
        long id,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Every retrieval is counted and timestamped — the containment the plan promised for the
        // one unauthenticated endpoint.
        await connection.ExecuteAsync(
            """
            UPDATE document_tokens
            SET access_count = access_count + 1, last_accessed_utc = @nowUtc
            WHERE id = @id;
            """,
            new { id, nowUtc });
    }

    public async Task RevokeAsync(
        long id,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE document_tokens SET revoked_at_utc = @nowUtc WHERE id = @id AND revoked_at_utc IS NULL;",
            new { id, nowUtc });
    }
}

/// <inheritdoc />
public sealed class CustomerPaymentReadRepository : ICustomerPaymentReadRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public CustomerPaymentReadRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<CustomerPayment?> FindByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<CustomerPayment>(
            """
            SELECT id AS Id, customer_id AS CustomerId, receipt_number AS ReceiptNumber,
                   amount AS Amount, payment_method AS PaymentMethod,
                   payment_date_utc AS PaymentDateUtc, is_overpayment AS IsOverpayment,
                   note AS Note, user_id AS UserId
            FROM customer_payments
            WHERE id = @id
            LIMIT 1;
            """,
            new { id });
    }

    public async Task<decimal?> BalanceAfterPaymentAsync(
        long paymentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // The entry this payment wrote, and the balance it left behind. Read from the ledger
        // rather than from customers.outstanding_balance, which has already moved on if the
        // customer has bought again since.
        return await connection.ExecuteScalarAsync<decimal?>(
            """
            SELECT balance_after
            FROM ledger_entries
            WHERE entry_type = 'Payment' AND reference_id = @paymentId
            ORDER BY id
            LIMIT 1;
            """,
            new { paymentId });
    }
}

/// <summary>Adapts the Infrastructure renderer to the Application port.</summary>
public sealed class PdfRendererAdapter : IPdfRendererPort
{
    private readonly Documents.IPdfRenderer _renderer;

    public PdfRendererAdapter(Documents.IPdfRenderer renderer) => _renderer = renderer;

    public byte[] RenderInvoice(Application.Documents.InvoiceDocument document) =>
        _renderer.RenderInvoice(document);

    public byte[] RenderReceipt(Application.Documents.ReceiptDocument document) =>
        _renderer.RenderReceipt(document);
}
