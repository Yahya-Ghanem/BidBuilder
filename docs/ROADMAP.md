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

## 8j. Phase 16 — Clone room

**Clone a unit:** ✅ A **Clone** button on each unit duplicates the room — it creates a new unit (same
parent / kind / measure) under a name you choose and copies all of that unit's activities in the current
estimate (description, unit, quantity and the Material/Manpower qty × rate build-up). `POST
/api/estimates/{id}/areas/{areaId}/clone` runs under the estimate's optimistic-concurrency guard, is blocked
on locked (Published/Superseded) revisions, and recomputes the bid. The copy is independent — edit it without
affecting the original. 1 integration test (clone a unit with a 10 × 5 = 50 activity → two activities). (PR #13.)

**Clone at any level (PR #15):** ✅ Clone now works on any area — Building, Apartment (sub-area) or Unit. It
duplicates the **whole subtree** (sub-areas + units, hierarchy preserved) and every activity tagged anywhere in
it, so you can build one apartment and clone it (with all its rooms and their material/manpower) in one click.
2nd integration test (clone a sub-area → its child unit + activity duplicated).

## 8i. Phase 15 — Activity catalog (built-in + user)

**Activity dropdown:** ✅ Adding work under a unit now picks the activity from a tenant **catalog** instead of
free typing. 28 common construction activities (Excavation, Block work, Plastering, Tiling, MEP first/second
fix, …) are seeded as **built-ins** (undeletable); users add their own from the dropdown's "+ New" or in
**Settings → Activities**. New `ActivityType` catalog (migration `AddActivityCatalog`, idempotent seed that
backfills existing tenants); `/api/activities` — read for any user, add for anyone with the `boq` permission,
edit/delete admin-only. Picking an activity fills the BOQ line description (no schema change to items). 3
integration tests. (PR #11.)

## 8h. Phase 14 — Activities under units (quantity × rate)

**Activities + qty × rate build-up:** ✅ Work activities can be added under each unit, each carrying
**Material (qty × unit-price)** and **Manpower (hours × rate)** — exactly the unit → activity → material +
manpower workflow. Delivered in two parts:
- *Cost build-up (PR #8):* each Amount-kind cost component (Material, Manpower/Labor, Equipment…) can now be
  entered as **quantity × rate** instead of a flat amount; Percent components (Waste, Overheads) still apply to
  the subtotal. `ItemCostComponent` gained `Quantity`/`Rate` (migration `AddCostComponentQtyRate`); the rate
  engine is unchanged (Value stays canonical). The build-up modal and the Excel/PDF component lines show qty ×
  rate. Test: MAT 100×50 + LAB 40×50 + WST 10% → unit rate 7,700.
- *Unit-centric editor (PR #9):* an "Activities by unit" panel shows the area tree with each unit's activities
  and their Material / Manpower / total, plus add / edit / delete. It reuses BOQ items + the build-up, so
  activities flow straight into the bid, the area roll-up and benchmarking. Adding an activity auto-creates an
  "Activities" section and tags the item to the unit.

## 8g. Phase 13 — Cross-project benchmarking

**Benchmarking:** ✅ `GET /api/benchmarks` + a Benchmarks page compare **cost per unit/m²** across the projects
a user can access. For each project it takes a representative estimate (latest published, else latest
revision), rolls up the areas, and collects every measured area as a cost-per-unit data point; points are
grouped by measure unit with **min/avg/max** (mixed currencies are flagged, aggregate hidden). Read-only —
never changes a bid; scoped by project access and gated by the `reports` permission. 2 integration tests
(cost/key 2000 ÷ 10 = 200; anonymous 401). 27/27 green. (PR #6.)

## 8f. Phase 12 — Area measures & cost-per-unit

**Area measures:** ✅ Each project area can carry a **quantity + unit** (e.g. 120 m², 50 units). The
per-estimate area roll-up now reports **cost per unit/m²** (`rollupTotal ÷ quantity`) — the core construction
benchmark — and the exported "Cost by Area" sheet/section shows the measure and per-unit cost (Excel + PDF).
Purely analytical: the measure never changes the bid. Migration `AddAreaMeasure`; 1 new integration test
(5000 / 100 m² → 50/unit; negative quantity → 400). 25/25 green. (PR #4.) Lays the groundwork for
cross-project benchmarking.

## 8e. Phase 11 — User & team management

**User & team administration:** ✅ Admin-only screen + API (`/api/admin`, PR #2) to manage who can sign in
and what they can do — closes the gap where a second user could only be added via direct DB access. Backend
`UserManagementEndpoints.cs`: users (list/create-with-password/update/reset-password/delete) and teams/groups
(list/create/update/delete + a per-module permission grid) over `GET /api/admin/modules`. Guards: per-tenant
unique email, password ≥ 8, role limited to TenantUser/TenantAdmin, built-in team undeletable, team-in-use
blocked, and lockout protection (can't remove the last active admin or your own account). Every action is
audited. Frontend `app/admin` (Users & Teams) with create/edit/reset/delete modals and a permission-grid
modal; admin-only nav entry. 3 new integration tests (24/24 green). Note: `User` is not `IHasTenant`, so its
`TenantId` is set explicitly on create.

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

**Branch protection on `main`:** ✅ Repo made **public** (free plan allows rulesets on public repos), then a
**"Protect main" ruleset** (active) was created: requires a pull request (0 approvals → owner can self-merge)
+ both CI checks green ("Backend (build + tests on Postgres)", "Frontend (type-check)", strict), with
no force-push and no branch deletion. Direct pushes to `main` are now blocked — all changes go via PR.

---

## 9. Build order within Phase 0

1. `BidBuilder.sln` + `api/BidBuilder.Api.csproj` (net8.0, EF Core + Npgsql + JWT + bcrypt).
2. Tenancy: `ITenantContext`, `TenantResolutionMiddleware`.
3. Reused admin models: Tenant, User, Group, Module, GroupModule, UserGroup, TenantSettings, AuditEvent.
4. Estimation domain models (section 5) + the Project layer (section 4).
5. `AppDbContext` with tenant query filters + `xmin` concurrency + tenant auto-stamping.
6. `DesignTimeDbContextFactory`, `Program.cs`, `appsettings*.json`, health endpoint.
7. `dotnet build` green ✔ — checkpoint.

---

## 10. Forward roadmap — Estimator + Systems-Analyst review → professional grade

> **Context.** Phases 0–16 delivered a complete, usable bid-estimating SaaS, and the
> post-audit hardening sprint closed the four production blockers (DB tenant FKs,
> observability, backups, write transactions) plus auth hardening, refactors and CI gates
> (audit moved 7.7 → ~9/10). This section is the **next** body of work, written from two
> lenses — a **senior estimator** (does it serve real tender practice?) and a **senior
> systems analyst** (architecture, data, scale, process). Each item is sized to ship as one
> **PR-gated change** (branch → CI green → squash-merge → rebuild → smoke), the same flow
> used throughout. Items are ordered so each makes the next safer.
>
> **Legend.** `E#` = estimator-driven (business value) · `S#` = analyst-driven (technical).
> Effort: **S** ≈ ½–1 day · **M** ≈ 1–2 days · **L** ≈ 3–5 days.

### Phase 17 — P0: protect bid correctness & money (do first)

These guard the integrity of the number the company is legally bound to. Ship before any
new feature, because every later change rides on the calculation engine being provably right.

#### 17.1 — `S1` Calculation golden-master + property tests + reconcile endpoint — **M**
- **Why.** `LineTotal`, `Assembly.ComputedRate`, `DirectCost`, `BidPrice` are *cached/denormalised*.
  A rounding or apply-order regression silently mis-prices a live tender. This is the single
  highest risk in a pricing tool.
- **Steps.**
  1. Add a **golden-master** test: build one representative estimate fixture (multi-section BOQ,
     assembly + ad-hoc + component build-up, fixed + time-related prelims, all 4 markups
     compounding, an Area tree, a secondary currency) and assert the full set of computed
     figures against a checked-in expected snapshot.
  2. Add **property-based** tests on `EstimateMath` (FsCheck/CsCheck): markups never produce a
     negative running subtotal for non-negative inputs; `ApplyMarkups` order-sensitivity holds;
     `Round2` is idempotent; Σ line totals == reported `DirectCost`.
  3. Add `POST /api/estimates/{id}/recompute` (estimate-admin) that re-runs the engine and
     returns `{ before, after, drifted: bool }` without persisting unless `?commit=true` — a
     reconciliation tool to detect/repair any cached drift.
  4. CI: fail the build if golden-master or property tests fail.
- **Files.** `api.Tests/CalcGoldenMasterTests.cs` (new), `api.Tests/EstimateMathPropertyTests.cs`
  (new), `api/Endpoints/EstimateEndpoints.cs`, `api/Services/EstimateCalculator.cs`.
- **Acceptance.** Golden master + property tests green in CI; `recompute` reports `drifted:false`
  on a freshly-saved estimate and detects an artificially corrupted cache.

#### 17.2 — `E2` Tax/VAT as a first-class line (distinct from profit markup) — **M**
- **Why.** Default currency is AED; UAE levies **5% VAT**, which sits *outside* margin and is
  often excluded from bid comparison. Folding it into a `Markup` mis-states gross margin and
  risks a non-compliant tender sum.
- **Steps.**
  1. Add `TaxRatePct` (nullable) + cached `TaxAmount` to `Estimate`; migration `AddEstimateTax`.
  2. Engine: compute `TaxAmount = Round2(BidPrice × TaxRatePct/100)` **after** markups; expose
     `BidPriceInclTax`. Tax never participates in the markup cascade.
  3. Surface tenant default tax rate in Settings; show "Bid (excl. tax) / VAT / Bid (incl. tax)"
     on the estimate header and in Excel/PDF/CSV exports.
  4. Tests: VAT line, exclusion from margin, exports carry both figures.
- **Files.** `api/Models/Estimate.cs`, `api/Services/EstimateCalculator.cs`,
  `api/Services/ExportService.cs`, `app/projects/[id]/page.tsx`, `app/settings/page.tsx`.
- **Acceptance.** A 5% VAT estimate shows excl/VAT/incl correctly; margin % is unchanged by VAT.

#### 17.3 — `E3` Provisional Sums / PC Sums / Dayworks / Alternates as line types — **M**
- **Why.** Only `Unit="LS"` exists today. Provisional/PC sums must usually be **excluded from
  OH+profit markup**; marking them up is a classic rejected-tender error. Alternates must be
  carried but excluded from the base tender total.
- **Steps.**
  1. Add `BoqItemKind` enum { Normal, ProvisionalSum, PcSum, Daywork, Alternate } to `BoqItem`;
     migration `AddBoqItemKind` (default Normal — existing items unchanged).
  2. Engine: ProvisionalSum/PcSum contribute to direct cost but are flagged `excludeFromMarkup`;
     Alternate lines roll up to a separate "alternates" total, not the base bid.
  3. UI: a Kind selector on the item form; exports group/label these sections distinctly.
  4. Tests: a provisional sum is **not** marked up; an alternate is excluded from the bid total.
- **Files.** `api/Models/BoqItem.cs`, `api/Models/Enums.cs`, `api/Services/EstimateCalculator.cs`,
  `api/Services/ExportService.cs`, `app/projects/[id]/page.tsx`.
- **Acceptance.** A provisional sum passes through at cost; alternates appear separately; bid total
  excludes them.

#### 17.4 — `S3` Verify & test project/team-level authorization — **S**
- **Why.** Global query filters scope by **tenant**; we must confirm a non-admin estimator can
  only see/edit projects their `ProjectTeam` grants. If enforcement is tenant-only, that's an
  intra-tenant confidentiality gap.
- **Steps.**
  1. Audit every project-scoped read/write endpoint for a `ProjectTeam` membership check
     (the `ProjectAuthorizationFilter` path).
  2. Add integration tests: user on Team A is 403/404 on a Team-B-only project across
     projects, estimates, BOQ, areas, exports.
  3. Close any endpoint missing the check.
- **Files.** `api/Endpoints/*.cs` (audit), `api.Tests/IntegrationTests.cs` (new tests).
- **Acceptance.** A cross-team access attempt is denied on every project-scoped route, proven by tests.

### Phase 18 — P1: high estimator value

#### 18.1 — `E1` Target-price back-solve (commercial adjustment) — **M**
- **Why.** The most-used move in the final 48h of a bid: *"we must land at AED 10.0M."* Today
  it's manual trial-and-error on the what-if panel.
- **Steps.**
  1. `POST /api/estimates/{id}/target` accepting `{ targetPrice }` or `{ targetMarginPct }`;
     back-solve the profit markup (or a final lump-sum commercial adjustment line) to hit it,
     using the existing pure `EstimateMath`. No persistence unless applied.
  2. Add an optional `CommercialAdjustment` amount on `Estimate` so the solve can be a flat
     ± lump sum rather than only a margin change.
  3. UI: extend the What-if panel with "Solve to target price/margin" → preview → Apply.
  4. Tests: solving to a target price reproduces it within rounding; margin solve is correct.
- **Files.** `api/Services/EstimateCalculator.cs`, `api/Endpoints/EstimateEndpoints.cs`,
  `api/Models/Estimate.cs`, `app/projects/[id]/page.tsx`.
- **Acceptance.** Entering a target price yields markups/adjustment that reproduce it; baseline untouched until Apply.

#### 18.2 — `E8` Surface gross-margin-on-price alongside markup-on-cost — **S**
- **Why.** "% markup on cost" vs "margin on selling price" confusion is the #1 estimating
  arithmetic error. Show both so reviewers can sanity-check instantly.
- **Steps.** Compute and display, on the estimate header + summary exports,
  `marginOnPrice = (BidPrice − TotalCost) / BidPrice` next to the existing markup %s; no schema
  change (derived). Add a unit test for the identity.
- **Files.** `api/Services/EstimateCalculator.cs` (derive in the breakdown DTO),
  `app/projects/[id]/page.tsx`, `api/Services/ExportService.cs`.
- **Acceptance.** Both figures shown and reconcile on the golden-master fixture.

#### 18.3 — `E4` + `S7` Dated resource rates, supplier-quote register, scheduled FX — **L**
- **Why.** Resources are single-valued (`RatePerHour`/`UnitPrice` + `UpdatedAt`) with no
  effective-dated history and no quote provenance; FX is refreshed manually. Multi-month bids
  need dated rates and "which quote, valid until when" traceability.
- **Steps.**
  1. `ResourceRateHistory` (effective-from date, rate, source) — engine resolves the rate
     effective at the estimate's pricing date; current rate stays the default.
  2. `SupplierQuote` (supplier, price, currency, validUntil, attachment ref) linked to a
     material resource; the chosen quote stamps the cost line for the cost report.
  3. Scheduled FX pull (see 19.3 job queue) writing `CurrencyRate` with provenance + audit.
  4. Tests: pricing date selects the correct historical rate; an expired quote is flagged.
- **Files.** `api/Models/` (new entities + migrations), `api/Services/RateEngine.cs`,
  `api/Endpoints/ResourceEndpoints.cs`, `app/resources/page.tsx`.
- **Acceptance.** Changing the pricing date re-prices via historical rates; quotes carry validity; FX auto-refreshes with an audit trail.

#### 18.4 — `S2` Move cascade recompute to a background job queue — **L**
- **Why.** `RateCascadeService` (resource → assemblies → estimates) is synchronous and
  in-request; on a large tenant a single common-resource price change can hang/timeout the
  request (RateEngine batching was deferred).
- **Steps.**
  1. Introduce a job queue (**Hangfire** on Postgres, or Quartz) with a dashboard behind admin auth.
  2. Resource PUT/DELETE enqueues a cascade job, returns 202 + a job id; the UI polls/toasts on completion.
  3. Batch the recompute SQL (set-based update of affected assemblies/estimates) to kill the N+1.
  4. Tests: enqueue → job completes → caches consistent; failure is retried + surfaced.
- **Files.** `api/Program.cs`, `api/Services/RateCascadeService.cs`, `api/Endpoints/ResourceEndpoints.cs`.
- **Acceptance.** A common-resource rate change returns immediately and reconciles asynchronously; large-tenant cascade no longer times out.

### Phase 19 — P2: maturity, scale & operability

#### 19.1 — `E5` Bid register + win/hit-rate dashboard (strategic) — **L**
- **Why.** `ProjectStatus` has Won/Lost but nothing captures **as-bid vs awarded vs actual**, so
  there's no win-rate learning loop — the hallmark of estimating maturity, and the biggest
  long-term differentiator.
- **Steps.** Capture submitted bid value, award value and (optional) final cost per project;
  a Bid Register page + `GET /api/analytics/bids` with hit-rate by project type / client / period,
  and bid-vs-award variance. Read-only analytics; never mutates a bid.
- **Files.** `api/Models/Project.cs` (outcome fields), `api/Endpoints/` (analytics), new
  `app/analytics/page.tsx`.
- **Acceptance.** Dashboard shows win rate and bid/award variance over a date range, scoped by access.

#### 19.2 — `E6` Risk-weighted contingency + `E7` cash-flow S-curve — **M**
- **Why.** Contingency is a flat %; reviewers want to *defend* it. Duration + time-related costs
  are already modelled, so an S-curve is nearly free and increasingly client-requested.
- **Steps.** A simple risk register (item, probability, impact) → suggested contingency that can
  feed the Contingency markup; an S-curve cash-flow projection over `DurationMonths` from
  time-related prelims + a spend curve, shown as a chart and an export tab.
- **Files.** `api/Models/` (risk entities), `api/Services/EstimateCalculator.cs`,
  `app/projects/[id]/page.tsx`, `api/Services/ExportService.cs`.
- **Acceptance.** Risk register produces a defensible contingency figure; S-curve renders and exports.

#### 19.3 — `S4` OpenTelemetry traces/metrics + alerting — **M**
- **Why.** Observability is logs-only — no metrics, traces or alerting. You'd learn of a failed
  recompute or backup from a user, not a page.
- **Steps.** Add OpenTelemetry (ASP.NET + EF + Npgsql instrumentation) exporting traces + metrics
  (OTLP → Grafana/Tempo/Prometheus or a hosted APM); alert on: failed backup, failed cascade job,
  5xx rate, readiness failures. Emit a metric on every estimate publish.
- **Files.** `api/Program.cs`, `docker-compose.prod.yml`, `deploy/`.
- **Acceptance.** Traces visible end-to-end; an induced backup failure raises an alert.

#### 19.4 — `S5` PITR (WAL archiving) + tested DR drill — **M**
- **Why.** Backups are daily `pg_dump` snapshots only → RPO up to 24h, weak for a financial
  system.
- **Steps.** Enable WAL archiving / continuous archiving (or managed-Postgres PITR); document and
  **rehearse** a point-in-time restore; record measured RPO/RTO in `docs/BACKUP.md`.
- **Files.** `docker-compose.prod.yml`, `deploy/backup/`, `docs/BACKUP.md`.
- **Acceptance.** A rehearsed PITR restores to a chosen timestamp; RPO/RTO documented.

#### 19.5 — `S6` Publish OpenAPI + versioned API + generated TS client — **M**
- **Why.** `lib/api.ts` is hand-written; no published contract or versioning → silent front/back drift.
- **Steps.** Emit OpenAPI from the minimal API, introduce `/api/v1` routing, generate the TS client
  into the web app (replacing hand-written calls incrementally), wire generation into CI.
- **Files.** `api/Program.cs`, `api/Endpoints/*`, `lib/` (generated client), `.github/workflows/ci.yml`.
- **Acceptance.** OpenAPI served; generated client compiles; a contract drift fails CI.

#### 19.6 — `S8` Frontend scale: split `page.tsx`, Playwright E2E, BOQ virtualization — **L**
- **Why.** `app/projects/[id]/page.tsx` is a ~1,471-line monolith; no E2E; large BOQs aren't
  virtualized (perf cliff on thousand-line bills).
- **Steps.** Decompose the page into focused components/hooks; add Playwright smoke E2E (login →
  build a small estimate → export) to CI; virtualize the BOQ/area grids (e.g. TanStack Virtual).
- **Files.** `app/projects/[id]/` (split), `e2e/` (new), `.github/workflows/ci.yml`,
  `package.json`.
- **Acceptance.** Page split with no behavior change; E2E green in CI; a 2,000-line BOQ scrolls smoothly.

#### 19.7 — `S9` Centralized server-side validation (FluentValidation) — **S**
- **Why.** Write-boundary validation is ad-hoc; no consistent guard against negative
  qty/rate/percentage.
- **Steps.** Add FluentValidation, validators for every write DTO (non-negative money/quantity,
  percentage ranges, required fields), returning ProblemDetails with field errors.
- **Files.** `api/Program.cs`, `api/Validation/` (new), `api/Endpoints/*`.
- **Acceptance.** Invalid inputs return a structured 400 with field-level messages, proven by tests.

### Conclusion & sequencing

The system is already a **correct, usable, multi-tenant estimating SaaS** with a sound tender
build-up and a hardened platform. To reach a **professional, enterprise-grade** standard, execute
the phases in order:

1. **Phase 17 (P0) first — non-negotiable.** Lock down calculation correctness (17.1), tender
   compliance (VAT 17.2, provisional/PC/alternate lines 17.3) and intra-tenant authorization (17.4).
   These protect the legally-binding number and close the only correctness/confidentiality risks.
2. **Phase 18 (P1) next — estimator leverage.** Target-price back-solve (18.1) and the margin-on-price
   view (18.2) are daily-use wins; dated rates + quotes (18.3) and the async cascade (18.4) add
   traceability and scale.
3. **Phase 19 (P2) — maturity & operability.** The bid register/win-rate loop (19.1) is the strategic
   differentiator; the rest (risk/S-curve, OTel, PITR, OpenAPI, frontend scale, validation) bring
   operational and engineering maturity.

**Recommended first PR: 17.1** — small, highest-risk area for a pricing tool, and it makes every
subsequent change provably safe. Each item above is independently shippable through the existing
PR-gated flow (branch → CI green → squash-merge → rebuild → smoke).
