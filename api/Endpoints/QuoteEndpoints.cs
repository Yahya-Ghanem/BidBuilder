using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Validation;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────
/// <summary>A quote as seen by the client. <c>IsExpired</c> is server-computed
/// (ValidUntil &lt; today) so the UI doesn't need to roll its own clock.</summary>
public record QuoteDto(
    int Id, string ResourceType, int? ResourceId,
    string Supplier, decimal Price, string Currency, string Unit,
    DateOnly QuotedOn, DateOnly? ValidUntil, bool IsExpired,
    string? Note, string? AttachmentUrl, DateTime UpdatedAt);

public record QuoteInput(
    string ResourceType, int? ResourceId,
    string Supplier, decimal Price, string Currency, string Unit,
    DateOnly QuotedOn, DateOnly? ValidUntil,
    string? Note, string? AttachmentUrl);

/// <summary>
/// CRUD for the supplier-quote register (18.3). Quotes are independent of the
/// resource library's "current" rate — they capture "supplier X offered Y per
/// unit of Z, valid until D" for comparison, audit, and recompute provenance.
/// All routes are guarded by the <c>resource-library</c> module permission, since
/// quotes feed the same procurement workflow as resource rates.
/// </summary>
public static class QuoteEndpoints
{
    private const string Mod = "resource-library";

    public static void MapQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/quotes").RequireAuthorization();

        grp.MapGet("/", async (ClaimsPrincipal me, PermissionService perm, AppDbContext db, string? type, int? resourceId) =>
        {
            var g = await Guard(me, perm, ModuleAction.View); if (g is not null) return g;
            var q = db.SupplierQuotes.AsQueryable();
            if (!string.IsNullOrWhiteSpace(type) && ResourceEndpoints.TryParseType(type!, out var rt))
                q = q.Where(x => x.ResourceType == rt);
            if (resourceId is { } rid) q = q.Where(x => x.ResourceId == rid);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var rows = await q.OrderByDescending(x => x.QuotedOn).ThenByDescending(x => x.Id)
                .Select(x => new QuoteDto(
                    x.Id, x.ResourceType.ToString(), x.ResourceId,
                    x.Supplier, x.Price, x.Currency, x.Unit,
                    x.QuotedOn, x.ValidUntil, x.ValidUntil != null && x.ValidUntil < today,
                    x.Note, x.AttachmentUrl, x.UpdatedAt))
                .ToListAsync();
            return Results.Ok(rows);
        });

        grp.MapPost("/", async (QuoteInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Add); if (g is not null) return g;
            if (!ResourceEndpoints.TryParseType(i.ResourceType, out var rt))
                return Results.Json(new { error = $"Unknown resource type '{i.ResourceType}'." }, statusCode: 400);
            if (i.Price < 0) return Results.Json(new { error = "Price cannot be negative." }, statusCode: 400);
            if (string.IsNullOrWhiteSpace(i.Supplier))
                return Results.Json(new { error = "Supplier is required." }, statusCode: 400);
            if (i.ValidUntil is { } v && v < i.QuotedOn)
                return Results.Json(new { error = "ValidUntil cannot be before QuotedOn." }, statusCode: 400);

            var q = new SupplierQuote
            {
                ResourceType = rt,
                ResourceId = i.ResourceId,
                Supplier = i.Supplier.Trim(),
                Price = i.Price,
                Currency = (i.Currency ?? "").Trim().ToUpperInvariant(),
                Unit = (i.Unit ?? "").Trim(),
                QuotedOn = i.QuotedOn,
                ValidUntil = i.ValidUntil,
                Note = i.Note?.Trim(),
                AttachmentUrl = i.AttachmentUrl?.Trim(),
            };
            db.SupplierQuotes.Add(q);
            await db.SaveChangesAsync();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return Results.Created($"/api/quotes/{q.Id}", new QuoteDto(
                q.Id, q.ResourceType.ToString(), q.ResourceId,
                q.Supplier, q.Price, q.Currency, q.Unit,
                q.QuotedOn, q.ValidUntil, q.ValidUntil != null && q.ValidUntil < today,
                q.Note, q.AttachmentUrl, q.UpdatedAt));
        }).AddEndpointFilter<ValidationFilter<QuoteInput>>();

        grp.MapPut("/{id:int}", async (int id, QuoteInput i, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Edit); if (g is not null) return g;
            var q = await db.SupplierQuotes.FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound(new { error = "Quote not found" });
            if (!ResourceEndpoints.TryParseType(i.ResourceType, out var rt))
                return Results.Json(new { error = $"Unknown resource type '{i.ResourceType}'." }, statusCode: 400);
            if (i.Price < 0) return Results.Json(new { error = "Price cannot be negative." }, statusCode: 400);
            if (i.ValidUntil is { } v && v < i.QuotedOn)
                return Results.Json(new { error = "ValidUntil cannot be before QuotedOn." }, statusCode: 400);

            q.ResourceType = rt;
            q.ResourceId = i.ResourceId;
            q.Supplier = i.Supplier.Trim();
            q.Price = i.Price;
            q.Currency = (i.Currency ?? "").Trim().ToUpperInvariant();
            q.Unit = (i.Unit ?? "").Trim();
            q.QuotedOn = i.QuotedOn;
            q.ValidUntil = i.ValidUntil;
            q.Note = i.Note?.Trim();
            q.AttachmentUrl = i.AttachmentUrl?.Trim();
            q.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return Results.Ok(new QuoteDto(
                q.Id, q.ResourceType.ToString(), q.ResourceId,
                q.Supplier, q.Price, q.Currency, q.Unit,
                q.QuotedOn, q.ValidUntil, q.ValidUntil != null && q.ValidUntil < today,
                q.Note, q.AttachmentUrl, q.UpdatedAt));
        }).AddEndpointFilter<ValidationFilter<QuoteInput>>();

        grp.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, PermissionService perm, AppDbContext db) =>
        {
            var g = await Guard(me, perm, ModuleAction.Delete); if (g is not null) return g;
            var q = await db.SupplierQuotes.FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound(new { error = "Quote not found" });
            db.SupplierQuotes.Remove(q);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task<IResult?> Guard(ClaimsPrincipal me, PermissionService perm, ModuleAction action) =>
        await perm.CanAsync(me, Mod, action)
            ? null
            : Results.Json(new { error = $"Missing '{action}' permission on {Mod}" }, statusCode: 403);
}
