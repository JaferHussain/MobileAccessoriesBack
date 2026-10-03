using System.Data.Common;

namespace MoizPos.Application.Abstractions;

/// <summary>
/// Opens connections to the shop database. Lives in Application so services depend on the
/// abstraction, while the MySQL/Dapper detail stays in Infrastructure (Constitution II).
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Opens a new connection. The caller owns it and must dispose it. Throws
    /// <see cref="DatabaseUnavailableException"/> when the server cannot be reached.
    /// </summary>
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The database server could not be reached, even after retrying. Answered as 503 with a message
/// the shopkeeper can act on — "check the internet connection" — rather than "unexpected error",
/// which sent the owner looking for a bug in supplier returns that did not exist.
///
/// <para>It comes from OPENING a connection, and a write opens its connection before doing
/// anything — so a sale or return refused this way wrote nothing. The message does not promise
/// that, though: it cannot know every caller.</para>
/// </summary>
public sealed class DatabaseUnavailableException : Exception
{
    public DatabaseUnavailableException(Exception innerException)
        : base(
            "Could not reach the database. Check the internet connection, then try again.",
            innerException)
    {
    }
}

/// <summary>
/// A single database transaction scoping one business operation.
///
/// Constitution Principle IV: every mutation touching stock, balances, payables or invoices runs
/// inside one of these and either completes fully or leaves no trace. Disposing without calling
/// <see cref="CommitAsync"/> rolls back — so an exception on any path cannot half-apply a sale.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    DbConnection Connection { get; }

    DbTransaction Transaction { get; }

    /// <summary>True once <see cref="CommitAsync"/> has succeeded.</summary>
    bool IsCommitted { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>Starts transactions. Injected into services that mutate money or stock.</summary>
public interface IUnitOfWorkFactory
{
    /// <summary>
    /// Begins a transaction at READ COMMITTED — the isolation the services are written against
    /// (research.md R4). Oversell protection comes from row locks taken inside the transaction,
    /// not from the isolation level.
    /// </summary>
    Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default);
}
