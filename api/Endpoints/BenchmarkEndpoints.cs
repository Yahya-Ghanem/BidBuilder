using System.Security.Claims;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

/// <summary>
/// Cross-project cost benchmarking (cost per unit/m² across the projects a user can
/// access). Gated by the <c>reports</c> module View permission; admins bypass.
/// </summary>
public static class BenchmarkEndpoints
{
    public static void MapBenchmarkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/benchmarks", async (ClaimsPrincipal me, PermissionService perm, BenchmarkService svc) =>
        {
            if (!await perm.CanAsync(me, "reports", ModuleAction.View))
                return Results.Json(new { error = "Missing 'View' permission on reports" }, statusCode: 403);
            return Results.Ok(await svc.ComputeAsync(me));
        }).RequireAuthorization();
    }
}
