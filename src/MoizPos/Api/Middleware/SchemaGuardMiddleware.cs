using System.Text.Json;
using MoizPos.Application.Contracts.Common;

namespace MoizPos.Api.Middleware;

/// <summary>
/// Refuses API requests, in words, while the database is behind the code.
///
/// <para>A deploy that forgets <c>migrate</c> runs new code against a database without its new
/// columns. That happened in the shop: every screen said "An unexpected error occurred", while the
/// fix was one command. Until the database is brought up to date, this answers every API request
/// with a 503 naming the command and the scripts waiting — the page itself and the health check
/// still load, so the message has somewhere to be read.</para>
///
/// <para>The pending list is read once, at startup (<see cref="Migrator.MigrationRunner"/>).
/// Running <c>migrate</c> and restarting the API clears it. Migrations are never applied here:
/// changing the shop's database stays a deliberate step.</para>
/// </summary>
public sealed class SchemaGuardMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<string> _pending;

    public SchemaGuardMiddleware(RequestDelegate next, IReadOnlyList<string> pending)
    {
        _next = next;
        _pending = pending;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (_pending.Count == 0
            || !path.StartsWithSegments("/api")
            || path.StartsWithSegments("/api/health"))
        {
            await _next(context);
            return;
        }

        var scripts = string.Join(", ", _pending.Select(ShortName));

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";

        var body = ApiResponse<object>.Fail(
            "DATABASE_OUT_OF_DATE",
            $"The database needs updating before the app can be used. On the server, run " +
            $"'dotnet MoizPos.dll migrate' (or 'dotnet run --project src/MoizPos -- migrate'), then " +
            $"restart the API. Waiting: {scripts}.",
            details: null,
            traceId: context.TraceIdentifier);

        await context.Response.WriteAsync(JsonSerializer.Serialize(body, SerializerOptions));
    }

    /// <summary>"MoizPos.Migrator.Scripts.0030_transaction_proofs.sql" → "0030_transaction_proofs".</summary>
    private static string ShortName(string script)
    {
        var withoutExtension = script.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? script[..^4] : script;
        var lastDot = withoutExtension.LastIndexOf('.');

        return lastDot >= 0 ? withoutExtension[(lastDot + 1)..] : withoutExtension;
    }
}
