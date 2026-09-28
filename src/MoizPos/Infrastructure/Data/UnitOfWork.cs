using System.Data;
using System.Data.Common;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Data;

/// <inheritdoc />
public sealed class UnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly IDbConnectionFactory _connectionFactory;

    public UnitOfWorkFactory(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var transaction = await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

            return new UnitOfWork(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <inheritdoc />
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private bool _disposed;

    internal UnitOfWork(DbConnection connection, DbTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public DbConnection Connection => _connection;

    public DbTransaction Transaction => _transaction;

    public bool IsCommitted { get; private set; }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsCommitted)
        {
            throw new InvalidOperationException("This unit of work has already been committed.");
        }

        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        IsCommitted = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsCommitted)
        {
            throw new InvalidOperationException("Cannot roll back a committed unit of work.");
        }

        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back if the caller never committed. This is what makes "a crash mid-sale leaves no
    /// partial state" true by default rather than by remembering to handle every path.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!IsCommitted)
        {
            try
            {
                await _transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection may already be broken; disposal must not mask the original error.
            }
        }

        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
