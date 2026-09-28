using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Data;

/// <summary>
/// Creates the first Admin so a brand-new installation can be signed into.
///
/// Only ever runs when the users table is completely empty, so it cannot resurrect a deleted
/// account or reset a real password. The default credentials are printed once, loudly, because
/// leaving them in place would give anyone who reaches the shop's network full access to cost
/// prices and profit.
/// </summary>
public sealed class DatabaseSeeder
{
    public const string DefaultUsername = "admin";
    public const string DefaultPassword = "Admin@123";

    private readonly IUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;

    public DatabaseSeeder(IUserRepository users, IPasswordHasher passwordHasher)
    {
        _users = users;
        _passwordHasher = passwordHasher;
    }

    /// <returns>True when a default admin was created by this call.</returns>
    public async Task<bool> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await _users.CountActiveAdminsAsync(cancellationToken) > 0)
        {
            return false;
        }

        if (await _users.FindByUsernameAsync(DefaultUsername, cancellationToken) is not null)
        {
            return false;
        }

        await _users.CreateAsync(
            new User
            {
                Username = DefaultUsername,
                FullName = "Shop Owner",
                PasswordHash = _passwordHasher.Hash(DefaultPassword),
                Role = UserRole.Admin,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
            },
            cancellationToken);

        return true;
    }
}
