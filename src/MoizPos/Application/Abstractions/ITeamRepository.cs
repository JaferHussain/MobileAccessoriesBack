namespace MoizPos.Application.Abstractions;

/// <summary>
/// One person's period, as the owner's Team card shows it. Init-only properties: Dapper
/// materialises it.
/// </summary>
public sealed record TeamMemberRow
{
    public long UserId { get; init; }

    public string FullName { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    /// <summary>Counter or FieldSales for staff; null for the owner.</summary>
    public string? Job { get; init; }

    public int InvoiceCount { get; init; }

    /// <summary>Net of returns, like every other sales total.</summary>
    public decimal TotalSales { get; init; }

    /// <summary>What was paid at the sale — cash or transfer.</summary>
    public decimal ReceivedAtSale { get; init; }

    public decimal CreditGiven { get; init; }

    /// <summary>Line and whole-bill discounts together.</summary>
    public decimal DiscountGiven { get; init; }

    public int ReturnCount { get; init; }

    public decimal ReturnValue { get; init; }

    /// <summary>Udhaar this person took back from customers.</summary>
    public decimal UdhaarCollected { get; init; }

    public DateTime? LastLoginUtc { get; init; }

    /// <summary>Cash a field salesman is holding now, until he hands it over. Zero at the counter.</summary>
    public decimal CashInHand { get; init; }

    /// <summary>Units of stock a field salesman is carrying now. Zero at the counter.</summary>
    public int StockUnits { get; init; }
}

/// <summary>One thing a person did — a line on their activity timeline.</summary>
public sealed record TeamActivityRow
{
    /// <summary>Sale, Return, Recovery, SupplierPayment, Expense, Purchase or SignIn.</summary>
    public string Kind { get; init; } = string.Empty;

    public long ReferenceId { get; init; }

    /// <summary>Invoice, return or receipt number; an expense's category; a purchase's product.</summary>
    public string? Reference { get; init; }

    public DateTime EntryDateUtc { get; init; }

    public decimal? Amount { get; init; }

    public string? Method { get; init; }

    /// <summary>The customer or supplier; a sign-in's device.</summary>
    public string? Detail { get; init; }
}

/// <summary>A line on the owner's watch list — something to look at, never an accusation.</summary>
public sealed record WatchItemRow
{
    /// <summary>BigDiscount, TransferWithoutProof, SameDayReturn or TransferRefund.</summary>
    public string Kind { get; init; } = string.Empty;

    public long ReferenceId { get; init; }

    public string? Reference { get; init; }

    public DateTime EntryDateUtc { get; init; }

    public long UserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public string? Detail { get; init; }
}

/// <summary>A sale that gave any discount at all — the raw material for the BigDiscount flag.</summary>
public sealed record DiscountedSaleRow
{
    public long InvoiceId { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateTime InvoiceDateUtc { get; init; }

    public long UserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    /// <summary>What the goods were listed at, before any discount.</summary>
    public decimal Gross { get; init; }

    /// <summary>Line and whole-bill discounts together.</summary>
    public decimal Discount { get; init; }
}

public interface ITeamRepository
{
    Task<IReadOnlyList<TeamMemberRow>> MembersAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamActivityRow>> ActivityAsync(
        long userId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);

    /// <summary>Every flag decided by the data alone: transfer sales without proof, same-day returns, transfer refunds.</summary>
    Task<IReadOnlyList<WatchItemRow>> WatchItemsAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscountedSaleRow>> DiscountedSalesAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);
}
