using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Endpoints;

public record CostComponentTypeDto(int Id, string Code, string Name, string CalcKind, int SortOrder, bool IsActive, bool Builtin);
public record CostComponentTypeInput(string Code, string Name, string CalcKind, int SortOrder, bool IsActive);

/// <summary>
/// Tenant catalog of cost-component types used to build up BOQ item unit rates
/// (Material, Labor, Equipment, Waste, Overheads + custom). Any signed-in user
/// may read (estimators pick types while pricing); only a tenant admin may edit.
/// </summary>
public static class CostComponentEndpoints
{
    public static void MapCostComponentEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/cost-components").RequireAuthorization();

        grp.MapGet("/", async (AppDbContext db) =>
        {
            var types = await db.CostComponentTypes.OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new CostComponentTypeDto(c.Id, c.Code, c.Name, c.CalcKind.ToString(), c.SortOrder, c.IsActive, c.Builtin))
                .ToListAsync();
            return Results.Ok(types);
        });

        grp.MapPost("/", async (CostComponentTypeInput i, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var (code, err) = NormalizeCode(i.Code);
            if (err is not null) return err;
            if (!Enum.TryParse<CostCalcKind>(i.CalcKind, true, out var kind))
                return Results.Json(new { error = "CalcKind must be 'Amount' or 'Percent'." }, statusCode: 400);
            if (string.IsNullOrWhiteSpace(i.Name)) return Results.Json(new { error = "Name is required." }, statusCode: 400);
            if (await db.CostComponentTypes.AnyAsync(c => c.Code == code))
                return Results.Json(new { error = $"A cost type with code '{code}' already exists." }, statusCode: 409);

            var t = new CostComponentType { Code = code, Name = i.Name.Trim(), CalcKind = kind, SortOrder = i.SortOrder, IsActive = i.IsActive, Builtin = false };
            db.CostComponentTypes.Add(t);
            await db.SaveChangesAsync();
            return Results.Created($"/api/cost-components/{t.Id}", ToDto(t));
        });

        grp.MapPut("/{id:int}", async (int id, CostComponentTypeInput i, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var t = await db.CostComponentTypes.FirstOrDefaultAsync(c => c.Id == id);
            if (t is null) return Results.NotFound();
            if (!Enum.TryParse<CostCalcKind>(i.CalcKind, true, out var kind))
                return Results.Json(new { error = "CalcKind must be 'Amount' or 'Percent'." }, statusCode: 400);
            if (string.IsNullOrWhiteSpace(i.Name)) return Results.Json(new { error = "Name is required." }, statusCode: 400);

            // Code is immutable; CalcKind is locked on built-in types to protect rate semantics.
            t.Name = i.Name.Trim();
            t.SortOrder = i.SortOrder;
            t.IsActive = i.IsActive;
            if (!t.Builtin) t.CalcKind = kind;
            t.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(t));
        });

        grp.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (!me.IsAdmin()) return Forbid();
            var t = await db.CostComponentTypes.FirstOrDefaultAsync(c => c.Id == id);
            if (t is null) return Results.NotFound();
            if (t.Builtin) return Results.Json(new { error = "Built-in cost types cannot be deleted (deactivate it instead)." }, statusCode: 409);
            var uses = await db.ItemCostComponents.CountAsync(c => c.CostComponentTypeId == id);
            if (uses > 0) return Results.Json(new { error = $"Cannot delete — this cost type is used on {uses} BOQ item(s). Deactivate it instead." }, statusCode: 409);
            db.CostComponentTypes.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static (string code, IResult? err) NormalizeCode(string? raw)
    {
        var code = (raw ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 1 or > 16) return ("", Results.Json(new { error = "Code must be 1–16 characters." }, statusCode: 400));
        return (code, null);
    }

    private static IResult Forbid() => Results.Json(new { error = "Only a tenant admin can manage cost types" }, statusCode: 403);

    private static CostComponentTypeDto ToDto(CostComponentType t) =>
        new(t.Id, t.Code, t.Name, t.CalcKind.ToString(), t.SortOrder, t.IsActive, t.Builtin);
}
