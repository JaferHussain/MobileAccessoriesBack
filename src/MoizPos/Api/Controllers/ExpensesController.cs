using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Storage;

namespace MoizPos.Api.Controllers;

public sealed record CreateExpenseRequest
{
    public long CategoryId { get; init; }

    public decimal Amount { get; init; }

    public DateTime ExpenseDate { get; init; }

    /// <summary>
    /// How it was paid — the one question the form asks: Cash (from the till), bank transfer,
    /// JazzCash, EasyPaisa or Raast. Cash means Till and anything else means Bank, so day close
    /// reads exactly what it always read (<see cref="ResolvedSource"/>).
    /// </summary>
    public PaymentMethod? PaymentMethod { get; init; }

    /// <summary>
    /// The older way of saying where the money came from, still accepted from callers that send
    /// it. When both are given they must agree: Till is cash, Bank is a transfer.
    /// </summary>
    public PaymentSource? PaymentSource { get; init; }

    /// <summary>Which shop account paid. Optional; only for a non-cash payment.</summary>
    public long? ShopAccountId { get; init; }

    /// <summary>The bank's or app's reference. Optional; only for a non-cash payment.</summary>
    public string? TransactionId { get; init; }

    public string? Note { get; init; }

    /// <summary>Paid in notes from the drawer.</summary>
    public bool IsCash =>
        PaymentMethod == Domain.Enums.PaymentMethod.Cash
        || (PaymentMethod is null && PaymentSource == Domain.Enums.PaymentSource.Till);

    /// <summary>What day close reads: Till for cash, Bank for everything else.</summary>
    public PaymentSource? ResolvedSource => PaymentMethod switch
    {
        Domain.Enums.PaymentMethod.Cash => Domain.Enums.PaymentSource.Till,
        null => PaymentSource,
        _ => Domain.Enums.PaymentSource.Bank,
    };

    /// <summary>The transfer method stored on the expense — never Cash, which the column does not hold.</summary>
    public PaymentMethod? StoredMethod => IsCash ? null : PaymentMethod;
}

public sealed record CreateExpenseCategoryRequest
{
    public string Name { get; init; } = string.Empty;
}

public sealed record RenameExpenseCategoryRequest
{
    public string Name { get; init; } = string.Empty;
}

public sealed class RenameExpenseCategoryValidator : AbstractValidator<RenameExpenseCategoryRequest>
{
    public RenameExpenseCategoryValidator() =>
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Category name is required.")
            .MaximumLength(80).WithMessage("Category name cannot exceed 80 characters.");
}

public sealed class CreateExpenseValidator : AbstractValidator<CreateExpenseRequest>
{
    public CreateExpenseValidator()
    {
        RuleFor(x => x.CategoryId).GreaterThan(0).WithMessage("A category is required.");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Expense amount must be greater than zero.");

        RuleFor(x => x.ExpenseDate)
            .NotEmpty().WithMessage("An expense date is required.");

        // Enforced here, not only by the dropdown. A required field on a form is a convenience
        // for whoever uses the form; it is not a rule, because nothing makes a caller use it.
        RuleFor(x => x)
            .Must(x => x.PaymentMethod is not null || x.PaymentSource is not null)
            .WithName("PaymentMethod")
            .WithMessage("Say how this was paid — cash from the till, bank transfer, JazzCash, EasyPaisa or Raast.");

        RuleFor(x => x.PaymentSource).IsInEnum().WithMessage("An expense is paid from either the till or the bank.");

        RuleFor(x => x.PaymentMethod)
            .Must(method => method is not (Domain.Enums.PaymentMethod.Credit or Domain.Enums.PaymentMethod.Partial))
            .WithMessage("An expense is paid — in cash or by transfer. Credit and Partial describe an unpaid sale.");

        RuleFor(x => x.PaymentMethod)
            .Must(method => method is null or Domain.Enums.PaymentMethod.Cash)
            .When(x => x.PaymentSource == Domain.Enums.PaymentSource.Till)
            .WithMessage("An expense paid from the till was paid in cash — it has no transfer method.");

        RuleFor(x => x.PaymentMethod)
            .Must(method => method != Domain.Enums.PaymentMethod.Cash)
            .When(x => x.PaymentSource == Domain.Enums.PaymentSource.Bank)
            .WithMessage("Cash comes from the till, not the bank.");

        // Cash left the drawer and no account; there is no reference to give.
        RuleFor(x => x.ShopAccountId)
            .Null().When(x => x.IsCash)
            .WithMessage("A cash expense did not go through a shop account.");

        RuleFor(x => x.TransactionId)
            .Null().When(x => x.IsCash)
            .WithMessage("A cash expense has no transaction ID.")
            .MaximumLength(50);

        RuleFor(x => x.Note).MaximumLength(255);
    }
}

public sealed class CreateExpenseCategoryValidator : AbstractValidator<CreateExpenseCategoryRequest>
{
    public CreateExpenseCategoryValidator() =>
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Category name is required.")
            .MaximumLength(80).WithMessage("Category name cannot exceed 80 characters.");
}

/// <summary>
/// Expenses. Admin-only: rent, salaries and the rest are exactly the figures that would let
/// someone work out the shop's margins (FR-040).
/// </summary>
[ApiController]
[Route("api/expenses")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class ExpensesController : ControllerBase
{
    private readonly IExpenseRepository _expenses;
    private readonly IShopAccountRepository _accounts;
    private readonly PeriodResolver _periods;

    public ExpensesController(IExpenseRepository expenses, IShopAccountRepository accounts, PeriodResolver periods)
    {
        _expenses = expenses;
        _accounts = accounts;
        _periods = periods;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] long? categoryId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedSize) = PagedResult<ExpenseRow>.Normalize(page, pageSize);

        var range = from is not null && to is not null
            ? _periods.ResolveLocalDateRange(from.Value, to.Value)
            : (DateRangeUtc?)null;

        var (items, total) = await _expenses.SearchAsync(
            categoryId, range, normalizedPage, normalizedSize, cancellationToken);

        return Ok(ApiResponse<PagedResult<ExpenseRow>>.Ok(
            new PagedResult<ExpenseRow>(items, normalizedPage, normalizedSize, total)));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateExpenseRequest request,
        CancellationToken cancellationToken)
    {
        // Active categories only: a hidden one keeps its old expenses but takes no new ones.
        var categories = await _expenses.ListCategoriesAsync(cancellationToken: cancellationToken);

        if (categories.All(c => c.Id != request.CategoryId))
        {
            throw new NotFoundException("Expense category", request.CategoryId);
        }

        // The account must be able to carry the method — a JazzCash payment did not leave a bank.
        if (request.StoredMethod is { } method)
        {
            await ShopAccountCheck.EnsureFitsAsync(_accounts, request.ShopAccountId, method, cancellationToken);
        }

        var id = await _expenses.CreateAsync(
            request.CategoryId,
            request.Amount,
            request.ExpenseDate,
            // Never null here: the validator refuses an expense that does not say how it was paid.
            request.ResolvedSource!.Value,
            request.StoredMethod,
            request.IsCash ? null : request.ShopAccountId,
            request.IsCash || string.IsNullOrWhiteSpace(request.TransactionId) ? null : request.TransactionId.Trim(),
            request.Note,
            CurrentUser.Id(User),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<object>.Ok(new { id }));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(
        long id,
        [FromServices] ITransactionProofRepository proofs,
        [FromServices] IImageStorageService images,
        CancellationToken cancellationToken)
    {
        // Read before the row goes: a deleted expense must take its proof with it, or the
        // screenshot sits on disk with nothing pointing at it and nothing ever deleting it.
        var proofPath = (await proofs.FindAsync(ProofKind.Expense, id, cancellationToken))?.ProofPath;

        await _expenses.DeleteAsync(id, cancellationToken);

        if (!string.IsNullOrWhiteSpace(proofPath))
        {
            images.DeletePaymentProof(proofPath);
        }

        return NoContent();
    }
}

/// <summary>The expense category list, extensible by the owner (FR-029).</summary>
[ApiController]
[Route("api/expense-categories")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class ExpenseCategoriesController : ControllerBase
{
    private readonly IExpenseRepository _expenses;

    public ExpenseCategoriesController(IExpenseRepository expenses) => _expenses = expenses;

    /// <summary>
    /// The categories offered on the expense form. <paramref name="includeInactive"/> adds the
    /// hidden ones, for the owner's Manage categories panel.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken cancellationToken)
    {
        var categories = await _expenses.ListCategoriesAsync(includeInactive, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<ExpenseCategory>>.Ok(categories));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateExpenseCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        await EnsureNameFreeAsync(name, exceptId: null, cancellationToken);

        var id = await _expenses.CreateCategoryAsync(name, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<object>.Ok(new { id }));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Rename(
        long id, [FromBody] RenameExpenseCategoryRequest request, CancellationToken cancellationToken)
    {
        await EnsureExistsAsync(id, cancellationToken);

        var name = request.Name.Trim();
        await EnsureNameFreeAsync(name, id, cancellationToken);
        await _expenses.RenameCategoryAsync(id, name, cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { id, name }));
    }

    /// <summary>
    /// Hides a category from the form. Never deletes it: every expense filed under it keeps its
    /// label, and it can be brought back.
    /// </summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Hide(long id, CancellationToken cancellationToken)
    {
        await EnsureExistsAsync(id, cancellationToken);
        await _expenses.SetCategoryActiveAsync(id, isActive: false, cancellationToken);

        return NoContent();
    }

    [HttpPost("{id:long}/reactivate")]
    public async Task<IActionResult> Reactivate(long id, CancellationToken cancellationToken)
    {
        await EnsureExistsAsync(id, cancellationToken);
        await _expenses.SetCategoryActiveAsync(id, isActive: true, cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { id }));
    }

    private async Task EnsureExistsAsync(long id, CancellationToken cancellationToken)
    {
        var all = await _expenses.ListCategoriesAsync(includeInactive: true, cancellationToken);

        if (all.All(category => category.Id != id))
        {
            throw new NotFoundException("Expense category", id);
        }
    }

    private async Task EnsureNameFreeAsync(string name, long? exceptId, CancellationToken cancellationToken)
    {
        if (await _expenses.CategoryNameTakenAsync(name, exceptId, cancellationToken))
        {
            throw new BusinessRuleViolationException($"There is already a category called \"{name}\".");
        }
    }
}
