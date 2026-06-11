using System.Net;
using System.Net.Http.Json;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 29.B.1 — Cover three load-bearing claims about feature flags:
///   1. The resolver respects override → kill switch → rollout, in that order.
///   2. <c>GET /api/me/features</c> hides Disabled flags from the tenant (the
///      "kill switch flips the SPA" path the spec calls out).
///   3. Only SuperAdmin can edit the catalogue; a TenantAdmin gets 403.
///
/// The resolver is exercised in-process (no HTTP) to make the percentage-rollout
/// distribution assertion deterministic and fast.
/// </summary>
[Collection("api")]
public class FeatureFlagsTests(ApiFixture fx)
{
    // ── 1. Resolver — pure-function behaviour ───────────────────────────────
    [Fact]
    public void Override_force_on_beats_master_kill_switch()
    {
        var f = new FeatureFlag { Key = "x", Enabled = false, RolloutPercentage = 100, Overrides = new() { ["acme"] = true } };
        Assert.True(FeatureService.ResolveFor(f, Guid.NewGuid(), "acme"));
    }

    [Fact]
    public void Override_force_off_beats_full_rollout()
    {
        var f = new FeatureFlag { Key = "x", Enabled = true, RolloutPercentage = 100, Overrides = new() { ["acme"] = false } };
        Assert.False(FeatureService.ResolveFor(f, Guid.NewGuid(), "acme"));
    }

    [Fact]
    public void Kill_switch_overrides_any_nonzero_rollout()
    {
        var f = new FeatureFlag { Key = "x", Enabled = false, RolloutPercentage = 50 };
        // No matter the tenant, Enabled=false means false.
        for (int i = 0; i < 50; i++) Assert.False(FeatureService.ResolveFor(f, Guid.NewGuid(), $"t{i}"));
    }

    [Fact]
    public void Rollout_is_deterministic_per_tenant()
    {
        var f = new FeatureFlag { Key = "experiment", Enabled = true, RolloutPercentage = 50 };
        var tenantId = Guid.NewGuid();
        var a = FeatureService.ResolveFor(f, tenantId, "acme");
        var b = FeatureService.ResolveFor(f, tenantId, "acme");
        Assert.Equal(a, b);  // same input ⇒ same answer, ALWAYS
    }

    [Fact]
    public void Rollout_distribution_is_roughly_uniform()
    {
        var f = new FeatureFlag { Key = "experiment", Enabled = true, RolloutPercentage = 50 };
        int on = 0;
        // 2 000 random tenant ids — a uniform hash bucket should land ~50% inside
        // a reasonable tolerance (±5%). If this fails, the hash isn't uniform
        // and the rollout % no longer means what operators think it means.
        for (int i = 0; i < 2000; i++)
            if (FeatureService.ResolveFor(f, Guid.NewGuid(), $"t{i}")) on++;
        var ratio = on / 2000.0;
        Assert.InRange(ratio, 0.45, 0.55);
    }

    // ── 2. /api/me/features — the SPA's read path ───────────────────────────
    [Fact]
    public async Task Me_features_returns_resolved_map_for_tenant()
    {
        var c = await fx.AdminClientAsync();
        var resp = await c.GetAsync("/api/me/features");
        resp.EnsureSuccessStatusCode();
        var map = await resp.Content.ReadFromJsonAsync<Dictionary<string, bool>>();
        Assert.NotNull(map);
        // The seed installs three flags all defaulted to enabled+100% rollout.
        Assert.True(map!["export-preview"]);
        Assert.True(map["anomaly-panel"]);
        Assert.True(map["bid-letter-templates"]);
    }

    // ── 3. Admin authorization ──────────────────────────────────────────────
    [Fact]
    public async Task TenantAdmin_cannot_edit_a_flag()
    {
        var c = await fx.AdminClientAsync();
        var resp = await c.PutAsJsonAsync("/api/platform/feature-flags/export-preview", new { Enabled = false });
        // The role filter on the admin group runs after auth — 403, not 401.
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_kill_switch_flips_me_features()
    {
        var super = await fx.PlatformAdminClientAsync();
        var tenant = await fx.AdminClientAsync();

        try
        {
            // Flip the master switch off via the admin endpoint.
            var put = await super.PutAsJsonAsync("/api/platform/feature-flags/export-preview", new { Enabled = false });
            put.EnsureSuccessStatusCode();

            // Tenant view of the feature catalogue should reflect the kill.
            var map = await tenant.GetFromJsonAsync<Dictionary<string, bool>>("/api/me/features");
            Assert.False(map!["export-preview"], "kill switch should be visible to tenant via /api/me/features");
        }
        finally
        {
            // Restore — these tests share the host with the rest of the suite.
            var restore = await super.PutAsJsonAsync("/api/platform/feature-flags/export-preview", new { Enabled = true });
            restore.EnsureSuccessStatusCode();
        }
    }
}
