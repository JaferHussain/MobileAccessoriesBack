using System.Text.Json;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Domain.Errors;

namespace MoizPos.Api.Middleware;

/// <summary>
/// T025 — turns every unhandled exception into the single response envelope from
/// contracts/conventions.md, so the counter never receives a shape it cannot parse (FR-049).
///
/// Domain exceptions carry their own error code and a message written for the shopkeeper.
/// Anything else becomes a generic 500 whose detail stays in the log, identified by traceId —
/// an internal error must never leak a stack trace or SQL to the browser.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Nginx's "client closed request"; not present in the ASP.NET constants.</summary>
    private const int ClientClosedRequest = 499;

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            await WriteErrorAsync(context, exception);
        }
    }

    private async Task WriteErrorAsync(HttpContext context, Exception exception)
    {
        var traceId = context.TraceIdentifier;
        var (statusCode, code, message) = Map(exception);

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled error. TraceId {TraceId}", traceId);
        }
        else
        {
            _logger.LogInformation(
                "Request rejected: {Code} — {Message}. TraceId {TraceId}", code, message, traceId);
        }

        if (context.Response.HasStarted)
        {
            // Too late to change the response; the log entry above is all we can offer.
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var body = ApiResponse<object>.Fail(code, message, details: null, traceId: traceId);

        await context.Response.WriteAsync(JsonSerializer.Serialize(body, SerializerOptions));
    }

    private static (int StatusCode, string Code, string Message) Map(Exception exception) =>
        exception switch
        {
            AuthenticationFailedException ex =>
                (StatusCodes.Status401Unauthorized, ex.Code, ex.Message),

            NotFoundException ex =>
                (StatusCodes.Status404NotFound, ex.Code, ex.Message),

            // 403 rather than 400: the request is well-formed and the sale is valid — this
            // caller is simply not allowed to authorise it (FR-051).
            CreditRequiresAdminException ex =>
                (StatusCodes.Status403Forbidden, ex.Code, ex.Message),

            ConcurrencyConflictException ex =>
                (StatusCodes.Status409Conflict, ex.Code, ex.Message),

            BusinessRuleViolationException ex =>
                (StatusCodes.Status422UnprocessableEntity, ex.Code, ex.Message),

            // InsufficientStock, DiscountExceedsTotal, ReturnExceedsOriginal, CustomerRequired
            // and OverpaymentNotConfirmed are all caller-correctable 400s.
            DomainException ex =>
                (StatusCodes.Status400BadRequest, ex.Code, ex.Message),

            // 499 "client closed request": the caller disconnected, so this is not our failure.
            OperationCanceledException =>
                (ClientClosedRequest, ErrorCodes.InternalError, "The request was cancelled."),

            _ => (StatusCodes.Status500InternalServerError, ErrorCodes.InternalError,
                  "An unexpected error occurred. Quote the trace id when reporting this."),
        };
}

public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseExceptionEnvelope(this IApplicationBuilder app) =>
        app.UseMiddleware<ExceptionHandlingMiddleware>();
}
