using MoizPos.Application.Calculations;

namespace MoizPos.Application.Abstractions;

/// <summary>Reads everything that ever moved one supplier's account.</summary>
public interface ISupplierLedgerRepository
{
    /// <summary>
    /// Every purchase, purchase return and payment for the supplier, in no particular order —
    /// <see cref="SupplierLedger.Build"/> puts them in date order and runs the balance.
    /// </summary>
    Task<IReadOnlyList<SupplierLedgerRow>> RowsAsync(
        long supplierId, CancellationToken cancellationToken = default);
}
