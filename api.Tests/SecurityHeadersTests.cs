using System.Net;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 29.A.4 — Lock in the security headers the middleware writes on EVERY
/// response. ApiFixture boots the host as Production, so these are the
/// exact headers a real customer's browser would receive.
///
/// The point of the test is regression: someone reorders the pipeline, the
/// header disappears silently in 200s, and the next pen-test catches what
/// CI should have. These assertions prove the headers are STILL there.
/// </summary>
[Collection("api")]
public class SecurityHeadersTests(ApiFixture fx)
{
    [Fact]
    public async Task Healthz_carries_every_security_header()
    {
        // /healthz is the most boring response we serve — short-circuits well
        // before any endpoint or auth. If the headers ride on this, the
        // middleware truly runs on EVERY response (not just JSON endpoints).
        var resp = await fx.Client().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal("DENY", resp.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin",
            resp.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("camera=()", resp.Headers.GetValues("Permissions-Policy").Single());
        Assert.Contains("microphone=()", resp.Headers.GetValues("Permissions-Policy").Single());

        var hsts = resp.Headers.GetValues("Strict-Transport-Security").Single();
        Assert.Contains("max-age=31536000", hsts);
        Assert.Contains("includeSubDomains", hsts);
        Assert.Contains("preload", hsts);

        // JSON/health paths get the strict CSP: nothing loads, no framing.
        var csp = resp.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    [Fact]
    public async Task Api_endpoint_carries_strict_csp()
    {
        // /api/ping returns JSON — exact same CSP applies. Belt-and-braces vs
        // the /healthz test: pins that the path classification is by-prefix,
        // not by endpoint identity.
        var resp = await fx.Client().GetAsync("/api/ping");
        // Unauthenticated → 401, headers MUST still be present (error 4xx is
        // exactly where missing security headers tend to leak through).
        Assert.True(resp.Headers.Contains("Content-Security-Policy"));
        Assert.True(resp.Headers.Contains("X-Frame-Options"));
        Assert.Contains("default-src 'none'",
            resp.Headers.GetValues("Content-Security-Policy").Single());
    }
}
