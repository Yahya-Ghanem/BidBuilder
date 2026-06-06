# Observability — traces, metrics, alerting

Phase 19.3. OpenTelemetry is wired into the API for both traces and metrics,
with two parallel egress paths: an OTLP exporter for an external collector
(Grafana/Tempo + Prometheus + Loki, Honeycomb, hosted APM, etc.) and a
Prometheus scrape endpoint that an in-cluster Prometheus can pull directly.

## What is instrumented

| Surface | Source | Notes |
|---|---|---|
| HTTP server requests | `OpenTelemetry.Instrumentation.AspNetCore` | `/healthz` and `/readyz` filtered out (probe noise). |
| Outbound HTTP | `OpenTelemetry.Instrumentation.Http` | Currency-FX refresh, any future HttpClient calls. |
| EF Core / Npgsql | `OpenTelemetry.Instrumentation.EntityFrameworkCore` | `SetDbStatementForText=false` — parameter values stay out of traces. |
| Cascade job (Hangfire) | `BidBuilder.Api` ActivitySource | One `cascade.run` span per job, tagged with tenant/resource. |
| Domain counters | `BidBuilder.Api` Meter | See table below. |

## Domain metrics

| Metric | Type | Tags | Why |
|---|---|---|---|
| `bidbuilder.estimates.published` | counter | `tenant_slug` | Per-tenant publish cadence. Sudden drop → users blocked. |
| `bidbuilder.cascade.runs` | counter | `resource.type` | Async fan-out throughput. |
| `bidbuilder.cascade.failures` | counter | `resource.type` | **Alert on this.** Sustained non-zero ⇒ stuck queue or bad data. |

Auto-instrumentation adds the usual ASP.NET Core suite
(`http.server.request.duration`, `http.server.active_requests`,
`kestrel.active_connections`, …).

## Configuration

| Key (also OTEL_* env) | Default | Purpose |
|---|---|---|
| `OpenTelemetry:Enabled` | `true` | Master switch. Test fixture currently leaves on. |
| `OpenTelemetry:Otlp:Endpoint` / `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | When set, traces+metrics export OTLP gRPC to that endpoint. |
| `OpenTelemetry:ServiceName` | `bidbuilder-api` | Resource attribute `service.name`. |

Without an OTLP endpoint configured, telemetry is still recorded in-process,
which is what powers `/metrics` and the in-process MeterListener in tests.

## /metrics — Prometheus scrape

Mounted at `/metrics` and gated by `TenantAdmin` or `SuperAdmin` role. A
Prometheus server scraping this surface must authenticate with a bearer token
for an operator account — the same trust boundary as the Hangfire dashboard.

```yaml
# Prometheus scrape config
- job_name: bidbuilder-api
  scheme: https
  bearer_token: <operator-service-account-jwt>
  static_configs:
    - targets: ["api.bidbuilder.example:443"]
  metrics_path: /metrics
```

## Recommended alerts

Set these on your Prometheus / Alertmanager (or equivalent):

1. **Cascade failure burn** — `rate(bidbuilder_cascade_failures_total[5m]) > 0`
   for 10m. The async fan-out should virtually never fail; sustained errors
   indicate a stuck queue or bad input.
2. **5xx burn** — `sum(rate(http_server_request_duration_count{http_response_status_code=~"5.."}[5m])) > 0`
   for 5m. ASP.NET Core auto-instrumentation tags the status code.
3. **Readiness flap** — external probe of `/readyz` returning non-200 more
   than 3 times in 15m. Catches DB outages the container healthcheck
   (`/healthz`, liveness only) intentionally ignores.
4. **Hangfire backlog** — Hangfire Prometheus exporter (not yet wired here)
   would expose enqueued vs. processed depth. Until then, use the dashboard.
5. **Backup health** — covered separately in `BACKUP.md`; ensure the cron
   wrapper still pages on a missing backup file.

## Tracing in practice

A failing request can be followed in one trace from HTTP entry through every
EF query down to the cascade Hangfire job (if the request enqueued one).
Search by `tenant.id` to scope a tenant-specific incident; the unhandled-error
ProblemDetails response also includes the trace id so a bug report from a user
can be correlated directly.
