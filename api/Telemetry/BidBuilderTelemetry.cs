using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BidBuilder.Api.Telemetry;

/// <summary>
/// Central registry for the API's own ActivitySource and Meter (19.3). The
/// names are stable, semver-style identifiers so dashboards and alert rules
/// can pin to them without leaking C# namespaces.
///
/// What lives here:
///   • <see cref="ActivitySource"/> — used to wrap domain-meaningful units of
///     work (currently the cascade job) in their own trace span, on top of the
///     auto-instrumented HTTP/EF spans.
///   • <see cref="Meter"/> + the named <see cref="Counter{T}"/> instruments:
///       - <c>bidbuilder.estimates.published</c> — bumped when an estimate
///         revision transitions to Published; tagged with tenant_slug so
///         dashboards can show per-tenant publish cadence.
///       - <c>bidbuilder.cascade.runs</c> / <c>.failures</c> — observe the
///         async resource→assembly→estimate cascade so a stuck queue or a
///         repeatedly-failing job becomes visible (and alertable) instead of
///         silently rotting in Hangfire.
///
/// Why not raw <c>Activity.Current</c>? Auto-instrumentation already wraps
/// the request and EF calls. These are the events the platform doesn't know
/// about — domain events worth charting on their own.
/// </summary>
public static class BidBuilderTelemetry
{
    /// <summary>Stable name. Subscribe in OTel via AddSource("BidBuilder.Api").</summary>
    public const string SourceName = "BidBuilder.Api";

    /// <summary>Stable name. Subscribe in OTel via AddMeter("BidBuilder.Api").</summary>
    public const string MeterName = "BidBuilder.Api";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(MeterName);

    /// <summary>Counter — incremented exactly once per Draft/Review→Published transition.</summary>
    public static readonly Counter<long> EstimatesPublished =
        Meter.CreateCounter<long>("bidbuilder.estimates.published",
            unit: "{publishes}",
            description: "Estimate revisions transitioning to the Published status.");

    /// <summary>Counter — every cascade-job execution (success or failure).</summary>
    public static readonly Counter<long> CascadeRuns =
        Meter.CreateCounter<long>("bidbuilder.cascade.runs",
            unit: "{runs}",
            description: "Resource-change cascade jobs executed (each recomputes dependent assemblies + estimates).");

    /// <summary>Counter — cascade jobs that threw. Alert on this growing.</summary>
    public static readonly Counter<long> CascadeFailures =
        Meter.CreateCounter<long>("bidbuilder.cascade.failures",
            unit: "{failures}",
            description: "Cascade jobs that threw an exception. Sustained non-zero rate ⇒ investigate.");
}
