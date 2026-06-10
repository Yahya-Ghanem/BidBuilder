using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 29.A.1 — the E2E test-support endpoints (POST/DELETE /api/test/users) are mapped
/// ONLY in Development. This fixture boots the host with UseEnvironment("Production"),
/// so the strongest possible evidence is right here: the routes must not exist at all.
/// A 404 (not 401/403) proves the group was never mapped — there is no handler to
/// guard, no auth to bypass, nothing reachable in a production build.
/// </summary>
[Collection("api")]
public class TestSupportEndpointsTests(ApiFixture fx)
{
    [Fact]
    public async Task Mint_user_endpoint_does_not_exist_outside_Development()
    {
        var c = fx.Client();   // unauthenticated, X-Tenant-Id: default — same shape an E2E helper would send
        var resp = await c.PostAsJsonAsync("/api/test/users", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_user_endpoint_does_not_exist_outside_Development()
    {
        var c = fx.Client();
        var resp = await c.DeleteAsync("/api/test/users/1");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
