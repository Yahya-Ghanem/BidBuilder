using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Services;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.9 — Tests for outbound webhooks (management + dispatch). Delivery is
/// captured by <see cref="ApiFixture.Webhooks"/> (the real HTTP sender is
/// replaced), so each test uses a unique URL to find its own deliveries.
///
/// Coverage:
///   • Create returns the secret once; the list never echoes it.
///   • Non-admins can't manage webhooks (403).
///   • Ping delivers a signed "ping" event carrying the subscription's secret.
///   • Publishing an estimate dispatches estimate.published to a subscriber.
///   • The event filter excludes events a subscription didn't subscribe to.
/// </summary>
[Collection("api")]
public class WebhookTests(ApiFixture fx)
{
    static string UniqueUrl() => $"https://hooks.test/{Guid.NewGuid():N}";

    static Task<HttpResponseMessage> ClearApprovalRequirement(HttpClient admin) =>
        admin.PutAsJsonAsync("/api/settings/", new
        {
            timezone = "UTC", baseCurrency = "AED",
            defaultOverheadPct = 0m, defaultProfitPct = 0m,
            defaultContingencyPct = 0m, defaultTaxRatePct = 0m,
            requiredApprovalsToPublish = 0,
        });

    static async Task<(int Id, string Secret)> CreateAsync(HttpClient admin, string url, params string[] events)
    {
        var body = await (await admin.PostAsJsonAsync("/api/webhooks", new { url, events })).Json();
        return (body.GetProperty("id").GetInt32(), body.GetProperty("secret").GetString()!);
    }

    [Fact]
    public async Task Create_returns_secret_once_and_list_never_does()
    {
        var admin = await fx.AdminClientAsync();
        var url = UniqueUrl();
        var (id, secret) = await CreateAsync(admin, url, "estimate.published");
        Assert.False(string.IsNullOrWhiteSpace(secret));

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/webhooks");
        var row = list.EnumerateArray().First(w => w.GetProperty("id").GetInt32() == id);
        Assert.Equal(url, row.GetProperty("url").GetString());
        Assert.False(row.TryGetProperty("secret", out _));   // never echoed back
    }

    [Fact]
    public async Task Non_admin_cannot_manage_webhooks()
    {
        var admin = await fx.AdminClientAsync();
        const string email = "webhook.nonadmin@bidbuilder.local";
        await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Plain", email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() })).Json();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync("/api/webhooks", new { url = UniqueUrl(), events = new[] { "ping" } })).StatusCode);
    }

    [Fact]
    public async Task Ping_delivers_signed_event()
    {
        var admin = await fx.AdminClientAsync();
        var url = UniqueUrl();
        var (id, secret) = await CreateAsync(admin, url, "estimate.published");

        var pong = await (await admin.PostAsync($"/api/webhooks/{id}/ping", null)).Json();
        Assert.True(pong.GetProperty("ok").GetBoolean());
        Assert.Equal("200", pong.GetProperty("status").GetString());

        var sent = ApiFixture.Webhooks.ForUrl(url);
        Assert.Single(sent);
        Assert.Equal(WebhookEvents.Ping, sent[0].EventType);
        Assert.Equal(secret, sent[0].Secret);
        // The signature the receiver would verify is a stable HMAC of the body.
        var sig = HttpWebhookSender.Sign(secret, sent[0].Body);
        Assert.Equal(64, sig.Length);   // hex SHA-256
        Assert.Matches("^[0-9a-f]+$", sig);
    }

    [Fact]
    public async Task Publishing_dispatches_to_subscriber()
    {
        var admin = await fx.AdminClientAsync();
        (await ClearApprovalRequirement(admin)).EnsureSuccessStatusCode();
        var url = UniqueUrl();
        await CreateAsync(admin, url, "estimate.published");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "webhook-publish");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        var sent = ApiFixture.Webhooks.ForUrl(url);
        Assert.Contains(sent, s => s.EventType == WebhookEvents.Published && s.Body.Contains($"\"estimateId\":{eid}"));
    }

    [Fact]
    public async Task Event_filter_excludes_unsubscribed_events()
    {
        var admin = await fx.AdminClientAsync();
        (await ClearApprovalRequirement(admin)).EnsureSuccessStatusCode();
        var url = UniqueUrl();
        // Subscribed ONLY to approvals — a publish must not reach this URL.
        await CreateAsync(admin, url, "estimate.approved");

        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.NewEstimateAsync(admin, pid, "webhook-filter");
        (await Api.PutMetaAsync(admin, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        Assert.DoesNotContain(ApiFixture.Webhooks.ForUrl(url), s => s.EventType == WebhookEvents.Published);
    }
}
