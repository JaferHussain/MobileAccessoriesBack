using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using System.Text;
using System.Data.Common;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using MoizPos.Api.Authorization;
using MoizPos.Migrator;
using MoizPos.Api.Controllers;
using MoizPos.Api.Middleware;
using MoizPos.Application.Abstractions;
using MoizPos.Application.Contracts.Auth;
using MoizPos.Application.Contracts.Common;
using MoizPos.Application.Services;
using MoizPos.Application.Time;
using MoizPos.Domain.Errors;
using MoizPos.Infrastructure.Auth;
using MoizPos.Infrastructure.Backup;
using MoizPos.Infrastructure.Data;
using MoizPos.Infrastructure.Repositories;
using MoizPos.Application.Documents;
using MoizPos.Infrastructure.Documents;
using MoizPos.Infrastructure.Storage;
using QuestPDF.Infrastructure;
using Serilog;

// QuestPDF Community licence: free below USD 1M annual revenue (research.md R3).
QuestPDF.Settings.License = LicenseType.Community;

// The database migrator lives in this same project (Constitution: schema changes only ever reach
// a database through it). Reached as `dotnet run --project backend/src/MoizPos -- migrate`, it
// applies the scripts and exits without ever starting the web host.
if (args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
{
    return MigratorCli.Run(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);

// A per-machine settings file. Added last, so it overrides everything before it — including
// user-secrets — which makes it the single place to look when asking "what is this machine
// actually using?". It is gitignored, which is the point: THIS machine's connection string lives
// in the project as an ordinary settings file, visible and editable, but never pushed.
// Optional — nothing breaks if it does not exist.
//
// The integration test host MUST opt out via SkipMachineLocalSettings. Its own connection string
// arrives through ConfigureAppConfiguration, which is applied AFTER this file, and the string is
// read a few lines below to build the connection factory — so without the opt-out this file wins
// and the whole suite runs against whatever database this machine happens to name. That is not
// hypothetical: it pointed the suite at the live server once, and 201 tests failed at login
// because their users existed in the test database instead.
if (!builder.Configuration.GetValue<bool>("SkipMachineLocalSettings"))
{
    builder.Configuration.AddJsonFile(
        $"appsettings.{builder.Environment.EnvironmentName}.local.json",
        optional: true,
        reloadOnChange: true);
}

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

// ---------------------------------------------------------------- configuration
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default is not configured. Set it with: " +
        "dotnet user-secrets set \"ConnectionStrings:Default\" \"Server=localhost;Database=moizpos;...\"");

var jwtOptions = new JwtOptions
{
    Issuer = builder.Configuration["Jwt:Issuer"] ?? "MoizPos",
    Audience = builder.Configuration["Jwt:Audience"] ?? "MoizPosCounter",
    Key = builder.Configuration["Jwt:Key"] ?? string.Empty,
    AccessTokenMinutes = int.TryParse(builder.Configuration["Jwt:AccessTokenMinutes"], out var m) ? m : 60,
    RefreshTokenDays = int.TryParse(builder.Configuration["Jwt:RefreshTokenDays"], out var d) ? d : 30,
};

jwtOptions.Validate();

DapperConfig.Apply();

// ---------------------------------------------------------------- services
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<PeriodResolver>();
// The shop's server already runs STRICT_TRANS_TABLES, so this stays off there: re-asserting it
// would cost a round trip per connection for nothing. The test host turns it on, because the
// local MySQL is not strict and tests must not run under weaker rules than production.
var enforceStrictSqlMode = builder.Configuration.GetValue<bool>("Database:EnforceStrictSqlMode");

builder.Services.AddSingleton<IDbConnectionFactory>(
    _ => new MySqlConnectionFactory(connectionString, enforceStrictSqlMode));
builder.Services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();

builder.Services.AddSingleton<IStockMovementWriter, StockMovementWriter>();
builder.Services.AddSingleton<IAuditWriter, AuditWriter>();
builder.Services.AddSingleton<IPurchaseWriteRepository, PurchaseWriteRepository>();
builder.Services.AddSingleton<IStockWriteRepository, StockWriteRepository>();
builder.Services.AddSingleton<IInvoiceWriteRepository, InvoiceWriteRepository>();
builder.Services.AddSingleton<ICustomerPaymentWriteRepository, CustomerPaymentWriteRepository>();
builder.Services.AddSingleton<IReturnWriteRepository, ReturnWriteRepository>();
builder.Services.AddSingleton<IReturnReadRepository, ReturnReadRepository>();

builder.Services.AddSingleton<IPdfRenderer, PdfRenderer>();
builder.Services.AddSingleton<IPdfRendererPort, PdfRendererAdapter>();

builder.Services.AddSingleton(new DocumentOptions
{
    PublicBaseUrl = builder.Configuration["Documents:PublicBaseUrl"] ?? string.Empty,
    ShareLinkExpiryDays =
        int.TryParse(builder.Configuration["Documents:ShareLinkExpiryDays"], out var shareDays)
            ? shareDays
            : 30,
    Shop = new ShopDetails
    {
        Name = builder.Configuration["Shop:Name"] ?? "Moiz Mobile & Corporation",
        Location = builder.Configuration["Shop:Location"] ?? "Danwran Lodhran",
        ContactNumber = builder.Configuration["Shop:ContactNumber"],
    },
});

var backupOptions = new BackupOptions
{
    Directory = builder.Configuration["Backup:Directory"] ?? "backups",
    RetainDays = int.TryParse(builder.Configuration["Backup:RetainDays"], out var retain) ? retain : 30,
    RunAtLocalHour = int.TryParse(builder.Configuration["Backup:RunAtLocalHour"], out var hour) ? hour : 2,
    // Empty means the MySQL client tools are already on PATH, which is the server case.
    ToolsDirectory = builder.Configuration["Backup:ToolsDirectory"] ?? string.Empty,

    // Product pictures ride along with each dump (FR-017). The database stores only their
    // paths, so a database-only backup restores a catalogue with every photograph missing.
    // Resolved against the content root here, the same way ImageStorageService resolves it.
    ProductImageDirectory = Path.Combine(
        builder.Environment.ContentRootPath,
        builder.Configuration["Storage:ProductImageRoot"] ?? "content/products"),

    // And the payment proofs, for the same reason — resolved the same way ImageStorageService
    // resolves PaymentProofRoot, so the backup reads exactly the folder uploads write to.
    PaymentProofDirectory = Path.Combine(
        builder.Environment.ContentRootPath,
        builder.Configuration["Storage:PaymentProofRoot"] ?? "content/payment-proofs"),
};

builder.Services.AddSingleton(backupOptions);

builder.Services.AddSingleton<IBackupService>(provider => new BackupService(
    backupOptions,
    connectionString,
    provider.GetRequiredService<IClock>(),
    provider.GetRequiredService<PeriodResolver>()));

builder.Services.AddSingleton(provider => new BackupSchedule(
    backupOptions.RunAtLocalHour, provider.GetRequiredService<PeriodResolver>()));

builder.Services.AddHostedService<BackupHostedService>();

builder.Services.AddSingleton(new ImageStorageOptions
{
    ProductImageRoot = builder.Configuration["Storage:ProductImageRoot"] ?? "content/products",
    // The same key the backup reads, so uploads and backups can never name different folders.
    PaymentProofRoot = builder.Configuration["Storage:PaymentProofRoot"] ?? "content/payment-proofs",
    MaxImageBytes = long.TryParse(builder.Configuration["Storage:MaxImageBytes"], out var maxBytes)
        ? maxBytes
        : 2 * 1024 * 1024,
});

builder.Services.AddSingleton<IImageStorageService>(provider => new ImageStorageService(
    provider.GetRequiredService<ImageStorageOptions>(),
    provider.GetRequiredService<IWebHostEnvironment>().ContentRootPath));

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<IBrandRepository, BrandRepository>();
builder.Services.AddScoped<ISupplierRepository, SupplierRepository>();
builder.Services.AddScoped<ISupplierLedgerRepository, SupplierLedgerRepository>();
builder.Services.AddScoped<ITransactionProofRepository, TransactionProofRepository>();
builder.Services.AddScoped<IShopAccountRepository, ShopAccountRepository>();
builder.Services.AddScoped<ITeamRepository, TeamRepository>();
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<ICommissionRepository, CommissionRepository>();
builder.Services.AddScoped<ICommissionService, CommissionService>();
builder.Services.AddScoped<ISalesmanCashRepository, SalesmanCashRepository>();
builder.Services.AddScoped<ISalesmanCashService, SalesmanCashService>();
builder.Services.AddScoped<ISalesmanStockRepository, SalesmanStockRepository>();
builder.Services.AddScoped<ISalesmanStockService, SalesmanStockService>();
builder.Services.AddScoped<IUdhaarCustomerRepository, UdhaarCustomerRepository>();
builder.Services.AddScoped<IRecoveryRepository, RecoveryRepository>();
builder.Services.AddScoped<IRecoveryService, RecoveryService>();
builder.Services.AddScoped<IPurchaseBillRepository, PurchaseBillRepository>();
builder.Services.AddScoped<IPurchaseBillService, PurchaseBillService>();
builder.Services.AddScoped<IPurchaseRepository, PurchaseRepository>();
builder.Services.AddScoped<IStockMovementRepository, StockMovementRepository>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IBrandService, BrandService>();
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IInvoiceReadRepository, InvoiceReadRepository>();
builder.Services.AddScoped<ILedgerRepository, LedgerRepository>();
builder.Services.AddScoped<ICustomerLedgerService, CustomerLedgerService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IReturnService, ReturnService>();
builder.Services.AddScoped<IExpenseRepository, ExpenseRepository>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();
builder.Services.AddScoped<IReportingService, ReportingService>();

// Counting the drawer — the only control over physical cash.
builder.Services.AddScoped<IDayClosingRepository, DayClosingRepository>();
builder.Services.AddScoped<IDayClosingService, DayClosingService>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<IDocumentTokenRepository, DocumentTokenRepository>();
builder.Services.AddScoped<ICustomerPaymentReadRepository, CustomerPaymentReadRepository>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IPurchaseService, PurchaseService>();
builder.Services.AddScoped<ISupplierLedgerService, SupplierLedgerService>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<IAuthService>(provider => new AuthService(
    provider.GetRequiredService<IUserRepository>(),
    provider.GetRequiredService<IRefreshTokenRepository>(),
    provider.GetRequiredService<IPasswordHasher>(),
    provider.GetRequiredService<ITokenService>(),
    provider.GetRequiredService<IClock>(),
    jwtOptions.RefreshTokenDays));

// ---------------------------------------------------------------- auth
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            // No grace period on expiry: the default 5 minutes would silently extend every token.
            ClockSkew = TimeSpan.Zero,
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.AdminOnly, policy => policy.RequireRole(Roles.Admin))
    // Authenticated by default: an endpoint must opt out with [AllowAnonymous], so forgetting
    // an attribute fails closed rather than exposing data (FR-038).
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// ---------------------------------------------------------------- mvc
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        // Enums travel as names, not numbers: contracts/openapi.yaml documents
        // "Cash"/"Partial"/"Credit", the counter sends those strings, and a number in a stored
        // JSON payload would be meaningless to anyone reading it later.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddFluentValidationAutoValidation();

// BOTH assemblies. Request contracts live in Application (LoginRequest, ProductUpsertRequest)
// and in Api next to their controllers (CreateInvoiceRequest, RestoreBackupRequest, ...).
// Scanning only one silently leaves the other half's rules unenforced.
builder.Services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();
builder.Services.AddValidatorsFromAssemblyContaining<RestoreBackupValidator>();

// Model-binding and validation failures must use the same envelope as everything else.
builder.Services.Configure<ApiBehaviorOptions>(options =>
    options.InvalidModelStateResponseFactory = context =>
    {
        var details = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .SelectMany(entry => entry.Value!.Errors.Select(error =>
                new ErrorDetail(entry.Key, error.ErrorMessage)))
            .ToList();

        var body = ApiResponse<object>.Fail(
            ErrorCodes.ValidationFailed,
            "One or more fields are invalid.",
            details,
            context.HttpContext.TraceIdentifier);

        return new BadRequestObjectResult(body);
    });

// The public document endpoint is the only unauthenticated data route, so it is the only one a
// stranger can probe. Rate limiting caps how fast tokens could be guessed.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindowLimiter(PublicDocumentsController.RateLimitPolicy, limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

const string CounterCorsPolicy = "CounterApp";
builder.Services.AddCors(options =>
    options.AddPolicy(CounterCorsPolicy, policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

// ---------------------------------------------------------------- pipeline
app.UseExceptionEnvelope();

// A deploy that skipped `migrate` runs new code against old columns, and every screen used to say
// "An unexpected error occurred". Read once here; while anything is waiting, API requests are told
// to run migrate instead. If the database cannot be reached right now the check is skipped — that
// failure has its own message (DatabaseUnavailableException) and must not stop the API starting.
IReadOnlyList<string> pendingMigrations;

try
{
    pendingMigrations = MigrationRunner.PendingScriptNames(connectionString);
}
catch (Exception exception) when (exception is DbException or InvalidOperationException or TimeoutException)
{
    pendingMigrations = [];
    app.Logger.LogWarning(exception, "Could not check for pending database updates at startup.");
}

if (pendingMigrations.Count > 0)
{
    app.Logger.LogCritical(
        "The database is behind this build. Run migrate. Waiting: {Scripts}", string.Join(", ", pendingMigrations));
}

app.UseMiddleware<SchemaGuardMiddleware>(pendingMigrations);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();

// ---------------------------------------------------------------- the counter app
//
// The built React app is served by this same host from wwwroot, so the shop runs on ONE origin:
// the browser asks the same address for the page and for /api. No CORS to configure, nothing to
// keep in step between two sites, and one thing to deploy instead of two.
//
// BEFORE authentication on purpose. The login page has to be reachable by someone who is not yet
// logged in, and this API authorises by default — with these after UseAuthorization, every request
// for the page itself came back 401 and the shop saw a blank screen instead of a login form.
//
// Guarded on index.html being present: in development the app is served by Vite, and the test host
// has no wwwroot. An unconditional fallback would turn a mistyped API path into an HTML page,
// making a genuine 404 look like a working screen.
var counterAppIsBundled =
    File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html"));

if (counterAppIsBundled)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

// Product pictures (feature 005) live on disk under Storage:ProductImageRoot — not inside
// wwwroot, so the block above never serves them. Registered unconditionally, not gated on
// counterAppIsBundled like the block above: a picture must be reachable in every environment,
// including development, where the counter app itself is served by Vite and this backend
// bundles no wwwroot at all. Missing this meant every upload succeeded but every <img> for it
// 404'd, silently falling back to the placeholder no matter how many products were photographed.
var productImageRoot = (builder.Configuration["Storage:ProductImageRoot"] ?? "content/products")
    .Replace('\\', '/').Trim('/');
var productImageDirectory = Path.Combine(app.Environment.ContentRootPath, productImageRoot);
Directory.CreateDirectory(productImageDirectory);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(productImageDirectory),
    RequestPath = $"/{productImageRoot}",
});

// Routing is placed HERE, deliberately, and not left to be inserted for us.
//
// Minimal hosting adds UseRouting() at the very START of the pipeline when it is not called
// explicitly. That puts endpoint selection BEFORE the two UseStaticFiles calls above — and
// StaticFileMiddleware skips a request that already has an endpoint selected. Since
// MapFallbackToFile below matches every path that is not /api, EVERY static request matched the
// fallback and was answered with index.html: product pictures, the SPA's own JavaScript and
// stylesheets, the favicon, all of them 200 OK with Content-Type text/html.
//
// Calling it here means static files get their chance first and routing only sees what is left.
// The integration suite could never have caught this: the test host bundles no wwwroot, so the
// fallback is not registered, no endpoint is ever selected, and the static middleware works.
// A request under /content that the static files above did not serve means the FILE is missing.
//
// Without this it falls through to the authorization middleware, which applies the fallback
// policy even to requests that matched no endpoint — so a missing picture answered 401, reading
// as a permissions problem when the truth is "that file is not on this server". Anonymous on
// purpose: an <img> tag sends no Authorization header, which is also why the static middleware
// above sits before UseAuthentication.
app.MapGet("/content/{**path}", () => Results.NotFound()).AllowAnonymous();

app.UseRouting();

app.UseCors(CounterCorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/api/health", () => Results.Ok(ApiResponse<object>.Ok(new { status = "healthy" })))
   .AllowAnonymous()
   .WithName("Health");

if (counterAppIsBundled)
{
    // Anything not matched by a controller or /api route is a client-side route (/pos,
    // /customers, ...), so hand back index.html and let React Router resolve it. Without this,
    // refreshing the page anywhere but the first screen returns 404.
    //
    // AllowAnonymous because the fallback would otherwise inherit this API's authorise-by-default
    // policy, and an unauthenticated visitor could never reach the login screen.
    // The regex excludes /api: without it a mistyped API path returns the HTML page with a 200,
    // and a client bug looks like a working screen instead of the 404 it is.
    // The regex excludes /api AND /content.
    //
    // /api: without it a mistyped API path returns the HTML page with a 200, and a client bug
    // looks like a working screen instead of the 404 it is.
    //
    // /content: a product picture whose FILE is missing must fail honestly. Answering with
    // index.html made "the file is not on this server" indistinguishable from "here is your
    // picture" at the network level — a 200 carrying 465 bytes of HTML. That is exactly how a
    // deployment that had the database rows but none of the image files looked healthy while
    // every photograph on every screen showed the placeholder.
    app.MapFallbackToFile("{*path:regex(^(?!api/|content/).*$)}", "index.html").AllowAnonymous();
}

// ---------------------------------------------------------------- first run
//await using (var scope = app.Services.CreateAsyncScope())
//{
  //  var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

    //if (await seeder.SeedAsync())
    //{
        // Uses the application's logger, not Serilog's static Log — the static logger is never
        // initialised here, so a warning sent there would silently vanish.
      //  scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        //    .CreateLogger("MoizPos.Startup")
          //  .LogWarning(
              //  "Created the default administrator '{Username}' with password '{Password}'. " +
                //"CHANGE THIS PASSWORD before the shop uses the system.",
              //  DatabaseSeeder.DefaultUsername,
            //    DatabaseSeeder.DefaultPassword);
    //}
//}

app.Run();

return 0;

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
