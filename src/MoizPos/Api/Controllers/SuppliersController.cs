using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Controllers;

public sealed record SupplierUpsertRequest
{
    public string Name { get; init; } = string.Empty;

    public string? ContactNumber { get; init; }

    public string? Address { get; init; }

    /// <summary>Stored exactly as typed — see the validator for why it is not pattern-checked.</summary>
    public string? Cnic { get; init; }

    public string? Email { get; init; }

    public string? BankName { get; init; }

    public string? BankAccountTitle { get; init; }

    public string? BankAccountNumber { get; init; }

    public string? Notes { get; init; }
}

public sealed record SupplierPaymentRequest
{
    public decimal Amount { get; init; }

    /// <summary>
    /// Required, with no default. Defaulting to Cash is what sent every bank transfer to a
    /// supplier into the evening's drawer count as a short: day close subtracts Cash payments only.
    /// </summary>
    public PaymentMethod? PaymentMethod { get; init; }

    /// <summary>A cheque or transaction number. Shown on the supplier's ledger.</summary>
    public string? Note { get; init; }

    /// <summary>Which shop account paid. Optional; only for a non-cash payment.</summary>
    public long? ShopAccountId { get; init; }

    public bool ConfirmOverpayment { get; init; }
}

public sealed class SupplierUpsertValidator : AbstractValidator<SupplierUpsertRequest>
{
    public SupplierUpsertValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Supplier name is required.")
            .MaximumLength(150).WithMessage("Supplier name cannot exceed 150 characters.");

        RuleFor(x => x.ContactNumber)
            .MaximumLength(20).WithMessage("Contact number cannot exceed 20 characters.");

        RuleFor(x => x.Address)
            .MaximumLength(255).WithMessage("Address cannot exceed 255 characters.");

        // Length only, no format. A CNIC or an IBAN is transcribed from the supplier's own
        // paperwork; refusing one because it is punctuated differently would make the software
        // argue with the document it is copying. Length is checked because the column has one,
        // and silently truncating would store something the owner never typed.
        RuleFor(x => x.Cnic)
            .MaximumLength(30).WithMessage("CNIC cannot exceed 30 characters.");

        RuleFor(x => x.Email)
            .MaximumLength(255).WithMessage("Email cannot exceed 255 characters.");

        RuleFor(x => x.BankName)
            .MaximumLength(150).WithMessage("Bank name cannot exceed 150 characters.");

        RuleFor(x => x.BankAccountTitle)
            .MaximumLength(150).WithMessage("Account title cannot exceed 150 characters.");

        RuleFor(x => x.BankAccountNumber)
            .MaximumLength(50).WithMessage("Account number cannot exceed 50 characters.");

        RuleFor(x => x.Notes)
            .MaximumLength(500).WithMessage("Notes cannot exceed 500 characters.");
    }
}

public sealed class SupplierPaymentValidator : AbstractValidator<SupplierPaymentRequest>
{
    public SupplierPaymentValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Payment amount must be greater than zero.");

        RuleFor(x => x.PaymentMethod)
            .NotNull().WithMessage("Say how the supplier was paid — cash, bank transfer, JazzCash…")
            .Must(method => method is not (PaymentMethod.Credit or PaymentMethod.Partial))
            .WithMessage("A payment to a supplier is made in cash or by transfer. Credit and Partial describe an unpaid sale.");

        RuleFor(x => x.Note)
            .MaximumLength(255).WithMessage("Note cannot exceed 255 characters.");

        RuleFor(x => x.ShopAccountId)
            .Null().When(x => x.PaymentMethod == Domain.Enums.PaymentMethod.Cash)
            .WithMessage("A cash payment did not go through a shop account.");
    }
}

/// <summary>
/// Suppliers and what the shop owes them.
///
/// Admin-only in full: payables are financial data a salesman must never see (FR-040).
/// </summary>
[ApiController]
[Route("api/suppliers")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class SuppliersController : ControllerBase
{
    private readonly ISupplierRepository _suppliers;
    private readonly IPurchaseService _purchases;
    private readonly ISupplierLedgerService _ledger;
    private readonly IShopAccountRepository _accounts;

    public SuppliersController(
        ISupplierRepository suppliers,
        IPurchaseService purchases,
        ISupplierLedgerService ledger,
        IShopAccountRepository accounts)
    {
        _suppliers = suppliers;
        _purchases = purchases;
        _ledger = ledger;
        _accounts = accounts;
    }

    /// <summary>
    /// The supplier's account: every purchase, return and payment in date order with what was
    /// owed after each. <paramref name="from"/> and <paramref name="to"/> are shop-local days,
    /// both inclusive; the result opens on what was already owed before <paramref name="from"/>.
    /// </summary>
    [HttpGet("{id:long}/ledger")]
    public async Task<IActionResult> Ledger(
        long id,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        var ledger = await _ledger.LedgerAsync(id, from, to, cancellationToken);

        return Ok(ApiResponse<SupplierLedgerResult>.Ok(ledger));
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<Supplier>.Normalize(page, pageSize);

        var (items, total) = await _suppliers.SearchAsync(
            search, normalizedPage, normalizedSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<Supplier>>.Ok(
            new PagedResult<Supplier>(items, normalizedPage, normalizedSize, total)));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var supplier = await _suppliers.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Supplier", id);

        return Ok(ApiResponse<Supplier>.Ok(supplier));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] SupplierUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _suppliers.CreateAsync(
            new Supplier
            {
                Name = request.Name.Trim(),
                ContactNumber = request.ContactNumber?.Trim(),
                Address = request.Address?.Trim(),
                Cnic = request.Cnic?.Trim(),
                Email = request.Email?.Trim(),
                BankName = request.BankName?.Trim(),
                BankAccountTitle = request.BankAccountTitle?.Trim(),
                BankAccountNumber = request.BankAccountNumber?.Trim(),
                Notes = request.Notes?.Trim(),
            },
            cancellationToken);

        var created = await _suppliers.FindByIdAsync(id, cancellationToken);

        return CreatedAtAction(nameof(Get), new { id }, ApiResponse<Supplier>.Ok(created!));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] SupplierUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await _suppliers.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Supplier", id);

        existing.Name = request.Name.Trim();
        existing.ContactNumber = request.ContactNumber?.Trim();
        existing.Address = request.Address?.Trim();
        existing.Cnic = request.Cnic?.Trim();
        existing.Email = request.Email?.Trim();
        existing.BankName = request.BankName?.Trim();
        existing.BankAccountTitle = request.BankAccountTitle?.Trim();
        existing.BankAccountNumber = request.BankAccountNumber?.Trim();
        existing.Notes = request.Notes?.Trim();

        // PayableBalance is untouched on purpose: it moves only through purchase, payment and
        // purchase-return transactions, never by editing contact details.
        await _suppliers.UpdateAsync(existing, cancellationToken);

        return Ok(ApiResponse<Supplier>.Ok((await _suppliers.FindByIdAsync(id, cancellationToken))!));
    }

    /// <summary>Records a payment to a supplier, reducing the payable (FR-009).</summary>
    [HttpPost("{id:long}/payments")]
    public async Task<IActionResult> RecordPayment(
        long id,
        [FromBody] SupplierPaymentRequest request,
        CancellationToken cancellationToken)
    {
        // The account must be able to carry the method — a bank transfer did not leave a JazzCash wallet.
        await ShopAccountCheck.EnsureFitsAsync(_accounts, request.ShopAccountId, request.PaymentMethod!.Value, cancellationToken);

        var result = await _purchases.RecordSupplierPaymentAsync(
            new RecordSupplierPaymentRequest
            {
                SupplierId = id,
                Amount = request.Amount,
                // Never null here: the validator refuses a payment that does not say how.
                PaymentMethod = request.PaymentMethod!.Value,
                Note = request.Note,
                ShopAccountId = request.ShopAccountId,
                ConfirmOverpayment = request.ConfirmOverpayment,
            },
            CurrentUser.Id(User),
            cancellationToken);

        return Ok(ApiResponse<object>.Ok(
            new { supplierId = id, paymentId = result.PaymentId, payableBalance = result.NewPayable }));
    }
}
