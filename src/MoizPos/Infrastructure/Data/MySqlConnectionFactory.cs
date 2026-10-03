using System.Data.Common;
using MoizPos.Application.Abstractions;
using MySqlConnector;

namespace MoizPos.Infrastructure.Data;

/// <inheritdoc />
public sealed class MySqlConnectionFactory : IDbConnectionFactory
{
    /// <summary>
    /// Makes this session refuse what a lax server would silently coerce — a truncated string, a
    /// NOT NULL column left out of an INSERT.
    ///
    /// <para>Off by default because the shop's own server already runs
    /// <c>STRICT_TRANS_TABLES</c>, and paying a round trip per connection to re-assert what is
    /// already true would cost latency on every query for nothing.</para>
    ///
    /// <para>Turned ON for the test suite, where the local MySQL is NOT strict. Without it the
    /// tests run under weaker rules than production, so a coercion bug passes here and fails on
    /// the live server — exactly the wrong way round. Measured, not assumed: omitting
    /// <c>expenses.payment_source</c> on the test server silently stored 'Till'.</para>
    /// </summary>
    private readonly bool _enforceStrictSqlMode;

    private readonly string _connectionString;

    /// <summary>
    /// One retry. A dropped attempt waits out the full connect timeout (15 s by default) before it
    /// fails, so a third attempt would leave the counter staring at a spinner for most of a minute
    /// — and a drop that outlasts two attempts is not a blip.
    /// </summary>
    private const int ConnectAttempts = 2;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public MySqlConnectionFactory(string connectionString, bool enforceStrictSqlMode = false)
    {
        _enforceStrictSqlMode = enforceStrictSqlMode;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A database connection string is required. Set ConnectionStrings:Default.",
                nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    public Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default) =>
        ConnectionRetry.OpenAsync(
            OpenOnceAsync, IsDroppedConnection, ConnectAttempts, RetryDelay, cancellationToken);

    /// <summary>
    /// A failure to reach the server, as opposed to one the server reported — "Access denied" or
    /// an unknown database are answered, not dropped, and retrying cannot fix them.
    /// </summary>
    private static bool IsDroppedConnection(Exception exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.UnableToConnectToHost }
            or MySqlException { IsTransient: true };

    private async Task<DbConnection> OpenOnceAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (_enforceStrictSqlMode)
            {
                await using var command = connection.CreateCommand();

                // Appended rather than replaced: whatever else the server wants stays.
                command.CommandText =
                    "SET SESSION sql_mode = CONCAT(@@sql_mode, ',STRICT_ALL_TABLES')";

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            // Never leak a half-opened connection back to the pool on failure.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
