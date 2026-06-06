using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────
/// <summary>A subcontractor RFQ as seen by the estimator. Includes the portal
/// token/link — the estimator owns and re-shares it, so (unlike a webhook secret)
/// it is returned on every read.</summary>
public record SubQuoteDto(
    int Id, int ProjectId, string ProjectCode, string ProjectName,
    string Trade, string Scope, string Currency,
    string ContractorName, string? ContractorEmail,
    DateTime ExpiresAt, bool Expired, string Status,
    decimal? QuotedAmount, string? SubmissionNotes, string? RespondentName,
    DateTime? SubmittedAt, DateTime? DecidedAt,
    string Token, string PortalPath, DateTime CreatedAt);

public record CreateSubQuoteInput(int ProjectId, string? Trade, string? Scope,
    string? ContractorName, string? ContractorEmail, string? Currency, int? ValidDays);

public record SubQuoteDecisionInput(bool Accept);

/// <summary>What an anonymous subcontractor sees at /portal/{token}.</summary>
public record PortalViewDto(string CompanyName, string Trade, string Scope, string Currency,
    string ContractorName, DateTime ExpiresAt, bool Expired, string Status,
    decimal? QuotedAmount, string? SubmissionNotes, string? RespondentName, DateTime? SubmittedAt);

public record PortalSubmitInput(decimal Amount, string? Notes, string? RespondentName);

/// <summary>
/// 20.6 — Subcontractor quote portal.
///
/// Estimator side (<c>/api/subcontractor-quotes</c>, authenticated, gated on the
/// <c>projects</c> module): create an RFQ against a project, list/track responses,
/// accept or decline, and revoke. Creating one mints an unguessable token and a
/// public portal link to hand to the subcontractor.
///
/// Public side (<c>/api/portal/{token}</c>, ANONYMOUS): the subcontractor reads
/// the scope and submits a price with no account. The token is globally unique,
/// so the owning tenant is resolved from the token itself (the route is exempt
/// from <c>TenantResolutionMiddleware</c>, and lookups bypass the query filter).
/// </summary>
public static class SubcontractorQuoteEndpoints
{
    private const string Module = "projects";

    public static void MapSubcontractorQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        MapEstimatorApi(app.MapGroup("/api/subcontractor-quotes").RequireAuthorization());
        MapPublicPortal(app.MapGroup("/api/portal"));
    }

    // ── Estimator-facing API ───────────────────────────────────────────────────
    private static void MapEstimatorApi(RouteGroupBuilder grp)
    {
        // GET list (optionally filtered to one project), newest first.
        grp.MapGet("/", async (int? projectId, ClaimsPrincipal me, AppDbContext db, PermissionService perms) =>
        {
            if (!await perms.CanAsync(me, Module, ModuleAction.View)) return Forbid();
            var q = db.SubcontractorQuotes.Include(s => s.Project).AsNoTracking();
            if (projectId is { } pid) q = q.Where(s => s.ProjectId == pid);
            var rows = await q.OrderByDescending(s => s.Id).ToListAsync();
            return Results.Ok(rows.Select(ToDto).ToList());
        });

        // POST create an RFQ — mints the token + portal link.
        grp.MapPost("/", async (CreateSubQuoteInput input, ClaimsPrincipal me,
            AppDbContext db, PermissionService perms, AuditService audit) =>
        {
            if (!await perms.CanAsync(me, Module, ModuleAction.Add)) return Forbid();

            var trade = input.Trade?.Trim();
            var scope = input.Scope?.Trim();
            var contractor = input.ContractorName?.Trim();
            if (string.IsNullOrWhiteSpace(trade))      return Bad("Trade is required.");
            if (string.IsNullOrWhiteSpace(scope))      return Bad("Scope is required.");
            if (string.IsNullOrWhiteSpace(contractor)) return Bad("Contractor name is required.");

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == input.ProjectId);
            if (project is null) return Results.NotFound(new { error = "Project not found" });

            var days = Math.Clamp(input.ValidDays ?? 30, 1, 365);
            var currency = (input.Currency?.Trim() is { Length: > 0 } c ? c : project.Currency).ToUpperInvariant();

            var quote = new SubcontractorQuote
            {
                ProjectId       = project.Id,
                Token           = NewToken(),
                Trade           = trade!,
                Scope           = scope!,
                Currency        = currency,
                ContractorName  = contractor!,
                ContractorEmail = input.ContractorEmail?.Trim() is { Length: > 0 } e ? e : null,
                ExpiresAt       = DateTime.UtcNow.AddDays(days),
                Status          = SubcontractorQuoteStatus.Pending,
            };
            db.SubcontractorQuotes.Add(quote);
            await db.SaveChangesAsync();
            quote.Project = project;
            await audit.LogAsync(me, "subcontractor-quote.create", "SubcontractorQuote", quote.Id.ToString(),
                $"RFQ to {contractor} for {trade} on {project.Code}");
            return Results.Created($"/api/subcontractor-quotes/{quote.Id}", ToDto(quote));
        });

        // POST decision — accept or decline a SUBMITTED quote.
        grp.MapPost("/{id:int}/decision", async (int id, SubQuoteDecisionInput input, ClaimsPrincipal me,
            AppDbContext db, PermissionService perms, AuditService audit) =>
        {
            if (!await perms.CanAsync(me, Module, ModuleAction.Edit)) return Forbid();
            var quote = await db.SubcontractorQuotes.Include(s => s.Project).FirstOrDefaultAsync(s => s.Id == id);
            if (quote is null) return Results.NotFound(new { error = "Quote not found" });
            if (quote.Status != SubcontractorQuoteStatus.Submitted)
                return Bad("Only a submitted quote can be accepted or declined.");

            quote.Status = input.Accept ? SubcontractorQuoteStatus.Accepted : SubcontractorQuoteStatus.Declined;
            quote.DecidedByUserId = me.Id();
            quote.DecidedAt = DateTime.UtcNow;
            quote.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "subcontractor-quote.decision", "SubcontractorQuote", quote.Id.ToString(),
                $"{quote.Status} — {quote.ContractorName} @ {quote.QuotedAmount} {quote.Currency}");
            return Results.Ok(ToDto(quote));
        });

        // DELETE — revoke the RFQ (kills the portal link; keeps the record).
        grp.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me,
            AppDbContext db, PermissionService perms, AuditService audit) =>
        {
            if (!await perms.CanAsync(me, Module, ModuleAction.Delete)) return Forbid();
            var quote = await db.SubcontractorQuotes.Include(s => s.Project).FirstOrDefaultAsync(s => s.Id == id);
            if (quote is null) return Results.NotFound(new { error = "Quote not found" });
            quote.Status = SubcontractorQuoteStatus.Revoked;
            quote.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "subcontractor-quote.revoke", "SubcontractorQuote", quote.Id.ToString(),
                $"{quote.ContractorName} — {quote.Trade}");
            return Results.Ok(ToDto(quote));
        });
    }

    // ── Public, anonymous portal ────────────────────────────────────────────────
    private static void MapPublicPortal(RouteGroupBuilder grp)
    {
        // GET the RFQ a subcontractor was invited to price.
        grp.MapGet("/{token}", async (string token, AppDbContext db) =>
        {
            var quote = await FindByTokenAsync(db, token);
            if (quote is null) return Results.NotFound(new { error = "This link is not valid." });
            var company = await CompanyNameAsync(db, quote.TenantId);
            return Results.Ok(ToPortalView(quote, company));
        }).AllowAnonymous();

        // POST the subcontractor's price. Allowed once, while Pending and unexpired.
        grp.MapPost("/{token}", async (string token, PortalSubmitInput input,
            AppDbContext db, ITenantContext tenantCtx, AuditService audit, NotificationService notif) =>
        {
            var quote = await FindByTokenAsync(db, token);
            if (quote is null) return Results.NotFound(new { error = "This link is not valid." });

            if (quote.Status == SubcontractorQuoteStatus.Revoked)
                return Bad("This request has been withdrawn.");
            if (quote.Status != SubcontractorQuoteStatus.Pending)
                return Bad("A quote has already been submitted for this request.");
            if (quote.ExpiresAt < DateTime.UtcNow)
                return Bad("This request has expired.");
            if (input.Amount <= 0)
                return Bad("Enter a quote amount greater than zero.");
            var respondent = input.RespondentName?.Trim();
            if (string.IsNullOrWhiteSpace(respondent))
                return Bad("Enter your name.");

            // Anonymous request: the middleware left the tenant unresolved. Adopt the
            // token's tenant so the audit row + notifications are stamped correctly.
            tenantCtx.Set(quote.TenantId, "");

            quote.QuotedAmount    = decimal.Round(input.Amount, 2);
            quote.SubmissionNotes = input.Notes?.Trim() is { Length: > 0 } n ? n : null;
            quote.RespondentName  = respondent;
            quote.Status          = SubcontractorQuoteStatus.Submitted;
            quote.SubmittedAt     = DateTime.UtcNow;
            quote.UpdatedAt       = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await audit.LogAsync(new ClaimsPrincipal(new ClaimsIdentity()),
                "subcontractor-quote.submitted", "SubcontractorQuote", quote.Id.ToString(),
                $"{respondent} quoted {quote.QuotedAmount} {quote.Currency} for {quote.Trade}");

            var admins = await notif.TenantAdminIdsAsync();
            await notif.NotifyAsync(admins, "subcontractor-quote.submitted",
                "Subcontractor quote received",
                $"{respondent} quoted {quote.QuotedAmount} {quote.Currency} for {quote.Trade}.",
                link: "/subcontractor-quotes",
                entityType: "SubcontractorQuote", entityKey: quote.Id.ToString());

            var company = await CompanyNameAsync(db, quote.TenantId);
            return Results.Ok(ToPortalView(quote, company));
        }).AllowAnonymous();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────
    private static Task<SubcontractorQuote?> FindByTokenAsync(AppDbContext db, string token) =>
        string.IsNullOrWhiteSpace(token)
            ? Task.FromResult<SubcontractorQuote?>(null)
            : db.SubcontractorQuotes.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Token == token);

    private static async Task<string> CompanyNameAsync(AppDbContext db, Guid tenantId)
    {
        var tenant = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tenantId);
        return tenant?.Name is { Length: > 0 } name ? name : "BidBuilder";
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static SubQuoteDto ToDto(SubcontractorQuote q) => new(
        q.Id, q.ProjectId, q.Project?.Code ?? "", q.Project?.Name ?? "",
        q.Trade, q.Scope, q.Currency, q.ContractorName, q.ContractorEmail,
        q.ExpiresAt, q.ExpiresAt < DateTime.UtcNow, q.Status.ToString(),
        q.QuotedAmount, q.SubmissionNotes, q.RespondentName,
        q.SubmittedAt, q.DecidedAt,
        q.Token, $"/portal/{q.Token}", q.CreatedAt);

    private static PortalViewDto ToPortalView(SubcontractorQuote q, string company) => new(
        company, q.Trade, q.Scope, q.Currency, q.ContractorName,
        q.ExpiresAt, q.ExpiresAt < DateTime.UtcNow, q.Status.ToString(),
        q.QuotedAmount, q.SubmissionNotes, q.RespondentName, q.SubmittedAt);

    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    private static IResult Forbid() => Results.Json(new { error = "You don't have access to subcontractor quotes." }, statusCode: 403);
}
