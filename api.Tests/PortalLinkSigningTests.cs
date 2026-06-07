using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 23.3 — HMAC-signed subcontractor portal URLs. Proves the new <c>?exp=&amp;sig=</c>
/// query is enforced (valid passes, tampered fails, expired fails), that the
/// signed-link endpoint mints them with a chosen expiry, that the email body now
/// emits a signed URL, and that the back-compat path (unsigned URLs) still works
/// while <c>PortalLinks:AllowUnsigned</c> defaults to true.
/// </summary>
[Collection("api")]
public class PortalLinkSigningTests(ApiFixture fx)
{
    static CapturingEmailSender Mail => ApiFixture.Emails;

    private static async Task<(int id, string token)> CreateInviteAsync(HttpClient admin, int pid, string? contractorEmail = null)
    {
        var r = await admin.PostAsJsonAsync("/api/subcontractor-quotes", new
        {
            projectId = pid,
            trade = "Concrete works",
            scope = "Supply, place and finish 500 m³ of C40 concrete to raft foundation.",
            contractorName = "Acme Concrete LLC",
            contractorEmail,
            currency = "AED",
            validDays = 30,
        });
        r.EnsureSuccessStatusCode();
        var dto = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (dto.GetProperty("id").GetInt32(), dto.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task Backcompat_unsigned_url_is_still_accepted_by_default()
    {
        // AllowUnsigned defaults true for 23.3 release → bare-token URLs still work.
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var (_, token) = await CreateInviteAsync(admin, pid);

        var anon = fx.AnonymousClient();
        var resp = await anon.GetAsync($"/api/portal/{token}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Signed_link_endpoint_returns_a_url_that_passes_the_portal_check()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var (id, _) = await CreateInviteAsync(admin, pid);

        var linkResp = await (await admin.GetAsync($"/api/subcontractor-quotes/{id}/signed-link?validDays=7")).Json();
        var portalPath = linkResp.GetProperty("portalPath").GetString()!;
        Assert.Contains("?exp=", portalPath);
        Assert.Contains("&sig=", portalPath);

        // The signed path is a relative URL. Hit it anonymously; should resolve.
        var resp = await fx.AnonymousClient().GetAsync("/api" + portalPath);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task A_tampered_signature_is_rejected_with_401_invalid()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var (id, _) = await CreateInviteAsync(admin, pid);

        var linkResp = await (await admin.GetAsync($"/api/subcontractor-quotes/{id}/signed-link?validDays=7")).Json();
        var portalPath = linkResp.GetProperty("portalPath").GetString()!;
        // Flip a single character in the sig. The constant-time compare must fail.
        var sigIdx = portalPath.IndexOf("sig=") + 4;
        var tampered = portalPath[..sigIdx] + (portalPath[sigIdx] == 'a' ? 'b' : 'a') + portalPath[(sigIdx + 1)..];

        var resp = await fx.AnonymousClient().GetAsync("/api" + tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_expired_signed_url_is_rejected_with_401_expired()
    {
        // Drive the signer directly to mint an URL that's already expired (validDays clamps to
        // >= 1, so we sign manually with a past exp).
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var (_, token) = await CreateInviteAsync(admin, pid);

        using var scope = await fx.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<PortalLinkSigner>();
        var tc = scope.ServiceProvider.GetRequiredService<BidBuilder.Api.Tenancy.ITenantContext>();
        // Re-implement the sig formula with a past exp to simulate "expired link".
        // (PortalLinkSigner only mints future-expiry URLs by design.)
        var pastExp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        // Use the verification API with a synthetic sig + matching exp — easiest path is to
        // sign now at a 1-day expiry and then re-issue the verification with a past exp +
        // its corresponding (different) sig. We instead test the signer's verdict directly.
        var v = await signer.VerifyAsync(tc.TenantId, token, pastExp.ToString(),
            // valid-looking sig structure, but won't match — the signer treats exp-mismatch
            // as an "invalid" verdict before checking the timestamp, so the right way to
            // exercise "expired" is to pass a sig that DOES match the (past) exp.
            "ignored");
        Assert.False(v.Ok);
        // The expiry check runs BEFORE HMAC, so the verdict here is "expired", not "invalid".
        Assert.Equal("expired", v.Reason);

        // And drive the HTTP path with the same shape to prove the endpoint surfaces the
        // 401-expired reason.
        var resp = await fx.AnonymousClient().GetAsync($"/api/portal/{token}?exp={pastExp}&sig=ignored");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("expired", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Half_signed_url_with_only_exp_or_only_sig_is_rejected()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var (_, token) = await CreateInviteAsync(admin, pid);

        // exp without sig
        var r1 = await fx.AnonymousClient().GetAsync($"/api/portal/{token}?exp=9999999999");
        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        // sig without exp
        var r2 = await fx.AnonymousClient().GetAsync($"/api/portal/{token}?sig=anything");
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
    }

    [Fact]
    public async Task Create_email_carries_a_signed_portal_link()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var contractorEmail = $"signed.{Guid.NewGuid():N}@bidbuilder.test";
        var (_, _) = await CreateInviteAsync(admin, pid, contractorEmail);

        var got = Mail.To(contractorEmail);
        Assert.NotEmpty(got);
        // The mailed body should contain the signed URL shape, not just /portal/{token}.
        Assert.Contains(got, m => m.Body.Contains("?exp=") && m.Body.Contains("&sig="));
    }

    [Fact]
    public async Task Signed_submit_post_works_and_validates_sig()
    {
        // End-to-end: GET signed link → POST submission to it → accepted with status Submitted.
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var (id, _) = await CreateInviteAsync(admin, pid);

        var linkResp = await (await admin.GetAsync($"/api/subcontractor-quotes/{id}/signed-link?validDays=14")).Json();
        var portalPath = linkResp.GetProperty("portalPath").GetString()!;

        var anon = fx.AnonymousClient();
        var submit = await anon.PostAsJsonAsync("/api" + portalPath,
            new { amount = 250000m, notes = "rev 1", respondentName = "Pat Bidder" });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var view = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Submitted", view.GetProperty("status").GetString());
    }
}

/// <summary>23.3 — Pure unit tests for PortalLinkSigner.VerifyAsync's
/// missing/expired/invalid arms (the simple state machine the endpoint maps to 401).
/// Shares the "api" collection's fixture so both classes use one host (avoids the
/// dual-fixture race that surfaces when IClassFixture creates a parallel host).</summary>
[Collection("api")]
public class PortalLinkSignerUnitTests(ApiFixture fx)
{
    [Fact]
    public async Task Verify_with_neither_exp_nor_sig_is_ok_under_default_allowunsigned()
    {
        using var scope = await fx.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<PortalLinkSigner>();
        var tc = scope.ServiceProvider.GetRequiredService<BidBuilder.Api.Tenancy.ITenantContext>();
        var v = await signer.VerifyAsync(tc.TenantId, "anytoken", null, null);
        Assert.True(v.Ok);   // back-compat
    }

    [Fact]
    public async Task Verify_with_invalid_exp_returns_invalid()
    {
        using var scope = await fx.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<PortalLinkSigner>();
        var tc = scope.ServiceProvider.GetRequiredService<BidBuilder.Api.Tenancy.ITenantContext>();
        var v = await signer.VerifyAsync(tc.TenantId, "anytoken", "not-a-number", "anysig");
        Assert.False(v.Ok);
        Assert.Equal("invalid", v.Reason);
    }

    [Fact]
    public async Task Sign_and_verify_roundtrips()
    {
        using var scope = await fx.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<PortalLinkSigner>();
        var tc = scope.ServiceProvider.GetRequiredService<BidBuilder.Api.Tenancy.ITenantContext>();
        var path = await signer.SignAsync(tc.TenantId, "tok-abc", validDays: 1);
        // Tease out exp + sig from the path.
        var q = path[(path.IndexOf('?') + 1)..].Split('&').ToDictionary(p => p.Split('=')[0], p => p.Split('=')[1]);
        var v = await signer.VerifyAsync(tc.TenantId, "tok-abc", q["exp"], q["sig"]);
        Assert.True(v.Ok);
    }
}
