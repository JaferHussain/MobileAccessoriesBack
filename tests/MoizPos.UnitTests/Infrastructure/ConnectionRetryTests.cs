using System.Data.Common;
using FluentAssertions;
using MoizPos.Application.Abstractions;
using MoizPos.Infrastructure.Data;

namespace MoizPos.UnitTests.Infrastructure;

/// <summary>
/// Opening a connection to a database across the internet, where a moment's drop is ordinary.
///
/// <para>Seen in the shop: a supplier return failed with "An unexpected error occurred" because
/// one connection attempt timed out, while the very next second the server answered in 190 ms.
/// Retrying the OPEN is safe — nothing has been written yet — and when it truly cannot connect,
/// the shopkeeper is told that, not "unexpected error".</para>
/// </summary>
public sealed class ConnectionRetryTests
{
    private sealed class DropException : DbException
    {
        public DropException() : base("Connect Timeout expired.") { }
    }

    private sealed class FakeConnection : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "0";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private static bool IsDrop(Exception exception) => exception is DropException;

    [Fact]
    public async Task A_single_dropped_attempt_is_retried_and_the_request_goes_through()
    {
        var attempts = 0;

        var connection = await ConnectionRetry.OpenAsync(
            _ => ++attempts == 1 ? throw new DropException() : Task.FromResult<DbConnection>(new FakeConnection()),
            IsDrop,
            maxAttempts: 2,
            delay: TimeSpan.Zero,
            CancellationToken.None);

        connection.Should().NotBeNull();
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task When_it_truly_cannot_connect_it_says_so_in_words()
    {
        var attempts = 0;

        var act = () => ConnectionRetry.OpenAsync(
            _ => { attempts++; throw new DropException(); },
            IsDrop,
            maxAttempts: 2,
            delay: TimeSpan.Zero,
            CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<DatabaseUnavailableException>();

        thrown.Which.Message.Should().Contain("Could not reach the database");
        thrown.Which.InnerException.Should().BeOfType<DropException>("the real cause stays in the log");
        attempts.Should().Be(2, "it gives up after the attempts it was allowed, not forever");
    }

    [Fact]
    public async Task A_failure_that_is_not_a_dropped_connection_is_never_retried_or_disguised()
    {
        // A wrong password is not a network blip: retrying cannot fix it, and calling it
        // "could not reach" would send someone to check a cable that is fine.
        var attempts = 0;

        var act = () => ConnectionRetry.OpenAsync(
            _ => { attempts++; throw new InvalidOperationException("Access denied"); },
            IsDrop,
            maxAttempts: 3,
            delay: TimeSpan.Zero,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_retried()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var attempts = 0;

        var act = () => ConnectionRetry.OpenAsync(
            _ => { attempts++; throw new DropException(); },
            IsDrop,
            maxAttempts: 3,
            delay: TimeSpan.Zero,
            cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1);
    }
}
