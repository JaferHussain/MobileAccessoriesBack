namespace MoizPos.Domain.Errors;

/// <summary>
/// Error codes surfaced to API callers. These are part of the API contract
/// (see specs/001-pos-inventory-ledger/contracts/conventions.md) — renaming one is a
/// breaking change for the frontend.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string DiscountExceedsTotal = "DISCOUNT_EXCEEDS_TOTAL";
    public const string ReturnExceedsOriginal = "RETURN_EXCEEDS_ORIGINAL";
    public const string CustomerRequired = "CUSTOMER_REQUIRED";
    public const string CreditRequiresAdmin = "CREDIT_REQUIRES_ADMIN";
    public const string OverpaymentNotConfirmed = "OVERPAYMENT_NOT_CONFIRMED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string BusinessRuleViolation = "BUSINESS_RULE_VIOLATION";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>Base for every error the domain raises deliberately.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>
/// A sale leaving money outstanding was attempted by someone other than the shop owner
/// (FR-051, FR-052).
///
/// Thrown after the server has recomputed the totals and before anything has been written, so a
/// refusal leaves no invoice, no stock movement and no balance change (FR-055). Deliberately
/// keyed on the outstanding amount rather than the payment method: a sale labelled "Cash" whose
/// paid amount falls short is still goods leaving the shop against a debt.
/// </summary>
public sealed class CreditRequiresAdminException : DomainException
{
    public CreditRequiresAdminException(decimal amountRemaining)
        : base(
            ErrorCodes.CreditRequiresAdmin,
            $"Only the shop owner can approve udhaar. This sale leaves Rs {amountRemaining:N2} " +
            "unpaid — take the full amount, or ask the owner to complete the sale.")
    {
        AmountRemaining = amountRemaining;
    }

    public decimal AmountRemaining { get; }
}

/// <summary>
/// A product search made only of one-letter words (FR-079).
///
/// A plain <see cref="DomainException"/>, which the middleware maps to 400: the caller fixes it by
/// typing more. Raised by the service rather than a request validator because <c>search</c>
/// arrives on the query string, and FluentValidation in this codebase validates request bodies.
/// </summary>
public sealed class SearchTooShortException : DomainException
{
    public SearchTooShortException()
        : base(ErrorCodes.ValidationFailed, "Type at least 2 letters to search.")
    {
    }
}

/// <summary>A sale or purchase return would drive stock below zero (FR-006, FR-016).</summary>
public sealed class InsufficientStockException : DomainException
{
    public InsufficientStockException(string productName, int available, int requested)
        : base(
            ErrorCodes.InsufficientStock,
            $"Not enough stock for '{productName}'. Available: {available}, requested: {requested}.")
    {
        ProductName = productName;
        Available = available;
        Requested = requested;
    }

    public string ProductName { get; }

    public int Available { get; }

    public int Requested { get; }
}

/// <summary>A line or order discount exceeds the amount it applies to (spec edge case).</summary>
public sealed class DiscountExceedsTotalException : DomainException
{
    public DiscountExceedsTotalException(decimal discount, decimal applicableAmount)
        : base(
            ErrorCodes.DiscountExceedsTotal,
            $"Discount {discount:0.00} exceeds the amount it applies to ({applicableAmount:0.00}).")
    {
    }
}

/// <summary>More units returned than were originally sold or purchased (FR-026).</summary>
public sealed class ReturnExceedsOriginalException : DomainException
{
    public ReturnExceedsOriginalException(int alreadyReturned, int original, int requested)
        : base(
            ErrorCodes.ReturnExceedsOriginal,
            $"Cannot return {requested}: {original} were recorded and {alreadyReturned} already returned.")
    {
    }
}

/// <summary>An unpaid sale was submitted with no customer attached (FR-017).</summary>
public sealed class CustomerRequiredException : DomainException
{
    public CustomerRequiredException(decimal amountRemaining)
        : base(
            ErrorCodes.CustomerRequired,
            $"A customer is required when {amountRemaining:0.00} remains unpaid.")
    {
    }
}

/// <summary>
/// A payment exceeds the outstanding balance and the caller did not explicitly confirm it
/// (FR-009, FR-022). Balances must never go negative by accident.
/// </summary>
public sealed class OverpaymentNotConfirmedException : DomainException
{
    public OverpaymentNotConfirmedException(decimal amount, decimal outstanding)
        : base(
            ErrorCodes.OverpaymentNotConfirmed,
            $"Payment {amount:0.00} exceeds the outstanding {outstanding:0.00}. " +
            "Set confirmOverpayment to true to record it deliberately.")
    {
    }
}

/// <summary>The requested resource does not exist, is inactive, or the token is invalid.</summary>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string what, object key)
        : base(ErrorCodes.NotFound, $"{what} '{key}' was not found.")
    {
    }
}

/// <summary>A domain invariant was breached; the message explains which.</summary>
public sealed class BusinessRuleViolationException : DomainException
{
    public BusinessRuleViolationException(string message)
        : base(ErrorCodes.BusinessRuleViolation, message)
    {
    }
}

/// <summary>A row lock could not be acquired in time; the caller may safely retry.</summary>
public sealed class ConcurrencyConflictException : DomainException
{
    public ConcurrencyConflictException(string message)
        : base(ErrorCodes.ConcurrencyConflict, message)
    {
    }
}
