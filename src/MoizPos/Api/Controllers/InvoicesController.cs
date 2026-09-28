using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Storage;

namespace MoizPos.Api.Controllers;

public sealed record InvoiceLineRequest
{
    public long ProductId { get; init; }

    public int Quantity { get; init; }

    public decimal UnitSalePrice { get; init; }

    public decimal LineDiscount { get; init; }
}

public sealed record NewCustomerRequest
{
    public string Name { get; init; } = string.Empty;

    public string? MobileNumber { get; init; }
}

public sealed record CreateInvoiceApiRequest
{
    public long? CustomerId { get; init; }

    public NewCustomerRequest? NewCustomer { get; init; }

    public decimal OrderDiscount { get; init; }

    public decimal AmountPaid { get; init; }

    public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.Cash;

    /// <summary>The customer's account, where a non-cash payment came from. Optional (0021).</summary>
    public string? PaymentAccountNumber { get; init; }

    /// <summary>Their reference for that transfer. Optional.</summary>
    public string? PaymentTransactionId { get; init; }

    /// <summary>Counter sale or bulk sale. Retail unless the salesman says otherwise.</summary>
    public SaleType SaleType { get; init; } = SaleType.Retail;

    public IReadOnlyList<InvoiceLineRequest> Items { get; init; } = [];
}

public sealed class CreateInvoiceValidator : AbstractValidator<CreateInvoiceApiRequest>
{
    public CreateInvoiceValidator()
    {
        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("Add at least one item before saving the sale.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).GreaterThan(0).WithMessage("A product is required.");

            item.RuleFor(i => i.Quantity)
                .GreaterThan(0).WithMessage("Quantity must be greater than zero.");

            item.RuleFor(i => i.UnitSalePrice)
                .GreaterThanOrEqualTo(0).WithMessage("Unit price cannot be negative.");

            item.RuleFor(i => i.LineDiscount)
                .GreaterThanOrEqualTo(0).WithMessage("Line discount cannot be negative.");
        });

        RuleFor(x => x.OrderDiscount)
            .GreaterThanOrEqualTo(0).WithMessage("Order discount cannot be negative.");

        RuleFor(x => x.AmountPaid)
            .GreaterThanOrEqualTo(0).WithMessage("Amount paid cannot be negative.");

        RuleFor(x => x.NewCustomer!.Name)
            .NotEmpty().When(x => x.NewCustomer is not null)
            .WithMessage("A customer name is required.");

        // Bounded to the column, so an over-long reference is a 400 with a message rather than
        // a truncation or a database error at the counter. Whether a reference is ALLOWED at
        // all depends on the payment method, which InvoiceService decides — a validator that
        // knew that rule would be a second place to keep it in step.
        RuleFor(x => x.PaymentAccountNumber)
            .MaximumLength(50).WithMessage("The account number is too long.");

        RuleFor(x => x.PaymentTransactionId)
            .MaximumLength(64).WithMessage("The transaction reference is too long.");
    }
}

/// <summary>
/// Selling. Staff may create and read sales — that is their job — but the responses carry no
/// cost or profit data (FR-040).
/// </summary>
[ApiController]
[Route("api/invoices")]
[Authorize]
public sealed class InvoicesController : ControllerBase
{
    private readonly IInvoiceService _invoices;
    private readonly IInvoiceReadRepository _reads;

    public InvoicesController(IInvoiceService invoices, IInvoiceReadRepository reads)
    {
        _invoices = invoices;
        _reads = reads;
    }

    /// <summary>
    /// Records a sale. One transaction: lock the products, verify stock, price the sale
    /// server-side, write the invoice and lines with the cost snapshotted, decrement stock, and
    /// raise the customer's balance if anything is owed (FR-015).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateInvoiceApiRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _invoices.CreateAsync(
                new CreateInvoiceRequest
                {
                    CustomerId = request.CustomerId,
                    NewCustomer = request.NewCustomer is null
                        ? null
                        : new NewCustomer(request.NewCustomer.Name, request.NewCustomer.MobileNumber),
                    OrderDiscount = request.OrderDiscount,
                    AmountPaid = request.AmountPaid,
                    PaymentMethod = request.PaymentMethod,
                    PaymentAccountNumber = request.PaymentAccountNumber,
                    PaymentTransactionId = request.PaymentTransactionId,
                    SaleType = request.SaleType,
                    IdempotencyKey = idempotencyKey,
                    Items = request.Items.Select(i => new CreateInvoiceLine
                    {
                        ProductId = i.ProductId,
                        Quantity = i.Quantity,
                        UnitSalePrice = i.UnitSalePrice,
                        LineDiscount = i.LineDiscount,
                    }).ToList(),
                },
                CurrentUser.Id(User),
                CurrentUser.Role(User),
                cancellationToken);

            return CreatedAtAction(
                nameof(Get), new { id = result.InvoiceId }, ApiResponse<CreateInvoiceResult>.Ok(result));
        }
        catch (DuplicateInvoiceException duplicate)
        {
            // A double-tap or a retried request: hand back the sale already recorded rather than
            // creating a second one or reporting an error the shopkeeper cannot act on.
            var existing = await _reads.FindByIdAsync(duplicate.ExistingInvoiceId, cancellationToken);

            if (existing is null)
            {
                // The key matched an invoice we can no longer read — report the duplicate rather
                // than pretending the retry succeeded.
                throw new BusinessRuleViolationException(duplicate.Message);
            }

            return Ok(ApiResponse<InvoiceWithItems>.Ok(existing));
        }
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var invoice = await _reads.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Invoice", id);

        return Ok(ApiResponse<InvoiceWithItems>.Ok(invoice));
    }

    /// <summary>
    /// Attaches a screenshot backing a non-cash payment (FR-001).
    ///
    /// <para>A second call, after the sale exists, on purpose: the invoice id it is addressed to
    /// does not exist until the sale has been saved, and the sale must never wait on a picture
    /// (FR-002). Any signed-in user may attach one — the salesman is who takes the payment
    /// (FR-007).</para>
    /// </summary>
    [HttpPost("{id:long}/payment-proof")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> UploadPaymentProof(
        long id,
        IFormFile file,
        [FromServices] IImageStorageService images,
        CancellationToken cancellationToken)
    {
        var invoice = await _reads.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Invoice", id);

        // Cash is its own proof — the money is in the drawer. Accepting a picture here would
        // be evidence of nothing, and would invite "proof" on sales that never had a transfer.
        if (invoice.Invoice.PaymentMethod == PaymentMethod.Cash)
        {
            throw new BusinessRuleViolationException(
                "A Cash sale needs no payment proof — the money was taken at the counter.");
        }

        if (file is null || file.Length == 0)
        {
            throw new BusinessRuleViolationException("No payment proof was uploaded.");
        }

        await using var stream = file.OpenReadStream();

        var relativePath = await images.SavePaymentProofAsync(
            stream, file.ContentType, file.Length, cancellationToken);

        await _reads.SetPaymentProofPathAsync(id, relativePath, cancellationToken);

        // Only once the new one is safely stored and recorded: a correction that deleted first
        // and then failed would leave the invoice with no proof at all.
        if (!string.IsNullOrWhiteSpace(invoice.Invoice.PaymentProofPath))
        {
            images.DeletePaymentProof(invoice.Invoice.PaymentProofPath);
        }

        return Ok(ApiResponse<object>.Ok(new { invoiceId = id, paymentProofPath = relativePath }));
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] long? customerId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<InvoiceListRow>.Normalize(page, pageSize);

        var (items, total) = await _reads.SearchAsync(
            customerId, from, to, normalizedPage, normalizedSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<InvoiceListRow>>.Ok(
            new PagedResult<InvoiceListRow>(items, normalizedPage, normalizedSize, total)));
    }
}
