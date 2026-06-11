# Phase 29 Tier A — Close-out (29.A.5)

**Scope shipped:** 29.A.1, 29.A.2, 29.A.4
**Scope deferred:** 29.A.3 (carried into Tier B backlog)
**Re-grade:** **7.5 → 8.0 / 10**

The Phase 29 audit pointed at six rough edges. Tier A targets the auditable
ones: test isolation, supply-chain hygiene, security headers. Server-synced
user preferences (29.A.3) was a designed Tier A item but did not block the
audited grade outcomes, so it was deferred to Tier B with the user's
explicit approval rather than gating the close-out on a UX-durability
feature.

---

## 1. What shipped

| Item | PR | Merge SHA | Evidence kind |
|------|----|-----------|---------------|
| 29.A.1 Per-spec E2E test users | [#131](https://github.com/Yahya-Ghanem/BidBuilder/pull/131) | `515b160` | Code + integration tests + CI green under strict rate limit |
| 29.A.2 Dependency scanning + SBOM | [#132](https://github.com/Yahya-Ghanem/BidBuilder/pull/132) | `656932e` | Config + 20 CVEs closed in same PR + 13 Dependabot PRs opened automatically within minutes of merge |
| 29.A.4 Security headers + CSP | [#146](https://github.com/Yahya-Ghanem/BidBuilder/pull/146) | `ffe2291` | Code + integration tests + live `curl -I` against rebuilt containers |

## 2. 29.A.1 evidence — shared-admin coupling removed

The CI override that loosened the login rate limit to 200/IP (`f835459`) is
removed at the root, not lowered:

```bash
$ grep -n RateLimiting__Login .github/workflows/ci.yml api/Program.cs
# (no matches — override deleted; default 10/60s/IP applies in CI)

$ grep -c admin@bidbuilder.local tests-e2e/_helpers/auth.ts
0

$ grep -rln admin@bidbuilder.local tests-e2e/
# (no matches)
```

The dev-only `/api/test/users` endpoint that mints per-spec users is pinned
absent in Production by `SecurityHeadersTests` + `TestSupportEndpointsTests`
(the `ApiFixture` boots `UseEnvironment("Production")`, so the 404 IS the
evidence). CI E2E on `#131` passed under the strict default limit — the
definitive proof that the test fragility was the root cause, not a real
security control.

## 3. 29.A.2 evidence — supply-chain scanning active

The first real Trivy gate run (PR `#132`) caught **20 fixable HIGH/CRITICAL
findings** that would otherwise have shipped silently:

- **api image (7: 2 CRITICAL, 5 HIGH)** — `libgnutls30`/`libssl3`/`openssl`,
  fixed by `apt-get upgrade -y` in the runtime stage
- **web image — libcrypto3 (1 HIGH)** — fixed by `apk upgrade --no-cache`
- **web image — Node-pkg tier (11 HIGH)** — `tar`/`glob`/`minimatch`/
  `cross-spawn` bundled inside `npm` itself. Production runs `node server.js`
  only, so `npm` + `corepack` are stripped from the final stage

All 20 fixed in the same PR. `.trivyignore` stayed empty.

Dependabot activated immediately on merge and opened **13 PRs** for
github-actions / Docker base / npm tier majors in the first hour:

```
#133 actions/setup-node 4 → 6     #140 eslint 9.39.4 → 10.4.1
#134 actions/checkout 4 → 6       #141 typescript 5.7.3 → 6.0.3
#135 node 20-alpine → 26-alpine   #142 sonner 1.7.4 → 2.0.7
#136 github/codeql-action 3 → 4   #143 dotnet/sdk 8.0 → 10.0
#137 actions/upload-artifact 4→7  #144 lucide-react 0.564.0 → 1.17.0
#138 actions/setup-dotnet 4 → 5   #145 dotnet/aspnet 8.0 → 10.0
#139 @types/node 22 → 25
```

Acceptance bar "first Dependabot PR within 24h" → **within minutes**. CVE
SLA in `SECURITY.md` (critical < 7d, high < 30d) — first triage window
opens 7 days after merge of `#132` and is owned by the Tier-B PR sweep.

## 4. 29.A.4 evidence — security headers + CSP

`SecurityHeadersTests` pin every header on `/healthz` (200) and `/api/ping`
(401) — both pass against the Production-mode `ApiFixture`.

Live verification on rebuilt containers (`ffe2291` build):

```
$ curl -sI http://localhost:8081/healthz | grep -iE 'x-frame|x-content|referrer|permissions|content-security|strict-transport'
X-Frame-Options: DENY
X-Content-Type-Options: nosniff
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=()
Strict-Transport-Security: max-age=31536000; includeSubDomains; preload
Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'

$ curl -sI http://localhost:3100/login | grep -iE 'x-frame|x-content|referrer|permissions|content-security|strict-transport'
content-security-policy: default-src 'self'; script-src 'self' 'nonce-…==' 'strict-dynamic'; …
permissions-policy: camera=(), microphone=(), geolocation=(), payment=(), usb=()
referrer-policy: strict-origin-when-cross-origin
strict-transport-security: max-age=31536000; includeSubDomains; preload
x-content-type-options: nosniff
x-frame-options: DENY
```

Every `<script>` tag emitted by Next (the inline boot script + every chunk)
carries the per-request nonce — the `'strict-dynamic'` chain works, so
`script-src` never needs `'unsafe-inline'`. The CSP for HTML surfaces on
the API (Swagger UI in Dev, Hangfire dashboard) intentionally relaxes to
`'self'` + their own inline scripts; the JSON / health / metrics / OpenAPI
paths get the strict `default-src 'none'`.

## 5. Regression evidence (cumulative across A.1 + A.2 + A.4)

| Check | Result | Notes |
|-------|--------|-------|
| `dotnet test` | **383/383 pass** | +2 over the 28.x baseline of 381 (the new `SecurityHeadersTests`) |
| `npx tsc --noEmit` | clean | |
| `npm run lint` | 0 errors | 40 pre-existing warnings, unchanged |
| `npm run test` (vitest) | **88/88 pass** | unchanged |
| `npm run build` | OK | Next 16.2.9 + middleware compiles to Edge runtime |
| CI on `#146` (8 checks) | all green | Backend / Frontend / E2E (Playwright) / CodeQL (TS + C#) / Trivy api / Trivy web / CodeQL job |

CI on `#131` and `#132` was also fully green on their respective merge
SHAs — the merge order means each tier landed against the previous one's
passing state, not a stale baseline.

## 6. Re-grade against the audit's six findings

| Audit finding | Tier A action | Status |
|---|---|---|
| Test isolation (shared-admin coupling) | 29.A.1 | **closed** |
| Supply-chain hygiene (no CVE scan, no SBOM) | 29.A.2 | **closed** |
| Security headers (no CSP / HSTS / frame-options) | 29.A.4 | **closed** |
| Server-synced preferences (UX durability) | 29.A.3 (deferred) | **open — Tier B** |
| Change-management discipline (no feature flags) | 29.B.1 | open — Tier B |
| External validation (pen test, SOC 2) | Tier D | open — Tier D |

Closing three of the three security/hygiene gaps the auditor named
specifically delivers the bulk of the Tier A grade. Deferring 29.A.3 keeps
one named UX gap open — that is why this is graded at the lower end of the
spec's 8.0–8.2 target.

**Re-grade: 7.5 → 8.0 / 10.** The 0.5 lift breaks down as:

- +0.1 supply-chain scan running on every PR + CVE policy with SLA
- +0.1 20 real CVEs closed (proving the gate is not theatre)
- +0.1 SBOM artifact per build (auditable "what shipped" record)
- +0.1 nonce-based CSP without `'unsafe-inline'` + HSTS preload-eligible
- +0.1 X-Frame-Options DENY + Permissions-Policy denying sensors

Not claimed: anything depending on 29.A.3 (cross-device recents/theme),
feature flags (Tier B), or external attestations (Tier D).

## 7. Tier-A debt carried to Tier B

- **29.A.3** — server-synced `UserPreferences.RecentEntities` JSONB column
  + `GET/PUT /api/me/preferences/recent`; localStorage becomes a
  write-through cache. Spec unchanged from §29.A.3 of `docs/ROADMAP.md`.
- **Dependabot triage** — 13 majors open from §3. The CVE SLA in
  `SECURITY.md` starts the clock on `2026-06-11`; the first sweep is due
  by `2026-07-11` for High-tier, `2026-06-18` for any Critical that
  arrives. Owner: Tier B PR sweep.

## 8. Sign-off

Phase 29 Tier A is **closed at 8.0 / 10** with one named open item
(29.A.3) carried to Tier B. Tier B begins with 29.A.3 + 29.B.1 (feature
flags / staged rollout); both are independently shippable and the order
between them is a product decision, not a technical one.
