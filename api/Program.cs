using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Formatting.Compact;
using Hangfire;
using Hangfire.PostgreSql;
using FluentValidation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Endpoints;
using BidBuilder.Api.Tenancy;
using BidBuilder.Api.Telemetry;

// Keep JWT claim names exactly as issued ("sub", "role", "tenant_slug" …) — no
// remap to long WS-* URIs. CurrentUser + TenantResolutionMiddleware rely on this.
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

// QuestPDF Community licence (free for small businesses / open source).
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

var isDev = builder.Environment.IsDevelopment();

// ── Structured logging (Serilog) ───────────────────────────────────────────────
// Human-readable console in Development; compact JSON (one object per line) in
// every other environment so logs are machine-parseable by a log aggregator and
// every request/error line carries structured fields (and a trace id, below).
builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .Enrich.WithProperty("app", "bidbuilder-api");
    if (ctx.HostingEnvironment.IsDevelopment())
        cfg.WriteTo.Console();
    else
        cfg.WriteTo.Console(new RenderedCompactJsonFormatter());
});

// ── Services ──────────────────────────────────────────────────────────────────
// Outside Development the connection string MUST be supplied (no insecure
// localhost/postgres fallback leaking into a real deployment).
var connString = builder.Configuration.GetConnectionString("Postgres")
                 ?? (isDev ? "Host=localhost;Database=bidbuilder;Username=postgres;Password=postgres" : null)
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:Postgres must be configured outside Development (e.g. ConnectionStrings__Postgres).");

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connString, npg =>
{
    // Survive transient DB blips (failover, brief network loss, a deploy restart)
    // instead of surfacing them as raw 500s. NOTE: a retrying execution strategy is
    // incompatible with manual BeginTransaction — every such site wraps its
    // transaction in db.Database.CreateExecutionStrategy().ExecuteAsync(...).
    npg.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
    npg.CommandTimeout(30);
}));
builder.Services.AddScoped<ITenantContext, TenantContext>();
// In-memory cache backs SAML single-use assertion replay protection (20.8b).
builder.Services.AddMemoryCache();

// RFC-7807 ProblemDetails for unhandled errors, enriched with a trace id so a 500
// in the field can be correlated to the exact request in the structured logs.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
        ctx.ProblemDetails.Extensions["traceId"] =
            System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier;
});
builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<ProjectAccessService>();
builder.Services.AddScoped<BidBuilder.Api.Services.RateEngine>();
builder.Services.AddScoped<BidBuilder.Api.Services.EstimateCalculator>();
builder.Services.AddScoped<BidBuilder.Api.Services.RateCascadeService>();
builder.Services.AddScoped<BidBuilder.Api.Services.ExportService>();
builder.Services.AddScoped<BidBuilder.Api.Services.ImportService>();
builder.Services.AddScoped<BidBuilder.Api.Services.AuditService>();
builder.Services.AddScoped<BidBuilder.Api.Services.NotificationService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<BidBuilder.Api.Services.IWebhookSender, BidBuilder.Api.Services.HttpWebhookSender>();
builder.Services.AddScoped<BidBuilder.Api.Services.WebhookDispatcher>();
builder.Services.AddScoped<BidBuilder.Api.Services.AreaRollupService>();
builder.Services.AddScoped<BidBuilder.Api.Services.BenchmarkService>();
builder.Services.AddScoped<BidBuilder.Api.Services.RateSuggestionService>();

// ── FluentValidation (19.7) ───────────────────────────────────────────────────
// Auto-register every IValidator<T> in the API assembly so the ValidationFilter
// can resolve them by request type without per-endpoint wiring. The filter itself
// is attached per-route below; validators run BEFORE the handler and short-circuit
// to RFC-7807 ProblemDetails on failure.
builder.Services.AddValidatorsFromAssemblyContaining<BidBuilder.Api.Validation.ValidationFilter<object>>();

// ── Background-job queue (Hangfire on Postgres, 18.4) ────────────────────────
// Resource edits are common; the cascade that recomputes every dependent assembly
// + estimate is expensive. We push the cascade onto a persisted job queue so the
// PUT/DELETE response stays fast, and a slow recompute can retry without blocking
// the user. Two modes:
//   • Enabled (default): Hangfire on its own Postgres schema ("hangfire") + an
//     in-process worker. Production + dev.
//   • Disabled: tests set Hangfire:Enabled=false and the cascade runs inline so
//     post-mutation assertions can see the cascaded effect deterministically.
// Either mode binds ICascadeQueue; ResourceEndpoints only ever calls that abstraction.
var hangfireEnabled = builder.Configuration.GetValue("Hangfire:Enabled", true);
if (hangfireEnabled)
{
    builder.Services.AddHangfire(h => h
        .SetDataCompatibilityLevel(Hangfire.CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connString),
            new Hangfire.PostgreSql.PostgreSqlStorageOptions
            {
                // Dedicated schema so EF migrations and Hangfire's install/upgrade SQL
                // never collide. Hangfire creates the schema and its tables on startup.
                SchemaName = "hangfire",
                QueuePollInterval = TimeSpan.FromSeconds(2),
                InvisibilityTimeout = TimeSpan.FromMinutes(5),
                PrepareSchemaIfNecessary = true,
            }));
    builder.Services.AddHangfireServer(opt =>
    {
        opt.WorkerCount = Math.Min(Environment.ProcessorCount * 2, 8);
        opt.Queues = new[] { "default" };
    });
    builder.Services.AddScoped<BidBuilder.Api.Services.RateCascadeJob>();
    builder.Services.AddScoped<BidBuilder.Api.Services.ICascadeQueue, BidBuilder.Api.Services.HangfireCascadeQueue>();
    builder.Services.AddSingleton<BidBuilder.Api.Auth.HangfireDashboardAuth>();
}
else
{
    builder.Services.AddScoped<BidBuilder.Api.Services.ICascadeQueue, BidBuilder.Api.Services.InlineCascadeQueue>();
}

// Liveness vs readiness: the DB probe is tagged "ready" so /readyz reflects the
// database while /healthz stays a pure liveness signal (process is up). The
// container healthcheck uses /healthz, so a transient Postgres outage doesn't get
// a perfectly healthy API killed and restarted.
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("db", tags: new[] { "ready" });

// ── OpenTelemetry traces + metrics (19.3) ────────────────────────────────────
// Two-sided observability:
//   • Traces — auto from ASP.NET Core, HttpClient, EF Core (+ Npgsql via EF),
//     PLUS our own ActivitySource for the Hangfire cascade job. A failed
//     request can be followed through every DB call.
//   • Metrics — auto runtime + http.server.* + our domain counters
//     (estimates.published, cascade.runs, cascade.failures).
// OTLP exporter is OPT-IN: set OpenTelemetry:Otlp:Endpoint (or the standard
// OTEL_EXPORTER_OTLP_ENDPOINT env var) and traces+metrics ship there. Without
// an endpoint, OTel still records in-process so the Prometheus scrape endpoint
// (/metrics) and the in-process MeterListener in tests both work. Disable
// entirely with OpenTelemetry:Enabled=false (test fixture does this so the
// background OTel collection threads don't outlive the test host).
var otelEnabled = builder.Configuration.GetValue("OpenTelemetry:Enabled", true);
var otlpEndpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"]
                   ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (otelEnabled)
{
    var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "bidbuilder-api";
    var serviceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    var resourceBuilder = ResourceBuilder.CreateDefault()
        .AddService(serviceName, serviceVersion: serviceVersion)
        .AddAttributes(new KeyValuePair<string, object>[]
        {
            new("deployment.environment", builder.Environment.EnvironmentName),
        });

    builder.Services.AddOpenTelemetry()
        .WithTracing(t =>
        {
            t.SetResourceBuilder(resourceBuilder)
             .AddSource(BidBuilderTelemetry.SourceName)
             .AddAspNetCoreInstrumentation(o =>
             {
                 // Drop the health/ready probes — they fire constantly and only add noise.
                 o.Filter = ctx => ctx.Request.Path != "/healthz" && ctx.Request.Path != "/readyz";
                 o.RecordException = true;
             })
             .AddHttpClientInstrumentation()
             .AddEntityFrameworkCoreInstrumentation(o => o.SetDbStatementForText = false);
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        })
        .WithMetrics(m =>
        {
            m.SetResourceBuilder(resourceBuilder)
             .AddMeter(BidBuilderTelemetry.MeterName)
             .AddAspNetCoreInstrumentation()
             .AddHttpClientInstrumentation()
             .AddPrometheusExporter();
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        });
}

// ── Auth ──────────────────────────────────────────────────────────────────────
// A strong, non-default signing key is mandatory outside Development; refuse to
// boot otherwise so a dev/placeholder key can never sign production tokens.
var jwtKey = builder.Configuration["Jwt:SigningKey"]
             ?? (isDev ? "dev-only-bidbuilder-signing-key-please-rotate-32b" : null)
             ?? throw new InvalidOperationException("Jwt:SigningKey must be configured outside Development (e.g. Jwt__SigningKey).");
if (!isDev && (jwtKey.Length < 32
               || jwtKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase)
               || jwtKey.StartsWith("dev-only", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException(
        "Jwt:SigningKey must be a strong, non-default secret of at least 32 characters in non-Development environments.");
// Write the resolved key back so JwtService (which reads Jwt:SigningKey from config)
// sees the same value — this is what lets us keep NO signing key in the committed
// appsettings.json: prod supplies it via env, dev falls back to the constant above.
builder.Configuration["Jwt:SigningKey"] = jwtKey;
var jwtIssuer   = builder.Configuration["Jwt:Issuer"]   ?? "bidbuilder";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "bidbuilder";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = jwtIssuer,
            ValidateAudience         = true,
            ValidAudience            = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime         = true,
            // Tighten the default 5-minute leeway so an expired token isn't honoured
            // for several extra minutes.
            ClockSkew                = TimeSpan.FromSeconds(30),
            RoleClaimType            = "role",
            NameClaimType            = "name",
        };
        // Stateless-JWT revocation: after the signature/lifetime check passes, re-validate
        // the token against the live user — reject it if the account was deactivated or its
        // token-version was bumped (role change / password reset) since the token was issued.
        // Looks the user up by primary key across tenants (IgnoreQueryFilters) because the
        // tenant context isn't resolved yet at authentication time.
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var sub = ctx.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                if (!int.TryParse(sub, out var uid)) { ctx.Fail("Invalid token subject."); return; }

                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var user = await db.Users.IgnoreQueryFilters()
                    .Where(u => u.Id == uid)
                    .Select(u => new { u.IsActive, u.TokenVersion })
                    .FirstOrDefaultAsync();

                if (user is null || !user.IsActive) { ctx.Fail("Account is inactive."); return; }

                var tv = ctx.Principal?.FindFirst(JwtService.TokenVersionClaim)?.Value;
                if (!int.TryParse(tv, out var tokenVer) || tokenVer != user.TokenVersion)
                    ctx.Fail("Token has been superseded.");
            },
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Pinned Info block so the emitted spec is stable across builds and consumers
    // (the generated TS client embeds this metadata) — bump Version when the contract
    // changes in a way that affects external consumers.
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "BidBuilder API",
        Version     = "v1",
        Description = "Multi-tenant construction bid-estimating API. The generated TS client (lib/api-generated.d.ts) is produced from this spec; a runtime-vs-checked-in diff in CI fails the build on contract drift (19.5).",
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. Example: \"Bearer {token}\"",
        Name = "Authorization", In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey, Scheme = "Bearer",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            },
            Array.Empty<string>()
        }
    });
});

// Comma-separated allowlist. In Development the local web (3000/3100) is the
// default; outside Development a real origin MUST be configured and localhost is
// rejected, so a permissive dev CORS policy can never leak into a deployment.
var originsCfg = builder.Configuration["AllowedOrigins"];
if (string.IsNullOrWhiteSpace(originsCfg))
    originsCfg = isDev
        ? "http://localhost:3000,http://localhost:3100"
        : throw new InvalidOperationException(
            "AllowedOrigins must be configured outside Development (comma-separated web origins, e.g. https://app.example.com).");
var allowedOrigins = originsCfg
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (!isDev && allowedOrigins.Any(o =>
        o.Contains("localhost", StringComparison.OrdinalIgnoreCase) || o.Contains("127.0.0.1")))
    throw new InvalidOperationException(
        "AllowedOrigins must not include localhost/127.0.0.1 outside Development.");
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins)
     .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// ── Rate limiting on the login endpoints ────────────────────────────────────────
// Throttle credential-guessing against /api/auth/login and /api/auth/platform-login
// (the latter is an especially high-value, unauthenticated SuperAdmin target). Fixed
// window per client, keyed on the forwarded client IP (behind Caddy, X-Forwarded-For
// carries the real address) so one abusive source can't lock everyone out. Limits are
// configurable; the defaults allow normal human retries but stop a brute-force burst.
var loginPermit = builder.Configuration.GetValue<int?>("RateLimiting:Login:PermitLimit") ?? 10;
var loginWindow = builder.Configuration.GetValue<int?>("RateLimiting:Login:WindowSeconds") ?? 60;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx =>
    {
        var ip = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(ip)) ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = loginPermit,
            Window      = TimeSpan.FromSeconds(loginWindow),
            QueueLimit  = 0,
        });
    });
});

var app = builder.Build();

// ── Apply migrations + seed demo data on startup ──────────────────────────────
await DbInitializer.RunAsync(app.Services);

// ── Pipeline ──────────────────────────────────────────────────────────────────
// First in the pipeline: turn any unhandled exception into an RFC-7807 ProblemDetails
// response (with a traceId) instead of a bare, body-less 500. Active in every
// environment so production failures are diagnosable, not silent.
app.UseExceptionHandler();

// One structured log line per request (method, path, status, elapsed ms) — so a
// failing request is findable in the logs by its trace id.
app.UseSerilogRequestLogging();

// Serve the OpenAPI JSON document in EVERY environment — the generated TS client
// reads it, and CI gates on it (a runtime-vs-checked-in diff fails the build). The
// interactive UI stays Dev-only; production gets no /swagger HTML page.
app.UseSwagger();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
}

app.UseCors();
app.UseRateLimiter();

// Authentication populates ctx.User so the tenant middleware can read the
// tenant_slug claim; tenant resolution then runs before any endpoint/DbContext.
app.UseAuthentication();
app.UseTenantResolution();
app.UseAuthorization();

// ── Hangfire dashboard (operator UI for the job queue) ───────────────────────
// Mounted at /hangfire when Hangfire is enabled. Auth is HTTP Basic via
// HangfireDashboardAuth: localhost in Development is allowed for convenience,
// every other request needs a configured operator credential. The dashboard is
// the only entry point — there is no other UI exposing job internals.
if (hangfireEnabled)
{
    app.UseHangfireDashboard("/hangfire", new Hangfire.DashboardOptions
    {
        Authorization = new[] { app.Services.GetRequiredService<BidBuilder.Api.Auth.HangfireDashboardAuth>() },
        DashboardTitle = "BidBuilder · Jobs",
        IsReadOnlyFunc = _ => false,
    });
}

// Liveness: the process is up and serving — NO dependency checks, so a DB outage
// never trips it (the container healthcheck targets this).
app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
// Readiness: the app can actually serve traffic — includes the DB probe.
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

// Prometheus scrape (19.3). Gated by TenantAdmin: the metrics surface includes
// per-tenant labels (publish counts, cascade activity) and per-endpoint latency
// histograms — useful operational data but not for anonymous consumption. A
// Prometheus server pulling this endpoint must present a TenantAdmin bearer
// (typically a long-lived service-account token), the same trust boundary the
// Hangfire dashboard sits behind.
if (otelEnabled)
{
    app.MapPrometheusScrapingEndpoint("/metrics")
       .RequireAuthorization(p => p.RequireRole("TenantAdmin", "SuperAdmin"));
}

// Authenticated-only diagnostic. /healthz (above) is the anonymous liveness probe;
// ping requires auth so it can't be used to enumerate tenant slugs while anonymous.
app.MapGet("/api/ping", (ITenantContext t) =>
    Results.Ok(new { ok = true, tenant = t.TenantSlug, at = DateTime.UtcNow }))
   .RequireAuthorization();

app.MapAuthEndpoints();
app.MapSsoEndpoints();
app.MapProjectEndpoints();
app.MapResourceEndpoints();
app.MapQuoteEndpoints();
app.MapAssemblyEndpoints();
app.MapEstimateEndpoints();
app.MapExportEndpoints();
app.MapSettingsEndpoints();
app.MapAuditEndpoints();
app.MapNotificationEndpoints();
app.MapSearchEndpoints();
app.MapWebhookEndpoints();
app.MapSubcontractorQuoteEndpoints();
app.MapCostComponentEndpoints();
app.MapAreaEndpoints();
app.MapActivityEndpoints();
app.MapAiEndpoints();
app.MapProjectTypeEndpoints();
app.MapUserManagementEndpoints();
app.MapBenchmarkEndpoints();
app.MapAnalyticsEndpoints();
app.MapPlatformEndpoints();

app.Run();

// Exposed so the integration test host (WebApplicationFactory<Program>) can boot the API in-process.
public partial class Program;
