using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Formatting.Compact;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Endpoints;
using BidBuilder.Api.Tenancy;

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
builder.Services.AddScoped<BidBuilder.Api.Services.AreaRollupService>();
builder.Services.AddScoped<BidBuilder.Api.Services.BenchmarkService>();

// Liveness vs readiness: the DB probe is tagged "ready" so /readyz reflects the
// database while /healthz stays a pure liveness signal (process is up). The
// container healthcheck uses /healthz, so a transient Postgres outage doesn't get
// a perfectly healthy API killed and restarted.
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("db", tags: new[] { "ready" });

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
            RoleClaimType            = "role",
            NameClaimType            = "name",
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// Authentication populates ctx.User so the tenant middleware can read the
// tenant_slug claim; tenant resolution then runs before any endpoint/DbContext.
app.UseAuthentication();
app.UseTenantResolution();
app.UseAuthorization();

// Liveness: the process is up and serving — NO dependency checks, so a DB outage
// never trips it (the container healthcheck targets this).
app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
// Readiness: the app can actually serve traffic — includes the DB probe.
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

// Authenticated-only diagnostic. /healthz (above) is the anonymous liveness probe;
// ping requires auth so it can't be used to enumerate tenant slugs while anonymous.
app.MapGet("/api/ping", (ITenantContext t) =>
    Results.Ok(new { ok = true, tenant = t.TenantSlug, at = DateTime.UtcNow }))
   .RequireAuthorization();

app.MapAuthEndpoints();
app.MapProjectEndpoints();
app.MapResourceEndpoints();
app.MapAssemblyEndpoints();
app.MapEstimateEndpoints();
app.MapExportEndpoints();
app.MapSettingsEndpoints();
app.MapAuditEndpoints();
app.MapCostComponentEndpoints();
app.MapAreaEndpoints();
app.MapActivityEndpoints();
app.MapProjectTypeEndpoints();
app.MapUserManagementEndpoints();
app.MapBenchmarkEndpoints();
app.MapPlatformEndpoints();

app.Run();

// Exposed so the integration test host (WebApplicationFactory<Program>) can boot the API in-process.
public partial class Program;
