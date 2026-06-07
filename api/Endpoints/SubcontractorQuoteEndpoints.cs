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

    /// <summary>23.3 — Lightweight DTO returned by the new signed-link endpoint.</summary>
    public record SignedLinkDto(string PortalPath, DateTime ExpiresAt);

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
            AppDbContext db, PermissionService perms, AuditService audit,
            EmailService email, ITenantContext tc, PortalLinkSigner signer) =>
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

            // 21.1 — email the portal link to the subcontractor when an address was given
            // and the platform SMTP transport + tenant toggle allow it. Best-effort: a
            // mail failure never fails the RFQ (the estimator can still copy the link).
            if (quote.ContractorEmail is { Length: > 0 } toEmail)
            {
                var company = await CompanyNameAsync(db, tc.TenantId);
                // 23.3 — Email a SIGNED portal URL (default expiry) so the link the
                // subcontractor receives carries the HMAC + expiry from day one.
                var signedPath = await signer.SignAsync(tc.TenantId, quote.Token, validDays: null);
                var link = email.AbsoluteLink(signedPath);
                var body =
                    $"{company} has invited you to submit a quote for \"{trade}\" on project {project.Code} — {project.Name}.\n\n" +
                    $"Scope:\n{scope}\n\n" +
                    (link is null ? "" : $"Submit your price here:\n{link}\n\n") +
                    $"This request expires on {quote.ExpiresAt:yyyy-MM-dd} (UTC).";
                await email.SendAsync(toEmail, contractor, $"Quote request: {trade} — {company}", body);
            }
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

        // 23.3 — Mint a freshly signed portal link with a caller-chosen expiry (default 7 days,
        // clamped to [1, 365]). The estimator UI calls this to copy a time-bounded URL.
        grp.MapGet("/{id:int}/signed-link", async (int id, int? validDays, ClaimsPrincipal me,
            AppDbContext db, PermissionService perms, PortalLinkSigner signer, ITenantContext tc) =>
        {
            if (!await perms.CanAsync(me, Module, ModuleAction.View)) return Forbid();
            var quote = await db.SubcontractorQuotes.FirstOrDefaultAsync(s => s.Id == id);
            if (quote is null) return Results.NotFound(new { error = "Quote not found" });
            var path = await signer.SignAsync(tc.TenantId, quote.Token, validDays);
            var days = Math.Clamp(validDays ?? PortalLinkSigner.DefaultValidDays, 1, 365);
            return Results.Ok(new SignedLinkDto(path, DateTime.UtcNow.AddDays(days)));
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
        grp.MapGet("/{token}", async (string token, string? exp, string? sig,
            AppDbContext db, PortalLinkSigner signer) =>
        {
            var quote = await FindByTokenAsync(db, token);
            if (quote is null) return Results.NotFound(new { error = "This link is not valid." });
            // 23.3 — Validate sig/exp against the tenant's HMAC key. Per the PortalLinkSigner
            // back-compat flag, a bare token (no sig+exp) is still accepted for one release.
            var v = await signer.VerifyAsync(quote.TenantId, token, exp, sig);
            if (!v.Ok) return PortalAuthFailed(v.Reason);
            var company = await CompanyNameAsync(db, quote.TenantId);
            return Results.Ok(ToPortalView(quote, company));
        }).AllowAnonymous();

        // POST the subcontractor's price. Allowed once, while Pending and unexpired.
        grp.MapPost("/{token}", async (string token, string? exp, string? sig, PortalSubmitInput input,
            AppDbContext db, ITenantContext tenantCtx, AuditService audit, NotificationService notif,
            PortalLinkSigner signer) =>
        {
            var quote = await FindByTokenAsync(db, token);
            if (quote is null) return Results.NotFound(new { error = "This link is not valid." });
            // 23.3 — Same sig+exp check on submit, so a leaked signed URL can't be POST'd after expiry.
            var v = await signer.VerifyAsync(quote.TenantId, token, exp, sig);
            if (!v.Ok) return PortalAuthFailed(v.Reason);

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

    /// <summary>23.3 — Map the PortalLinkSigner verdict to a single 401 with a precise reason
    /// (so the public portal page can show "expired" vs "invalid" distinctly). "missing" is
    /// returned when AllowUnsigned is off and no sig/exp was presented at all.</summary>
    private static IResult PortalAuthFailed(string reason)
    {
        var message = reason switch
        {
            "expired"  => "This link has expired. Ask the sender for a fresh one.",
            "invalid"  => "This link is invalid or has been tampered with.",
            "missing"  => "This link is no longer valid. Ask the sender for a fresh one.",
            _          => "This link is no longer valid.",
        };
        return Results.Json(new { error = message, reason }, statusCode: 401);
    }
}
