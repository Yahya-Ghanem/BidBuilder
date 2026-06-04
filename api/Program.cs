using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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

// ── Services ──────────────────────────────────────────────────────────────────
// Outside Development the connection string MUST be supplied (no insecure
// localhost/postgres fallback leaking into a real deployment).
var connString = builder.Configuration.GetConnectionString("Postgres")
                 ?? (isDev ? "Host=localhost;Database=bidbuilder;Username=postgres;Password=postgres" : null)
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:Postgres must be configured outside Development (e.g. ConnectionStrings__Postgres).");

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connString));
builder.Services.AddScoped<ITenantContext, TenantContext>();
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

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

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

app.MapHealthChecks("/healthz");

app.MapGet("/api/ping", (ITenantContext t) =>
    Results.Ok(new { ok = true, tenant = t.TenantSlug, at = DateTime.UtcNow }));

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
app.MapUserManagementEndpoints();

app.Run();

// Exposed so the integration test host (WebApplicationFactory<Program>) can boot the API in-process.
public partial class Program;
