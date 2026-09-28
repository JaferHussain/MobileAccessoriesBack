using System.Data;
using Dapper;

namespace MoizPos.Infrastructure.Data;

/// <summary>
/// Global Dapper setup. Call <see cref="Apply"/> once at startup and once in test setup.
/// </summary>
public static class DapperConfig
{
    private static bool _applied;
    private static readonly object Gate = new();

    public static void Apply()
    {
        lock (Gate)
        {
            if (_applied)
            {
                return;
            }

            // The schema uses snake_case (quantity_on_hand); the entities use PascalCase
            // (QuantityOnHand). This maps between them without an attribute on every property.
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            // MySQL DATETIME has no offset. Everything is stored in UTC (research.md R6), so
            // stamp the Kind on the way out — otherwise DateTimeKind.Unspecified silently
            // becomes local time the first time anything formats it.
            SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
            SqlMapper.AddTypeHandler(new NullableUtcDateTimeHandler());

            // A calendar DATE is not an instant. day_closings.closing_date is the shop's own
            // trading day in Asia/Karachi, so it must never be given a time or a Kind — treat it
            // that way and the day cannot drift across midnight when a UTC offset is applied.
            // Without this, reading a DATE column into a DateOnly throws at materialisation.
            SqlMapper.AddTypeHandler(new DateOnlyHandler());
            SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());

            _applied = true;
        }
    }

    /// <summary>Test seam: allows re-applying configuration in a fresh assembly load context.</summary>
    internal static void ResetForTesting()
    {
        lock (Gate)
        {
            _applied = false;
        }
    }
}

/// <summary>Reads and writes <see cref="DateTime"/> as UTC, never local.</summary>
internal sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override DateTime Parse(object value) =>
        DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);

    public override void SetValue(IDbDataParameter parameter, DateTime value)
    {
        parameter.DbType = DbType.DateTime2;
        parameter.Value = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Unspecified is treated as already-UTC: every write path in this system produces
            // UTC, so converting would corrupt it.
            _ => value,
        };
    }
}

/// <inheritdoc cref="UtcDateTimeHandler" />
internal sealed class NullableUtcDateTimeHandler : SqlMapper.TypeHandler<DateTime?>
{
    public override DateTime? Parse(object value) =>
        value is DateTime dateTime
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : null;

    public override void SetValue(IDbDataParameter parameter, DateTime? value)
    {
        parameter.DbType = DbType.DateTime2;

        if (value is null)
        {
            parameter.Value = DBNull.Value;
            return;
        }

        parameter.Value = value.Value.Kind == DateTimeKind.Local
            ? value.Value.ToUniversalTime()
            : value.Value;
    }
}

/// <summary>
/// Maps a MySQL <c>DATE</c> to <see cref="DateOnly"/>.
///
/// <para>A trading day is a date, not an instant: it has no time and belongs to no time zone, so
/// it must not be carried as a <see cref="DateTime"/> that some later conversion can shift across
/// midnight. The driver hands back a DateTime, and this drops the part that should never have
/// been there.</para>
/// </summary>
internal sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        string text => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"Cannot read {value?.GetType().Name} as a date."),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }
}

/// <inheritdoc cref="DateOnlyHandler" />
internal sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
{
    public override DateOnly? Parse(object value) => value switch
    {
        null or DBNull => null,
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        string text => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"Cannot read {value.GetType().Name} as a date."),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly? value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value;
    }
}
