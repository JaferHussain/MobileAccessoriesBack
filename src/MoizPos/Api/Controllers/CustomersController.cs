using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Application.Abstractions;
using MoizPos.Api.Authorization;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Contracts.Customers;
using MoizPos.Domain.Entities;
using MoizPos.Application.Services;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Controllers;

public sealed record CustomerUpsertRequest
{
    public string Name { get; init; } = string.Empty;

    public string? MobileNumber { get; init; }

    public string? Address { get; init; }

    /// <summary>
    /// A standing label, Admin-only to set (FR-105). Null lets a Staff quick-create (FR-017)
    /// omit it entirely rather than send a value the server will ignore.
    /// </summary>
    public SaleType? SaleType { get; init; }
}

public sealed record ReceiveCustomerPaymentRequest
{
    public decimal Amount { get; init; }

    public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.Cash;

    public string? Note { get; init; }

    /// <summary>Must be true to accept more than the customer owes (FR-022).</summary>
    public bool ConfirmOverpayment { get; init; }
}

public sealed class ReceiveCustomerPaymentValidator : AbstractValidator<ReceiveCustomerPaymentRequest>
{
    public ReceiveCustomerPaymentValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Payment amount must be greater than zero.");

        RuleFor(x => x.Note)
            .MaximumLength(255).WithMessage("Note cannot exceed 255 characters.");
    }
}

public sealed class CustomerUpsertValidator : AbstractValidator<CustomerUpsertRequest>
{
    public CustomerUpsertValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Customer name is required.")
            .MaximumLength(150).WithMessage("Customer name cannot exceed 150 characters.");

        RuleFor(x => x.MobileNumber)
            .MaximumLength(20).WithMessage("Mobile number cannot exceed 20 characters.");

        RuleFor(x => x.Address)
            .MaximumLength(255).WithMessage("Address cannot exceed 255 characters.");
    }
}

/// <summary>
/// Customers and their udhaar balances.
///
/// Staff need these to sell on credit and take payments, so this controller is not Admin-only.
/// Customer balances are the shop's receivables, not its margins — nothing here reveals cost or
/// profit (FR-040).
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize]
public sealed class CustomersController : ControllerBase
{
    private readonly ICustomerRepository _customers;
    private readonly ICustomerLedgerService _ledger;

    public CustomersController(ICustomerRepository customers, ICustomerLedgerService ledger)
    {
        _customers = customers;
        _ledger = ledger;
    }

    /// <summary>
    /// The customer's udhaar register: every bill and payment in date order with the balance
    /// that resulted (FR-020).
    /// </summary>
    [HttpGet("{id:long}/ledger")]
    public async Task<IActionResult> Ledger(
        long id,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        _ = await _customers.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Customer", id);

        var entries = await _ledger.LedgerAsync(id, from, to, page, pageSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<LedgerEntryRow>>.Ok(entries));
    }

    /// <summary>Total purchased, total paid and total outstanding (FR-023).</summary>
    [HttpGet("{id:long}/summary")]
    public async Task<IActionResult> Summary(long id, CancellationToken cancellationToken)
    {
        _ = await _customers.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Customer", id);

        var summary = await _ledger.SummaryAsync(id, cancellationToken);

        return Ok(ApiResponse<CustomerSummaryRow>.Ok(summary));
    }

    /// <summary>
    /// Records money received against the customer's balance and produces a receipt (FR-021).
    /// </summary>
    [HttpPost("{id:long}/payments")]
    public async Task<IActionResult> ReceivePayment(
        long id,
        [FromBody] ReceiveCustomerPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _ledger.ReceivePaymentAsync(
            new ReceivePaymentRequest
            {
                CustomerId = id,
                Amount = request.Amount,
                PaymentMethod = request.PaymentMethod,
                Note = request.Note,
                ConfirmOverpayment = request.ConfirmOverpayment,
            },
            CurrentUser.Id(User),
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created, ApiResponse<ReceivePaymentResult>.Ok(result));
    }

    /// <summary>
    /// Records what a customer already owed before this software was in use, carried over from
    /// the shop's paper register (FR-065).
    ///
    /// <para>Sending this a second time <b>corrects</b> the figure — the customer's balance moves
    /// by the difference, never by the full amount again (FR-070). A reason is required for a
    /// correction (FR-071).</para>
    ///
    /// <para>Admin only: this is a statement about money owed to the shop, not a sale
    /// (FR-068).</para>
    /// </summary>
    [HttpPut("{id:long}/opening-balance")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> SetOpeningBalance(
        long id,
        [FromBody] SetOpeningBalanceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _ledger.SetOpeningBalanceAsync(
            id, request, CurrentUser.Id(User), cancellationToken);

        return Ok(ApiResponse<OpeningBalanceResult>.Ok(result));
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] bool withBalanceOnly = false,
        [FromQuery] SaleType? saleType = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<Customer>.Normalize(page, pageSize);

        var (items, total) = await _customers.SearchAsync(
            search, withBalanceOnly, saleType, normalizedPage, normalizedSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<Customer>>.Ok(
            new PagedResult<Customer>(items, normalizedPage, normalizedSize, total)));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var customer = await _customers.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Customer", id);

        return Ok(ApiResponse<Customer>.Ok(customer));
    }

    /// <summary>Creates a customer. Also used for inline quick-create during a sale (FR-017).</summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CustomerUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _customers.CreateAsync(
            new Customer
            {
                Name = request.Name.Trim(),
                MobileNumber = string.IsNullOrWhiteSpace(request.MobileNumber)
                    ? null
                    : request.MobileNumber.Trim(),
                Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),

                // Admin-only (FR-105). A Staff request's SaleType is silently ignored rather
                // than rejected — quick-creating a customer mid-sale (FR-017) must keep working
                // exactly as it did before this field existed.
                SaleType = CurrentUser.Role(User) == UserRole.Admin && request.SaleType is not null
                    ? request.SaleType.Value
                    : Domain.Enums.SaleType.Retail,
            },
            cancellationToken);

        var created = await _customers.FindByIdAsync(id, cancellationToken);

        return CreatedAtAction(nameof(Get), new { id }, ApiResponse<Customer>.Ok(created!));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] CustomerUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await _customers.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Customer", id);

        existing.Name = request.Name.Trim();
        existing.MobileNumber = string.IsNullOrWhiteSpace(request.MobileNumber)
            ? null
            : request.MobileNumber.Trim();
        existing.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();

        if (CurrentUser.Role(User) == UserRole.Admin && request.SaleType is not null)
        {
            existing.SaleType = request.SaleType.Value;
        }

        // OutstandingBalance is untouched: it moves only through invoice, payment and
        // sale-return transactions, never by editing contact details or the sale-type label.
        await _customers.UpdateAsync(existing, cancellationToken);

        return Ok(ApiResponse<Customer>.Ok((await _customers.FindByIdAsync(id, cancellationToken))!));
    }
}
