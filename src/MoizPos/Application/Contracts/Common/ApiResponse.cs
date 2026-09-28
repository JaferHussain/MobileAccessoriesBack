namespace MoizPos.Application.Contracts.Common;

/// <summary>One field-level reason a request was rejected.</summary>
public sealed record ErrorDetail(string Field, string Message);

/// <summary>
/// The error half of the envelope. <see cref="Code"/> is part of the API contract — the frontend
/// switches on it — so renaming one is a breaking change
/// (see specs/001-pos-inventory-ledger/contracts/conventions.md).
/// </summary>
public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyList<ErrorDetail>? Details = null,
    string? TraceId = null);

/// <summary>
/// Every response, success or failure, uses this one shape so the client never has to guess
/// (FR-049).
/// </summary>
public sealed record ApiResponse<T>
{
    private ApiResponse(bool success, T? data, ApiError? error)
    {
        Success = success;
        Data = data;
        Error = error;
    }

    public bool Success { get; }

    public T? Data { get; }

    public ApiError? Error { get; }

    public static ApiResponse<T> Ok(T data) => new(true, data, null);

    public static ApiResponse<T> Fail(ApiError error) => new(false, default, error);

    public static ApiResponse<T> Fail(
        string code,
        string message,
        IReadOnlyList<ErrorDetail>? details = null,
        string? traceId = null) =>
        new(false, default, new ApiError(code, message, details, traceId));
}

/// <summary>
/// A page of results. Every list endpoint returns this — products, customers, invoices and stock
/// history can all grow unbounded (FR-048).
/// </summary>
public sealed record PagedResult<T>
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int totalItems)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Page is 1-based.");
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize), pageSize, $"Page size must be between 1 and {MaxPageSize}.");
        }

        if (totalItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalItems), totalItems, "Cannot be negative.");
        }

        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalItems = totalItems;
    }

    public IReadOnlyList<T> Items { get; }

    public int Page { get; }

    public int PageSize { get; }

    public int TotalItems { get; }

    public int TotalPages => TotalItems == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);

    /// <summary>Clamps caller-supplied paging into the allowed range rather than rejecting it.</summary>
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
    {
        var normalizedPage = page is null or < 1 ? 1 : page.Value;
        var normalizedSize = pageSize switch
        {
            null or < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize.Value,
        };

        return (normalizedPage, normalizedSize);
    }
}
