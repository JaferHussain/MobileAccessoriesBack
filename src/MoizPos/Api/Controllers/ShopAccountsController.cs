using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoizPos.Api.Authorization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Contracts.Common;
using MoizPos.Domain.Enums;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Controllers;

public sealed record ShopAccountRequest
{
    public string Name { get; init; } = string.Empty;

    public ShopAccountType? AccountType { get; init; }

    public string? AccountNumber { get; init; }

    public string? AccountTitle { get; init; }
}

public sealed class ShopAccountRequestValidator : AbstractValidator<ShopAccountRequest>
{
    public ShopAccountRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Give the account a name, e.g. \"HBL Current\".")
            .MaximumLength(80);

        RuleFor(x => x.AccountType)
            .NotNull().WithMessage("Say whether this is a bank, JazzCash or EasyPaisa account.")
            .IsInEnum();

        RuleFor(x => x.AccountNumber).MaximumLength(50);
        RuleFor(x => x.AccountTitle).MaximumLength(100);
    }
}

/// <summary>
/// The shop's own bank and wallet accounts, registered once so every non-cash payment the shop
/// makes can say which account it left. Admin only: these are the owner's accounts. Nothing is
/// hard-deleted — a retired account stays on the payments that name it.
/// </summary>
[ApiController]
[Route("api/shop-accounts")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class ShopAccountsController : ControllerBase
{
    private readonly IShopAccountRepository _accounts;

    public ShopAccountsController(IShopAccountRepository accounts) => _accounts = accounts;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ShopAccount>>.Ok(await _accounts.ListAsync(includeInactive, cancellationToken)));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ShopAccountRequest request, CancellationToken cancellationToken)
    {
        var input = await InputAsync(request, exceptId: null, cancellationToken);
        var id = await _accounts.CreateAsync(input, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ApiResponse<ShopAccount>.Ok((await _accounts.FindAsync(id, cancellationToken))!));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ShopAccountRequest request, CancellationToken cancellationToken)
    {
        _ = await _accounts.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Shop account", id);

        await _accounts.UpdateAsync(id, await InputAsync(request, id, cancellationToken), cancellationToken);

        return Ok(ApiResponse<ShopAccount>.Ok((await _accounts.FindAsync(id, cancellationToken))!));
    }

    /// <summary>Retires the account: no longer offered, never deleted.</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Retire(long id, CancellationToken cancellationToken)
    {
        _ = await _accounts.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Shop account", id);

        await _accounts.SetActiveAsync(id, isActive: false, cancellationToken);

        return NoContent();
    }

    [HttpPost("{id:long}/reactivate")]
    public async Task<IActionResult> Reactivate(long id, CancellationToken cancellationToken)
    {
        _ = await _accounts.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Shop account", id);

        await _accounts.SetActiveAsync(id, isActive: true, cancellationToken);

        return Ok(ApiResponse<ShopAccount>.Ok((await _accounts.FindAsync(id, cancellationToken))!));
    }

    private async Task<ShopAccountInput> InputAsync(ShopAccountRequest request, long? exceptId, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();

        if (await _accounts.NameTakenAsync(name, exceptId, cancellationToken))
        {
            throw new BusinessRuleViolationException($"An account called \"{name}\" already exists.");
        }

        return new ShopAccountInput(
            name,
            request.AccountType!.Value,
            Trimmed(request.AccountNumber),
            Trimmed(request.AccountTitle));
    }

    /// <summary>Blank is "not given", stored as NULL — never an empty string.</summary>
    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Checks the account a payment names: it must exist, still be in use, and be able to carry that
/// method. Shared by expenses and supplier payments so the rule lives in one place.
/// </summary>
public static class ShopAccountCheck
{
    public static async Task EnsureFitsAsync(
        IShopAccountRepository accounts, long? shopAccountId, PaymentMethod method, CancellationToken cancellationToken)
    {
        if (shopAccountId is not { } id)
        {
            return;
        }

        var account = await accounts.FindAsync(id, cancellationToken)
            ?? throw new BusinessRuleViolationException("That shop account does not exist.");

        if (!account.IsActive)
        {
            throw new BusinessRuleViolationException(
                $"{account.Name} is no longer in use. Choose another account, or bring it back under Shop accounts.");
        }

        if (!ShopAccountRules.Accepts(account.AccountType, method))
        {
            throw new BusinessRuleViolationException(ShopAccountRules.MismatchMessage(account.Name, method));
        }
    }
}
