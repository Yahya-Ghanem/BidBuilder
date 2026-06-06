using BidBuilder.Api.Models;
using BidBuilder.Api.Services;

namespace BidBuilder.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>
/// 20.10 — Rate suggestion response.
/// </summary>
public record RateSuggestionDto(
    decimal  SuggestedRate,
    string   Confidence,      // "high" | "medium" | "low"
    string   Basis,           // "historical" | "benchmark"
    IReadOnlyList<ComparableRateDto> Comparables);

public record ComparableRateDto(string Name, string Unit, decimal Rate, string Source);

/// <summary>
/// 20.10 — AI-assisted rate suggestion endpoints.
///
/// GET /api/ai/rate-suggestion?name=&amp;unit=&amp;type=
///   Returns a suggested unit rate derived from the tenant's own historical
///   resource library (when available) or from industry benchmark data (fallback).
///   Requires authentication; results are tenant-scoped.
/// </summary>
public static class AiEndpoints
{
    public static WebApplication MapAiEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/ai")
                     .RequireAuthorization()
                     .WithTags("AI");

        grp.MapGet("/rate-suggestion", async (
            string          name,
            string          unit,
            ResourceType    type,
            RateSuggestionService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest("name is required");
            if (string.IsNullOrWhiteSpace(unit))
                return Results.BadRequest("unit is required");

            var suggestion = await svc.SuggestAsync(name.Trim(), unit.Trim(), type, ct);

            var dto = new RateSuggestionDto(
                suggestion.SuggestedRate,
                suggestion.Confidence,
                suggestion.Basis,
                suggestion.Comparables
                    .Select(c => new ComparableRateDto(c.Name, c.Unit, c.Rate, c.Source))
                    .ToList());

            return Results.Ok(dto);
        })
        .WithName("GetRateSuggestion")
        .WithSummary("AI rate suggestion for a resource name and type")
        .WithDescription(
            "Returns a suggested unit rate derived from the tenant's own historical " +
            "resource library when available (confidence: high/medium), or from " +
            "industry benchmark data (confidence: low). Results are always tenant-scoped.")
        .Produces<RateSuggestionDto>(200)
        .ProducesProblem(400)
        .ProducesProblem(401);

        return app;
    }
}
