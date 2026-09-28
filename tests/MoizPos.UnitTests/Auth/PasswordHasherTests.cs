using FluentAssertions;
using MoizPos.Infrastructure.Auth;

namespace MoizPos.UnitTests.Auth;

/// <summary>T028 — password hashing. Written before PasswordHasher exists.</summary>
public sealed class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Verifies_a_correct_password()
    {
        var hash = _hasher.Hash("Admin@123");

        _hasher.Verify("Admin@123", hash).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_wrong_password()
    {
        var hash = _hasher.Hash("Admin@123");

        _hasher.Verify("admin@123", hash).Should().BeFalse();
        _hasher.Verify("Admin@1234", hash).Should().BeFalse();
        _hasher.Verify(string.Empty, hash).Should().BeFalse();
    }

    [Fact]
    public void Never_stores_the_password_in_the_hash()
    {
        var hash = _hasher.Hash("Admin@123");

        hash.Should().NotContain("Admin@123");
    }

    [Fact]
    public void Produces_a_different_hash_each_time_for_the_same_password()
    {
        // A per-password salt: two staff choosing the same password must not share a hash,
        // or cracking one would crack both.
        var first = _hasher.Hash("Admin@123");
        var second = _hasher.Hash("Admin@123");

        first.Should().NotBe(second);
        _hasher.Verify("Admin@123", first).Should().BeTrue();
        _hasher.Verify("Admin@123", second).Should().BeTrue();
    }

    [Fact]
    public void Handles_non_ascii_passwords()
    {
        var hash = _hasher.Hash("پاسورڈ۱۲۳");

        _hasher.Verify("پاسورڈ۱۲۳", hash).Should().BeTrue();
        _hasher.Verify("پاسورڈ", hash).Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_empty_password_at_hash_time()
    {
        var act = () => _hasher.Hash("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-hash")]
    [InlineData("1.2")]
    [InlineData("abc.def.ghi")]
    public void Returns_false_for_a_malformed_stored_hash(string storedHash)
    {
        // A corrupted row must fail the sign-in, not crash the endpoint.
        _hasher.Verify("Admin@123", storedHash).Should().BeFalse();
    }

    [Fact]
    public void Hash_fits_the_database_column()
    {
        // users.password_hash is VARCHAR(255) (data-model.md §1).
        _hasher.Hash("Admin@123").Length.Should().BeLessThanOrEqualTo(255);
    }
}
