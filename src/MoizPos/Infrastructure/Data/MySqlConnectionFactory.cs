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

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
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
