using System.Data.Common;
using MoizPos.Application.Abstractions;

namespace MoizPos.Infrastructure.Data;

/// <summary>
/// Opens a connection, trying again when the attempt was dropped.
///
/// <para>The shop's database is across the internet, where a moment's drop is ordinary: a
/// supplier return once failed on a 15-second connect timeout while the next attempt answered in
/// 190 ms. Retrying the OPEN is safe — no command has run on a connection that never opened.</para>
///
/// <para>Only what <paramref name="isDropped"/> recognises is retried. Anything else — a wrong
/// password, a missing database — fails at once and unchanged, because retrying cannot fix it and
/// calling it "could not reach" would send someone to check a cable that is fine.</para>
///
/// <para>Pure orchestration, so it is tested without MySQL.</para>
/// </summary>
public static class ConnectionRetry
{
    public static async Task<DbConnection> OpenAsync(
        Func<CancellationToken, Task<DbConnection>> openOnce,
        Func<Exception, bool> isDropped,
        int maxAttempts,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await openOnce(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (isDropped(exception) && cancellationToken.IsCancellationRequested)
            {
                // The caller went away. Not a database failure, and not worth another attempt.
                throw new OperationCanceledException(
                    "The request was cancelled while connecting.", exception, cancellationToken);
            }
            catch (Exception exception) when (isDropped(exception))
            {
                if (attempt >= maxAttempts)
                {
                    throw new DatabaseUnavailableException(exception);
                }
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
