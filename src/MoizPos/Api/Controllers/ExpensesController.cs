using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Time;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Controllers;

public sealed record CreateExpenseRequest
{
    public long CategoryId { get; init; }

    public decimal Amount { get; init; }

    public DateTime ExpenseDate { get; init; }

    /// <summary>
    /// Where the money came from. Required: an expense with no source is invisible to the day's
    /// drawer count, so the drawer reads short by exactly the amount that legitimately left it.
    /// </summary>
    public PaymentSource? PaymentSource { get; init; }

    public string? Note { get; init; }
}

public sealed record CreateExpenseCategoryRequest
{
    public string Name { get; init; } = string.Empty;
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
        RuleFor(x => x.PaymentSource)
            .NotNull().WithMessage("Say whether this was paid from the till or the bank.")
            .IsInEnum().WithMessage("An expense is paid from either the till or the bank.");

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
    private readonly PeriodResolver _periods;

    public ExpensesController(IExpenseRepository expenses, PeriodResolver periods)
    {
        _expenses = expenses;
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
        var categories = await _expenses.ListCategoriesAsync(cancellationToken);

        if (categories.All(c => c.Id != request.CategoryId))
        {
            throw new NotFoundException("Expense category", request.CategoryId);
        }

        var id = await _expenses.CreateAsync(
            request.CategoryId,
            request.Amount,
            request.ExpenseDate,
            request.PaymentSource!.Value,
            request.Note,
            CurrentUser.Id(User),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<object>.Ok(new { id }));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await _expenses.DeleteAsync(id, cancellationToken);

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

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var categories = await _expenses.ListCategoriesAsync(cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<ExpenseCategory>>.Ok(categories));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateExpenseCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _expenses.CreateCategoryAsync(request.Name.Trim(), cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<object>.Ok(new { id }));
    }
}
