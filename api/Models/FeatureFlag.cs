namespace BidBuilder.Api.Models;

/// <summary>
/// 29.B.1 — A platform-defined feature flag with optional per-tenant overrides.
/// </summary>
/// <remarks>
/// <para>
/// Flags live at the platform tier (not <see cref="IHasTenant"/>) because the
/// catalogue is the same for every tenant. A flag describes one toggleable
/// surface — typically a recently-shipped UI affordance behind a kill switch
/// so we can roll it back per-tenant without a redeploy.
/// </para>
/// <para>
/// Resolution for a given tenant (computed by <c>FeatureService</c>):
/// <list type="number">
///   <item><description>If <see cref="Overrides"/> has an entry for the
///     tenant slug, that wins (force on/off — per-tenant kill switch).</description></item>
///   <item><description>Else if <see cref="Enabled"/> is false, the answer is
///     false (master kill switch).</description></item>
///   <item><description>Else if <see cref="RolloutPercentage"/> ≥ 100, true.</description></item>
///   <item><description>Else a stable hash of <c>(tenantId + key)</c> mod 100 is
///     compared against <see cref="RolloutPercentage"/> — same tenant always gets
///     the same answer for the same percentage, so a tenant doesn't see the
///     feature flip back and forth between requests.</description></item>
/// </list>
/// </para>
/// </remarks>
public class FeatureFlag
{
    public int Id { get; set; }

    /// <summary>
    /// Stable identifier the application code references (e.g. <c>bid-letter-templates</c>).
    /// Unique across all flags. kebab-case by convention so it reads cleanly in
    /// URLs (admin endpoint takes the key as a path segment).
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// One-line human description shown in the admin UI. Lets an operator decide
    /// whether to flip a flag without having to dig into the codebase.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Master switch: when false, no tenant gets the feature regardless of
    /// rollout % or override (other than an explicit override entry, which still
    /// wins — that is the "force-on for one debug tenant" escape hatch).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 0–100. The percentage of tenants who get the feature when no override
    /// applies. Stored as int; the resolver uses a deterministic hash so the
    /// assignment is stable across requests for the same tenant.
    /// </summary>
    public int RolloutPercentage { get; set; } = 100;

    /// <summary>
    /// Per-tenant overrides keyed by tenant SLUG (not id), so a fresh dev clone
    /// of production data keeps the same overrides without remapping. Value is
    /// the resolved bool — true forces on, false forces off, missing key means
    /// "fall through to Enabled / RolloutPercentage".
    /// </summary>
    public Dictionary<string, bool> Overrides { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
