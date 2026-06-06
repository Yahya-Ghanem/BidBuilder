using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Telemetry;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 19.3 — OpenTelemetry / Prometheus surface tests. We don't try to assert against
/// the OTLP exporter (it's gated on config and there's no collector in the test
/// harness); instead we prove two things:
///   1. The DOMAIN counter <c>bidbuilder.estimates.published</c> increments exactly
///      when an estimate revision transitions to <c>Published</c>.
///   2. The Prometheus scrape endpoint <c>/metrics</c> is auth-gated (TenantAdmin)
///      and serves the standard Prometheus text format with the runtime/HTTP
///      metrics the auto-instrumentation registers.
/// Together they cover the read end (consumers can scrape) and the write end
/// (domain events actually emit) of the observability pipeline.
/// </summary>
[Collection("api")]
public class TelemetryTests(ApiFixture fx)
{
    [Fact]
    public async Task Publishing_an_estimate_increments_the_published_counter()
    {
        // MeterListener subscribes BEFORE the publish call so we don't miss the increment.
        // Filter to OUR meter only — runtime/http counters use different meters and we don't
        // want to pollute the assertion. We tag-check tenant_slug to prove the dimension
        // dashboards rely on is attached.
        long delta = 0;
        string? observedTenant = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BidBuilderTelemetry.MeterName &&
                    instrument.Name == "bidbuilder.estimates.published")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Interlocked.Add(ref delta, value);
            foreach (var t in tags)
                if (t.Key == "tenant_slug") observedTenant = t.Value?.ToString();
        });
        listener.Start();

        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "telemetry-publish");

        // Draft → Published. The counter should fire exactly once.
        (await Api.PutMetaAsync(c, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        // A second PUT with the same status MUST NOT double-count (newlyPublished gate).
        (await Api.PutMetaAsync(c, eid, new { status = "Published" })).EnsureSuccessStatusCode();

        // Flush — synchronous in MeterListener, but call anyway for clarity.
        listener.RecordObservableInstruments();

        Assert.Equal(1, Interlocked.Read(ref delta));
        Assert.Equal("default", observedTenant);   // seeded tenant slug
    }

    [Fact]
    public async Task Reverting_a_published_estimate_and_republishing_increments_again()
    {
        long delta = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BidBuilderTelemetry.MeterName &&
                    instrument.Name == "bidbuilder.estimates.published")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref delta, value));
        listener.Start();

        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "telemetry-republish");

        (await Api.PutMetaAsync(c, eid, new { status = "Published" })).EnsureSuccessStatusCode();   // +1
        (await Api.PutMetaAsync(c, eid, new { status = "Draft" })).EnsureSuccessStatusCode();      // no fire
        (await Api.PutMetaAsync(c, eid, new { status = "Published" })).EnsureSuccessStatusCode();   // +1

        Assert.Equal(2, Interlocked.Read(ref delta));
    }

    [Fact]
    public async Task Metrics_endpoint_rejects_anonymous_callers()
    {
        // The Prometheus surface includes per-tenant labels and per-endpoint latency
        // histograms — useful operationally, not for the public internet. The endpoint
        // is RequireAuthorization(TenantAdmin/SuperAdmin), so unauthenticated hits 401.
        var r = await fx.Client().GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Metrics_endpoint_serves_prometheus_text_to_an_admin()
    {
        var c = await fx.AdminClientAsync();

        // Generate a little traffic so the auto-instrumented http.server.* metrics
        // have at least one observation to expose.
        await c.GetAsync("/api/ping");

        var r = await c.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // Prometheus content type carries "text/plain; version=0.0.4" (or OpenMetrics).
        var ct = r.Content.Headers.ContentType?.ToString() ?? "";
        Assert.Contains("text/plain", ct, StringComparison.OrdinalIgnoreCase);

        var body = await r.Content.ReadAsStringAsync();
        // The exposition format always opens metric families with "# HELP" / "# TYPE".
        Assert.Contains("# HELP", body);
        Assert.Contains("# TYPE", body);
    }
}
