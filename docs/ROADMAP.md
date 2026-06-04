# BidBuilder — System Roadmap

> A multi-tenant **construction bid-estimating** web platform.
> Architecture mirrors **SmartShipping** (same stack, same admin/tenancy/RBAC core).

---

## 1. Vision

BidBuilder lets a contracting company (a **tenant**) run **many project estimations
concurrently**. Each **team** logs in and works on **their own project's estimate**:
quantity take-off → unit-rate build-up (labor + material + equipment) → preliminaries
→ overhead + margin → **final bid price + quote document**.

This is a deterministic, **inch-by-inch / unit-rate** construction estimate
(AACE Class 3 → Class 1), not a parametric guess.

---

## 2. Technology stack (identical to SmartShipping)

| Layer | Technology |
|---|---|
| Frontend | Next.js 16 (App Router) · React 19 · TypeScript · Tailwind v4 · shadcn/ui (Radix) · TanStack Query · react-hook-form + zod · recharts · lucide · sonner · i18n (en/ar + RTL) |
| Backend | ASP.NET Core 8 minimal-API (`Endpoints/`) · EF Core 8 · **PostgreSQL** |
| Security | JWT · TOTP 2FA · API keys · bcrypt · CSRF · RBAC filters · audit log |
| Infra | Docker Compose · Caddy reverse proxy · Redis (cache/events) · optional Expo mobile |

---

## 3. Reused as-is from SmartShipping (the "administration module")

The tenancy + admin + RBAC core is domain-agnostic and lifted wholesale:

- `Tenant` + `IHasTenant` marker → global EF query filter auto-scopes every table.
- `TenantResolutionMiddleware` → resolves tenant from JWT `tenant_slug` claim (header fallback).
- `User` + `UserRole` { TenantUser, TenantAdmin, SuperAdmin, CustomerUser }.
- `Group` (a **team**) → `UserGroup` (membership) → `GroupModule`
  (`CanView/CanAdd/CanEdit/CanDelete` per module) → `Module` (per-tenant feature toggle).
- Admin pages: platform admin, tenant-users, tenant-users-groups, general settings.

---

## 4. The one structural addition: the Project scoping layer

SmartShipping is **2-level** (`Tenant → data`). BidBuilder is **3-level**:

```
Tenant
  └─< Project                 NEW  — the tender/opportunity (IHasTenant)
        ├─< ProjectTeam        NEW  — (ProjectId × GroupId) which TEAMS may access it
        └─< Estimate                — a versioned bid; a team works "their" estimate here
```

- **Project access = team-based.** A `Group` (team) is granted a `Project` via `ProjectTeam`;
  existing `GroupModule` flags still govern *what* members may do inside.
- **Concurrency = optimistic** (Postgres `xmin` rowversion) — many teams/estimates run at
  once; conflicting writes are detected, never silently lost.
- New `ProjectAuthorizationFilter` runs alongside the reused `ModulePermissionFilter`.

---

## 5. Estimation domain model

```
Project ─< Estimate (revision/version)
   Estimate ─< BoqSection ─< BoqItem ──→ Assembly | ad-hoc build-up   (+ Qty, UnitRate)
   Estimate ─< Preliminary          (indirect / site-overhead cost lines)
   Estimate ─< Markup               (overhead %, profit %, contingency %, escalation %)

RESOURCE LIBRARY  (tenant-scoped, shared across all projects)
   LaborResource · MaterialResource · EquipmentResource · Subcontractor
   Assembly ─< AssemblyComponent ──→ resource × consumption factor  ⇒ unit rate
```

**Engine:** `qty × unit-rate → direct cost → + prelims → + markups → BID PRICE → priced BOQ`.

---

## 6. RBAC module registry (seeded per tenant)

`projects` · `resource-library` · `assemblies` · `boq` · `rate-analysis`
· `prelims-markups` · `reports` · `estimate-admin`

Each is a `Module` row; teams get `CanView/Add/Edit/Delete` per module via the reused admin UI.

---

## 7. Locked decisions (override anytime)

1. **Project access** — team (Group) based, via `ProjectTeam`.
2. **Concurrency** — optimistic, Postgres `xmin`.
3. **Codebase** — separate repo/DB/deployment mirroring SmartShipping (a fork, not a bolt-on).
4. **Billing** — drop Stripe plan-gating for v1; keep tenancy/auth/RBAC/admin.

---

## 8. Phased delivery

| Phase | Deliverable | Status |
|---|---|---|
| **0 — Foundation** | Solution + API + reused tenancy/admin/RBAC core + estimation domain models. Clean build + EF `InitialCreate` migration (21 tables, xmin concurrency). | ✅ done |
| **1 — Project layer** | Dockerized (Postgres+API), migrate+seed on boot, JWT auth/login, team-scoped projects CRUD + team assignment. Access control proven via tests. | ✅ done |
| **2 — Resource Library** | Labor/material/equipment/subcontractor CRUD + RBAC `resource-library` module gating (PermissionService). Proven via tests. | ✅ done |
| **3 — Estimate & BOQ** | BOQ sections/items CRUD under an estimate (assembly-priced or ad-hoc), project-access + `boq` RBAC. Excel import deferred to Phase 7. | ✅ done |
| **4 — Rate Engine** ⭐ | Assemblies + components CRUD + unit-rate build-up (`RateEngine`, materials carry wastage), `assemblies` RBAC. Verified: RC footing = 124.50/m³. | ✅ done |
| **5 — Markups** | Preliminaries (fixed + time-related) + compounding markups → bid price (`EstimateCalculator` + pure `EstimateMath`, 10 unit tests). Verified live: bid = 78,903.72. | ✅ done |
| **6 — Outputs** | Priced-BOQ + bid-summary export: Excel (ClosedXML) + PDF (QuestPDF), download endpoints + UI buttons, `reports` RBAC. Verified: valid files, totals match (bid 78,903.72). | ✅ done |
| **7 — Advanced** | Versioning, what-if margin, multi-currency, benchmarking, mobile. | |

Phases 0→6 = a complete, usable bid-estimating SaaS. Phase 4 is the core value.

**Frontend (vertical slices):** ✅ Next.js 16 + React 19 + Tailwind v4 + TanStack Query app
running in Docker (`web` on host :3100). Token+tenant API client (`lib/api.ts`), auth context
(`lib/auth.tsx`), app shell with sidebar. Pages: login, projects list, project detail (full
estimate breakdown: BOQ + prelims + markups + totals), resource library, assemblies. Builds
clean (`next build`, standalone output).

**Frontend editing (CRUD):** ✅ Create project; full resource-library CRUD (add/edit/delete all
4 types); create assembly + live component build-up editor; inline BOQ editing on the estimate
(add/delete sections & items, inline qty/rate edit, add/delete preliminaries & markups) with live
bid-price recompute. Mutations use TanStack Query; recompute responses pushed straight into cache.
Data persists in PostgreSQL (named volume) — verified across an API container restart.

**Team-assignment UI (Phase 7 polish):** ✅ On the project page, a Teams panel lists assigned teams
(with a Lead badge); a tenant admin can assign any unassigned team (dropdown + Lead toggle) or
remove one. Backed by `GET /api/projects/groups` + `DELETE /api/projects/{id}/teams/{groupId}`
(both admin-only) added to `ProjectEndpoints`. Verified live: assign 200 / duplicate 409 /
unassign 204 / re-delete 404.

**Per-permission UI gating (Phase 7 polish):** ✅ `GET /api/auth/permissions` returns the caller's
effective module rights (admins = all; others = OR of their teams' `GroupModule` flags). The
`usePermissions()` hook (`lib/permissions.tsx`) exposes `can(module, action)`; the sidebar now only
shows modules the user can view, and add/edit/delete/export affordances across resources, assemblies,
and the estimate editor are hidden when the right is absent. The API still enforces every mutation —
this only hides affordances. Verified live: admin sees all; a view-only estimator sees just Resource
Library, read-only.

**What-if margin analysis (Phase 7 polish):** ✅ `POST /api/estimates/{id}/whatif` re-prices the bid
under a proposed markup set against the current direct+indirect cost — no persistence
(`EstimateCalculator.WhatIfAsync`, pure `EstimateMath`). The estimate page gains a What-if panel:
edit each markup %, see the projected bid price and the delta vs the committed one update live, then
optionally "Apply these margins" to persist (gated by prelims-markups edit). Verified live: 8/12/5 →
78,903.72; profit 12→8% → 76,085.73; negative % rejected; baseline untouched.

**Estimate revisions / versioning (Phase 7 polish):** ✅ A project now holds multiple estimate
revisions. `POST /projects/{pid}/estimates` (blank), `POST …/{id}/clone` (deep-copy BOQ + prelims +
markups into the next revision, status reset to Draft, source untouched), and `PUT /estimates/{id}`
(title + lifecycle status: Draft → UnderReview → Published → Superseded) — all gated by the
`estimate-admin` module. The project page gains a revision selector + New / Duplicate buttons, an
editable status, and a "Create first estimate" empty state (so API-created projects are now usable in
the UI). Verified live: clone reproduced the 78,903.72 bid; blank started at 0; status published; the
source revision stayed intact.

**Concurrency-conflict handling (Phase 7 polish):** ✅ The optimistic-concurrency promise (locked
decision #2) is now wired up. Each estimate breakdown carries a `rowVersion` (Postgres xmin); the
client echoes it as an `If-Match` header on every estimate edit, and a single endpoint filter pins it
so the save runs `UPDATE … WHERE xmin = expected`. A stale write now returns a clean **409** (the UI
toasts and reloads the latest) instead of the previous 500, so no concurrent edit is silently lost.
No header = last-writer-wins (backward compatible). Verified live: fresh edit 200, replayed stale
version 409, header-less edit 200.

**Rate cascade (Phase 7 polish / correctness fix):** ✅ Closed the stale-price gap — `Assembly.
ComputedRate` and estimate roll-ups are cached, and editing a library resource's rate now cascades:
`RateCascadeService` recomputes every assembly that references the resource, then every estimate whose
BOQ uses those assemblies (wired into all resource PUT/DELETE handlers). Verified live: bumping a
mason's hourly rate 15→20 auto-updated the RC-footing assembly (124.50→130.50) and the estimate bid
(78,903.72→80,808.84) with no manual recompute; 10 unit tests still green.

**Excel BOQ import (Phase 7 feature):** ✅ Bulk-load a Bill of Quantities from a spreadsheet.
`ImportService` (ClosedXML) parses a header-driven .xlsx (Description + Quantity required; sections
grouped by code/title; items priced by Assembly Code or an ad-hoc Unit Rate), validating fully before
any write. `GET /api/estimates/import-template.xlsx` serves a ready-to-fill template; `POST
/api/estimates/{id}/import` appends the parsed sections/items and recomputes, returning the counts +
breakdown. The estimate page gains Template + Import buttons on the BOQ card. Verified live: the
template's example rows import to a 1-section/2-item BOQ priced at 39,125 (assembly + ad-hoc); invalid
and missing files return a clean 400.

**Referential-integrity delete guards (Phase 7 polish):** ✅ Deleting a library resource that an
assembly still uses — or an assembly that a BOQ item still prices — now returns a clear **409** naming
the dependents, instead of silently orphaning the build-up and dropping its cost. Unused resources and
assemblies still delete cleanly (204). Verified live; 10 unit tests green.

**Delete estimate revision (Phase 7):** ✅ `DELETE /api/projects/{pid}/estimates/{id}` (estimate-admin)
removes a revision and all its BOQ/preliminaries/markups, and the revision bar gains a Delete button
(with confirm). Children are removed explicitly so the self-referencing section FK can't trip a cascade
ordering error. Verified live: clone → delete → 204 with no orphaned rows; deleting the last revision
returns the project to the "create first estimate" empty state.

**Estimate-status workflow lock (Phase 7):** ✅ Once a revision is **Published** or **Superseded** it's
frozen — its BOQ, preliminaries, markups and imports are locked (409) so a finalised bid can't silently
change; only the status itself can move (revert to Draft to edit again). Enforced by an endpoint filter
(content-mutation paths only; reads, exports, what-if and status changes stay open). The estimate page
disables the edit affordances and shows a banner when locked. Verified live: Published → edits/import
409, what-if 200; revert to Draft → edits 200.

**Assembly edit/delete in the UI (Phase 7):** ✅ The assembly detail page gains Edit (rename / unit /
active) and Delete actions (gated by the assemblies module), completing assembly CRUD in the app. Delete
surfaces the in-use 409 guard; an unused assembly deletes and routes back to the list. Verified live.

**Tenant settings + branded exports (Phase 7):** ✅ A tenant-admin **Settings** page edits the company
profile (address, city, country, phone, email, website) and estimating defaults, backed by
`GET/PUT /api/settings` (read for any user, edit admin-only). The company profile now brands the
exported Excel/PDF bid header beneath the company name. Verified live: settings save (admin), exported
workbook header carries the address + contact, non-admins are read-only (PUT 403/401).

**Copy estimate to another project (Phase 7):** ✅ An estimate can be copied into a different project as
a new Draft revision (`POST /api/projects/{pid}/estimates/{id}/copy`, needs access to both projects +
estimate-admin). A "Copy to…" action on the revision bar picks the target project. Shares the deep-copy
engine with same-project Duplicate. Verified live: full BOQ/prelims/markups copied; time-related
preliminaries correctly re-scale to the target project's duration.

**Project editing (Phase 7):** ✅ `PUT /api/projects/{id}` (admin) edits a project's name, client,
location, currency, duration, tender date and status, with an Edit modal on the project page. Changing
the **duration** re-prices time-related preliminaries on the project's non-locked estimates (Published
bids stay frozen). Verified live: duration 9→6 moved the sample bid 78,903.72→71,283.24 and back.

**Audit logging (Phase 7):** ✅ A per-tenant, append-only audit trail records who did what — project
create/edit, team assign/remove, and estimate create/clone/copy/delete/status-change (e.g. "Draft →
Published"). `AuditService` writes events after each action; `GET /api/audit` (admin) and an **Audit
log** admin page show them newest-first. Verified live: publishing, creating and deleting estimates
are recorded with the acting user; non-admins are blocked.

**CSV BOQ export (Phase 7):** ✅ `GET /api/estimates/{id}/export.csv` (reports RBAC) completes the
export trio (Excel/PDF/CSV). Renders a flat, machine-readable priced BOQ — one row per line item with
its section denormalized onto each row — for spreadsheet pivots and re-import by procurement tools.
RFC-4180 quoting + UTF-8 BOM so Excel detects the encoding. A **CSV** button sits beside Excel/PDF on
the estimate. Verified live: `text/csv` with `…-boq.csv` filename, two items totalling 39,125; unauth
→ 401.

**Company logo on exports (Phase 7):** ✅ A tenant admin can upload a PNG/JPEG logo (≤1 MB) on the
Settings page; it's stored as bytes on `TenantSettings` (migration `AddTenantLogo`) and branded onto the
exported **Excel** (top-right of the Bid Summary sheet, aspect-preserved) and **PDF** (header, beside the
company name) bid documents. `POST/GET/DELETE /api/settings/logo` (upload+delete admin-only; content-type
+ size validated). Settings exposes `hasLogo`; the page shows an authed preview with Replace/Remove.
Verified live: upload 200 → Excel 7,979→9,985 b with `xl/media/image.png`, PDF 68,901→70,301 b; non-image
→ 400; remove → baseline. (Note: ClosedXML's PNG reader rejects degenerate 1×1 PNGs — real logos are fine.)

**Audit log filters + pagination (Phase 7):** ✅ `GET /api/audit` now filters by action, actor
(case-insensitive name/email contains via Npgsql `ILIKE`), and a UTC date range (`from`/`to` as whole
days), and returns a paged envelope `{ total, take, skip, items }`; `GET /api/audit/actions` lists the
distinct actions for the filter dropdown. The Audit log page gained a filter bar (action select, actor
search, From/To dates, Apply/Clear) and a Prev/Next pager ("Showing 1–50 of N", `keepPreviousData` for
flicker-free paging). Verified live: action filter (total 2), case-insensitive actor match, date bounds
(future→0, today→all), skip paging (distinct ids), 401 unauth.

**Assemblies active/inactive filter (Phase 7):** ✅ `GET /api/assemblies?active=true|false` filters by
status (omit = all). The Assemblies list gained a segmented **Active / All / Inactive** control (defaults
to Active to declutter retired items) and an "Inactive" badge on retired rows. Verified live: flipping the
sample assembly inactive moved it between the active(0)/inactive(1) lists and back; baseline restored.

**Multi-currency (FX) — present bids in a 2nd currency (Phase 7):** ✅ A manual tenant **rate table**
(`CurrencyRate`: base-currency units per 1 unit of a currency, e.g. 1 USD = 3.6725 AED) is maintained on
Settings (`GET /api/settings/currencies`, `PUT/DELETE …/{code}` admin-only; base-currency and ≤0 rates
rejected). Each estimate can pick a **secondary (presentation) currency**; the breakdown then carries an
`fx` view — the bid converted via the base cross-rate (`factor = R(native)/R(secondary)`) plus the rate
and a frozen flag. **Freeze-at-publish:** publishing snapshots the native→secondary rate onto the estimate
(`FxRate`/`FxRateAt`) so the converted figure never drifts; Draft/UnderReview use the live rate; reverting
to Draft drops the snapshot. Exports (Excel + PDF) show the converted bid line. The estimate header gains a
"Show in" currency picker and a converted-bid line (rate + frozen/live). Verified live: USD@3.6725 → bid
converts at 0.272294 (live, Draft); publish freezes; changing the rate to 4.0 left the published bid
unchanged; reverting to Draft moved it to the live 0.25; a currency with no rate yields `fx:null`; baseline
restored. **Phase 7 is now fully complete.**

---

## 8b. Phase 8 — Project breakdown & extensible item pricing

**Extensible cost build-up (item-level pricing):** ✅ A tenant **catalog of cost-component types**
(`CostComponentType`: Code/Name/CalcKind, seeded built-ins Material·Labor·Equipment = *Amount* and
Waste·Overheads = *Percent*; admins add custom types like Transport/Insurance in Settings). Each BOQ item
can carry a build-up of `ItemCostComponent` lines; its unit rate = Σ(Amount lines) + Σ(Percent% × that
amount subtotal). Pricing precedence: **component build-up → assembly → ad-hoc rate** (existing items
unchanged). The estimate item form gained a "cost build-up" pricing mode + a live build-up modal; the
breakdown/exports carry the per-component lines. `/api/cost-components` CRUD (admin; built-ins
undeletable, in-use guarded). Verified live: MAT 100 + LAB 50 + Waste 10% → rate 165, ×2 = 330; catalog
add/delete/guards.

**Project area breakdown + cost roll-up:** ✅ A project now has an **arbitrarily-nested Area tree**
(`Area`, ParentAreaId; Kind label Area/Sub-area/Unit), shared across estimate revisions and managed in an
Areas panel on the project page. BOQ items carry an optional `AreaId`; per estimate, item line totals
**escalate unit → sub-area → area → project** via `GET /api/estimates/{id}/areas-rollup`, shown in a
"Cost by area" panel. Area CRUD at `/api/projects/{pid}/areas` (project access + `projects` RBAC; delete
blocked when an area has children or assigned items). Cross-project estimate copy drops area tags.
Verified live: Unit 101 (600) → Floor 1 (600) → Building A (600); delete guards; roll-up refreshes on edits.

**Export detail (summary + details):** ✅ Both Excel and PDF deliverables now carry the full picture.
Excel: a new **Cost by Area** worksheet (tree, indented, rollup totals + assigned/unassigned), and per-item
**unit-rate build-up sub-rows** (Material/Labor/… and Waste%/Overheads% with their money amounts) under the
Priced BOQ. PDF: a **Cost by Area** section (indented tree with rollup totals) and the component build-up as
a muted line under each item's description. Roll-up computation refactored into a shared `AreaRollupService`
used by both the endpoint and the exporters. Verified live: xlsx has the *Cost by Area* sheet + Material/
Waste sub-rows; PDF text contains *Cost by Area* + component names.

---

## 8c. Phase 9 — Automated test suite

**Integration tests (HTTP, real Postgres):** ✅ `api.Tests` gained an `ApiFixture`
(`WebApplicationFactory<Program>`) that boots the API in-process against a throwaway database on the dev
compose Postgres (a unique `bidbuilder_test_<guid>`, created + migrated + seeded on startup, force-dropped
on disposal; connection injected via the `ConnectionStrings__Postgres` env var, host/creds overridable for
CI). 11 integration tests across an `"api"` collection cover: health, auth (401 unauthenticated, admin
login, `permissions` all-true), the seeded sample project, cost-component catalog (seeded types, duplicate
409, built-in delete 409), the **cost build-up** rate math (MAT 100 + Waste 10% → 165, ×2 = 330), the
**area roll-up** escalation (600 up the tree) + delete guard, optimistic-**concurrency** 409 on a stale
`If-Match`, and Excel/CSV export content types. Plus the existing 10 `EstimateMath` unit tests →
**21 tests, all green**. Run with `dotnet test` (requires the compose `db` up on :5433). `Program` was made
`public partial` so the test host can boot it.

---

## 8d. Phase 10 — Deployment hardening (in progress)

**Fail-fast secret guards:** ✅ `Program.cs` now refuses to boot outside `Development` unless
`ConnectionStrings:Postgres` is supplied and `Jwt:SigningKey` is a strong, non-default value (≥32 chars,
not a `CHANGE-ME`/`dev-only` placeholder) — no insecure localhost/dev-key fallback can leak into a real
deployment. The integration tests run as `Production` with a valid injected key, so they exercise the
guarded path (21/21 still green). Added `docs/DEPLOYMENT.md` (secrets, DB, TLS/CORS, CI, smoke test).

**Prod CORS allowlist:** ✅ Outside `Development`, `AllowedOrigins` is mandatory and `localhost`/`127.0.0.1`
are rejected — the permissive dev CORS policy can no longer leak into a deployment.

**No default admin in prod:** ✅ Seed credentials are configurable (`Seed:AdminEmail` / `Seed:AdminPassword`).
Outside `Development` no admin is seeded unless a password is supplied, and the sample project only seeds when
`Seed:DemoData=true` — `Admin@12345` stays out of real deployments. `ApiFixture` opts into both for the test
baseline (21/21 green). Merged to `main` (7acab56).

**Dev DB reset:** ✅ Dropped + recreated the dev `bidbuilder` database; the API reseeded a clean baseline
(estimate 1 bid back to 0.00 from the drifted ~92,525; 1 tenant/user/project, 8 modules, 5 cost types, 0 BOQ items).

**Rotated dev secrets:** ✅ Generated strong `POSTGRES_PASSWORD` (openssl hex, 48 chars) + `JWT_SECRET`
(base64, 64 chars), `ALTER ROLE`d the live Postgres role, rewrote the gitignored `.env`, recreated the
containers — healthz/login/DB verified. Old `dev-only…` placeholders gone.

**TLS reverse-proxy:** ✅ Added `docker-compose.prod.yml` + `deploy/Caddyfile` (commit 04591c0): Caddy
terminates TLS (auto Let's Encrypt), single public origin routes `/api`+`/healthz` to the API and the rest
to web; db/api/web publish no host ports; API runs Production with `AllowedOrigins=PUBLIC_URL`. `.env.example`
+ `docs/DEPLOYMENT.md` updated.

**Branch protection on `main`:** ⛔ BLOCKED — both the classic branch-protection API and repository rulesets
return 403 "Upgrade to GitHub Pro or make this repository public" (private repo on the free plan). Needs the
repo made public or a Pro upgrade; awaiting user decision. Until then `main` is advanced by fast-forward.

---

## 9. Build order within Phase 0

1. `BidBuilder.sln` + `api/BidBuilder.Api.csproj` (net8.0, EF Core + Npgsql + JWT + bcrypt).
2. Tenancy: `ITenantContext`, `TenantResolutionMiddleware`.
3. Reused admin models: Tenant, User, Group, Module, GroupModule, UserGroup, TenantSettings, AuditEvent.
4. Estimation domain models (section 5) + the Project layer (section 4).
5. `AppDbContext` with tenant query filters + `xmin` concurrency + tenant auto-stamping.
6. `DesignTimeDbContextFactory`, `Program.cs`, `appsettings*.json`, health endpoint.
7. `dotnet build` green ✔ — checkpoint.
