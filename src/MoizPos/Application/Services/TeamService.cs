using System.Globalization;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Calculations;
using MoizPos.Application.Time;
using MoizPos.Domain.Errors;

namespace MoizPos.Application.Services;

/// <summary>A person's own day: their card and what they sold, returned and recovered.</summary>
public sealed record MyDay(TeamMemberRow Card, IReadOnlyList<TeamActivityRow> Activity);

public interface ITeamService
{
    /// <summary>
    /// The signed-in person's own figures — the same card the owner sees, never anyone else's.
    /// Only selling is listed: sign-ins, purchases, supplier payments and expenses are the owner's
    /// business, and a purchase line carries cost.
    /// </summary>
    Task<MyDay> MineAsync(long userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamMemberRow>> MembersAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamActivityRow>> ActivityAsync(
        long userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchItemRow>> WatchListAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);
}

/// <summary>
/// The owner's view of the team. Everything here is read from what the shop already records —
/// each sale, return and payment names who made it — so nothing needs recording twice. Admin only
/// at the controller.
/// </summary>
public sealed class TeamService : ITeamService
{
    private readonly ITeamRepository _team;
    private readonly IUserRepository _users;
    private readonly PeriodResolver _periods;
    private readonly IClock _clock;

    public TeamService(ITeamRepository team, IUserRepository users, PeriodResolver periods, IClock clock)
    {
        _team = team;
        _users = users;
        _periods = periods;
        _clock = clock;
    }

    private static readonly HashSet<string> OwnSelling = new(StringComparer.Ordinal) { "Sale", "Return", "Recovery" };

    public async Task<MyDay> MineAsync(long userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User", userId);
        var range = Range(from, to);

        var card = (await _team.MembersAsync(range.StartUtc, range.EndUtc, cancellationToken))
            .FirstOrDefault(member => member.UserId == userId)
            ?? new TeamMemberRow { UserId = userId, FullName = user.FullName, Role = user.Role.ToString(), Job = user.Job?.ToString() };

        var activity = (await _team.ActivityAsync(userId, range.StartUtc, range.EndUtc, cancellationToken))
            .Where(entry => OwnSelling.Contains(entry.Kind))
            .ToList();

        return new MyDay(card, activity);
    }

    public Task<IReadOnlyList<TeamMemberRow>> MembersAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var range = Range(from, to);

        return _team.MembersAsync(range.StartUtc, range.EndUtc, cancellationToken);
    }

    public async Task<IReadOnlyList<TeamActivityRow>> ActivityAsync(
        long userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        _ = await _users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User", userId);

        var range = Range(from, to);

        return await _team.ActivityAsync(userId, range.StartUtc, range.EndUtc, cancellationToken);
    }

    public async Task<IReadOnlyList<WatchItemRow>> WatchListAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var range = Range(from, to);

        var flagged = await _team.WatchItemsAsync(range.StartUtc, range.EndUtc, cancellationToken);

        // The discount threshold lives in WatchRules, not in SQL, so it is tested and readable.
        var bigDiscounts = (await _team.DiscountedSalesAsync(range.StartUtc, range.EndUtc, cancellationToken))
            .Where(sale => WatchRules.IsBigDiscount(sale.Gross, sale.Discount))
            .Select(sale => new WatchItemRow
            {
                Kind = "BigDiscount",
                ReferenceId = sale.InvoiceId,
                Reference = sale.InvoiceNumber,
                EntryDateUtc = sale.InvoiceDateUtc,
                UserId = sale.UserId,
                UserName = sale.UserName,
                Amount = sale.Discount,
                Detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{WatchRules.DiscountPercent(sale.Gross, sale.Discount)}% off — Rs {sale.Discount:N0} of Rs {sale.Gross:N0}"),
            });

        return flagged.Concat(bigDiscounts).OrderByDescending(item => item.EntryDateUtc).ToList();
    }

    /// <summary>Shop-local days, both inclusive; today when neither is given.</summary>
    private DateRangeUtc Range(DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(_periods.ToShopLocal(_clock.UtcNow));
        var first = from ?? to ?? today;
        var last = to ?? from ?? today;

        if (last < first)
        {
            throw new BusinessRuleViolationException("The end date cannot be before the start date.");
        }

        return _periods.ResolveLocalDateRange(first, last);
    }
}
