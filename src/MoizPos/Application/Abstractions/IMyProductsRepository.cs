namespace MoizPos.Application.Abstractions;

/// <summary>A product a salesman is carrying, with the shop's price for it.</summary>
public sealed record MyProductRow
{
    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    /// <summary>Units in his bag now.</summary>
    public int Quantity { get; init; }

    /// <summary>The shop's retail price — the price a retail sale is made at. Never the cost or
    /// the wholesale price: neither reaches a Staff principal (FR-040).</summary>
    public decimal Price { get; init; }

    /// <summary>Quantity × price.</summary>
    public decimal Value { get; init; }

    /// <summary>The fixed commission the owner promised on each unit he sells. 0 when none.</summary>
    public decimal CommissionPerUnit { get; init; }
}

/// <summary>One product a person sold in the period, net of what came back.</summary>
public sealed record MySoldProductRow
{
    public long ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int QuantitySold { get; init; }

    /// <summary>What the customers paid for them — each line after its share of the bill discount.</summary>
    public decimal Amount { get; init; }
}

public interface IMyProductsRepository
{
    Task<IReadOnlyList<MyProductRow>> AssignedAsync(long userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MySoldProductRow>> SoldAsync(
        long userId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);
}
