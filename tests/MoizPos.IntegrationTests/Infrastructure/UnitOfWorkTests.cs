using System.Data;
using Dapper;
using FluentAssertions;
using MoizPos.Domain.Enums;

namespace MoizPos.IntegrationTests.Infrastructure;

/// <summary>
/// T018 — proves the transactional guarantee the whole system rests on: a mutation that fails
/// part-way leaves no trace (Constitution Principle IV, FR-050).
///
/// These run against real MySQL. A fake cannot demonstrate that a rollback actually rolled back.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UnitOfWorkTests
{
    private readonly DatabaseFixture _fixture;

    public UnitOfWorkTests(DatabaseFixture fixture) => _fixture = fixture;

    private static string UniqueUsername() => $"t_{Guid.NewGuid():N}"[..20];

    private static async Task<int> CountUserAsync(DatabaseFixture fixture, string username)
    {
        await using var connection = await fixture.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE username = @username;",
            new { username });
    }

    private static async Task InsertUserAsync(IDbConnection connection, IDbTransaction transaction, string username)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO users (username, full_name, password_hash, role, is_active, created_at_utc)
            VALUES (@username, 'Test User', 'x', @role, TRUE, UTC_TIMESTAMP(6));
            """,
            new { username, role = nameof(UserRole.Staff) },
            transaction);
    }

    [Fact]
    public async Task Committed_work_is_visible_to_other_connections()
    {
        var username = UniqueUsername();

        await using (var uow = await _fixture.UnitOfWorkFactory.BeginAsync())
        {
            await InsertUserAsync(uow.Connection, uow.Transaction, username);
            await uow.CommitAsync();
        }

        (await CountUserAsync(_fixture, username)).Should().Be(1);
    }

    [Fact]
    public async Task Work_abandoned_without_commit_is_rolled_back()
    {
        var username = UniqueUsername();

        await using (var uow = await _fixture.UnitOfWorkFactory.BeginAsync())
        {
            await InsertUserAsync(uow.Connection, uow.Transaction, username);
            // Deliberately no CommitAsync — disposal must roll back.
        }

        (await CountUserAsync(_fixture, username)).Should().Be(0);
    }

    [Fact]
    public async Task An_exception_midway_through_commits_nothing()
    {
        var first = UniqueUsername();
        var second = UniqueUsername();

        var act = async () =>
        {
            await using var uow = await _fixture.UnitOfWorkFactory.BeginAsync();

            await InsertUserAsync(uow.Connection, uow.Transaction, first);

            // Simulates a crash between two writes of one business operation.
            throw new InvalidOperationException("Simulated failure mid-transaction.");
        };

        await act.Should().ThrowAsync<InvalidOperationException>();

        (await CountUserAsync(_fixture, first)).Should().Be(0);
        (await CountUserAsync(_fixture, second)).Should().Be(0);
    }

    [Fact]
    public async Task A_failing_second_write_discards_the_first()
    {
        var username = UniqueUsername();

        var act = async () =>
        {
            await using var uow = await _fixture.UnitOfWorkFactory.BeginAsync();

            await InsertUserAsync(uow.Connection, uow.Transaction, username);

            // Same username again — violates uq_users_username and throws.
            await InsertUserAsync(uow.Connection, uow.Transaction, username);

            await uow.CommitAsync();
        };

        await act.Should().ThrowAsync<Exception>();

        (await CountUserAsync(_fixture, username)).Should().Be(0);
    }

    [Fact]
    public async Task Explicit_rollback_discards_the_work()
    {
        var username = UniqueUsername();

        await using (var uow = await _fixture.UnitOfWorkFactory.BeginAsync())
        {
            await InsertUserAsync(uow.Connection, uow.Transaction, username);
            await uow.RollbackAsync();
        }

        (await CountUserAsync(_fixture, username)).Should().Be(0);
    }

    [Fact]
    public async Task Uncommitted_work_is_invisible_to_other_connections()
    {
        var username = UniqueUsername();

        await using var uow = await _fixture.UnitOfWorkFactory.BeginAsync();
        await InsertUserAsync(uow.Connection, uow.Transaction, username);

        // READ COMMITTED: another connection must not see the pending insert.
        (await CountUserAsync(_fixture, username)).Should().Be(0);

        await uow.CommitAsync();

        (await CountUserAsync(_fixture, username)).Should().Be(1);
    }

    [Fact]
    public async Task Runs_at_read_committed_isolation()
    {
        await using var uow = await _fixture.UnitOfWorkFactory.BeginAsync();

        var level = await uow.Connection.ExecuteScalarAsync<string>(
            "SELECT @@transaction_isolation;", transaction: uow.Transaction);

        level.Should().Be("READ-COMMITTED");
    }

    [Fact]
    public async Task Committing_twice_is_refused()
    {
        await using var uow = await _fixture.UnitOfWorkFactory.BeginAsync();
        await uow.CommitAsync();

        var act = async () => await uow.CommitAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Is_committed_reflects_the_outcome()
    {
        await using var uow = await _fixture.UnitOfWorkFactory.BeginAsync();

        uow.IsCommitted.Should().BeFalse();

        await uow.CommitAsync();

        uow.IsCommitted.Should().BeTrue();
    }
}
