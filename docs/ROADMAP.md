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

---

## 11. Forward roadmap — Senior design review → professional UX grade

> Phases 20–24 (mobile, MFA/SSO, AI rate suggestion, email, API keys, templates, Arabic i18n,
> weekly digests, IP-allowlist, HMAC portal links, rate trends, anomaly detector, i18n deepening,
> template sharing, a11y CI gate, branding text) shipped between this section and the previous
> one — they are tracked in `memory/bidbuilder-project.md`. The four phases below come from a
> **senior-designer review of the live build** (47-surface walkthrough, June 2026) and address
> the visual/UX debt that accumulated while shipping breadth.
>
> **Headline findings:**
> 1. The project detail page stacks **14 sections** in one scroll (~5,000 px) — needs tabs.
> 2. The settings page stacks **14 cards** in one scroll (~6,000 px) — needs sub-navigation.
> 3. **Visible formatting bugs** in production demos (`AED 187,500.50 AED` on subcontractor
>    quotes, unlabelled `· 5` count in the areas tree, leftover test data) erode user trust.
> 4. **Color-contrast debt** (slate-400 muted text) deferred from Phase 24.4 still affects most
>    surfaces outside /login and /settings.
> 5. **Stat cards lack trend signals** — the headline bid price never shows ±% vs the previous
>    revision, despite that being the single number estimators iterate on.
>
> The platform is **functionally complete and technically solid** — Phases 25–28 are about
> presentation matching capability. Each phase is roughly 5 items, sized to fit the same
> 5-feature shipping cadence as Phases 20–24.

### Phase 25 — P0: design polish (visible-bug + IA fixes that block enterprise sale)

Highest-leverage UX work. Every item here is a small, frontend-only change that reshapes the
perceived quality of the product without any backend or schema change.

#### 25.1 — `D1` Money component + format audit (currency-dup fix) — **S**
- **Why.** The subcontractor-quotes page renders `AED 187,500.50 AED` (currency rendered twice);
  multiple surfaces use ad-hoc number formatting. Construction estimators triple-check money
  figures — visible formatting bugs break trust instantly.
- **Steps.** Introduce `<Money value={n} currency={c} />` as the single source of truth (locale-
  aware thousands separator, currency symbol position, secondary-currency suffix). Audit every
  place a money value renders (`grep "currency"` across `app/` and `components/`). Wire it.
- **Files.** `components/money.tsx` (new), every surface that displays money
  (`app/projects/[id]/_components/*`, `app/subcontractor-quotes/*`, `app/quotes/*`,
  `components/projects-tree.tsx`).
- **Acceptance.** A new unit test asserts `<Money value={187500.5} currency="AED" />` renders
  exactly `AED 187,500.50` with no duplicate suffix; visual QA on all 47 captured surfaces.

#### 25.2 — `D2` Settings sub-navigation (4 logical tabs) — **S**
- **Why.** The settings page is 14 cards stacked in a 6,000-px column. There is no visual
  grouping and no way to find a specific setting except by scrolling.
- **Steps.** Group cards into four tabs: **Company** (profile, logo, branding, custom domain) ·
  **Workspace** (estimating defaults, approvals, currencies, email/digest) · **Catalogues**
  (cost types, activities, project types, templates) · **Integrations** (SSO, webhooks, API
  keys). Sticky tab strip below the page title; deep-link via `?tab=company`.
- **Files.** `app/settings/page.tsx` (refactor — split out a `<SettingsTabs>` plus 4 panel
  components), `lib/i18n-core.ts` (new tab labels in en + ar).
- **Acceptance.** Each tab loads in a single viewport on 1080p; deep-link works; a11y gate
  stays green; lighthouse perf doesn't regress.

#### 25.3 — `D3` Project detail page — tab restructure — **M**
- **Why.** The single biggest UX issue. 14 sections stacked vertically on the project page
  forces a wall of scroll on a new estimator and wastes time for experienced ones.
- **Steps.** Restructure as: sticky project header + 4 stat cards always visible; below them, a
  tab strip: **Overview** (teams, areas, revisions) · **Estimate** (BOQ, prelims, markups,
  what-if, target) · **Insights** (cost-by-area, activities, anomalies, compare) · **Risk**
  (risk register, cash-flow S-curve) · **Activity** (approvals, audit excerpt, future comments).
  The Bid price card never leaves the screen.
- **Files.** `app/projects/[id]/page.tsx`, every `app/projects/[id]/_components/*.tsx` (no
  behaviour change, just regrouping into tab panels), `lib/i18n-core.ts`.
- **Acceptance.** Scroll distance on a fresh project drops from ~5,000 px to ~1,200 px; happy-
  path E2E still passes; a11y gate green; visual diff approved.

#### 25.4 — `D4` Revision bar consolidation — **S**
- **Why.** 14 controls in one strip (revision dropdown + 6 lifecycle buttons + status + show-in
  + date + 4 export buttons). Cognitive overload and risk of mis-click on irreversible actions.
- **Steps.** Collapse lifecycle actions (`New`, `Duplicate`, `Copy to…`, `Save as template`,
  `From template`, `Delete`) into one `Actions ▾` dropdown. Collapse exports
  (`Excel`, `CSV`, `PDF`, `Bid Letter`) into one `Export ▾` dropdown. Result: 4 controls
  visible — revision dropdown, status, Actions, Export.
- **Files.** `app/projects/[id]/_components/estimates-section.tsx`,
  `components/ui.tsx` (add `DropdownButton` if not present).
- **Acceptance.** Bar fits in one row on 1366-px viewport without wrapping; every action still
  reachable in ≤ 2 clicks; happy-path E2E green.

#### 25.5 — `D5` Stat-card trend deltas + per-m² toggle — **S**
- **Why.** The Bid price card shows `SAR 2,724,499.41` with no indication of direction vs
  previous revision. For a tool whose whole purpose is iterating on price, this is missed
  information. Estimators also want cost-per-m² visible.
- **Steps.** Add a small `<DeltaBadge>` to each stat card: `↑ 4.2% vs Rev 1` (green if better
  for the user — usually lower direct cost / higher bid). Add a per-m² toggle on the stat-card
  row that divides totals by the top-level area's quantity when areas exist.
- **Files.** `app/projects/[id]/_components/stat-cards.tsx` (new),
  `app/projects/[id]/_components/estimates-section.tsx`,
  `api/Endpoints/EstimateEndpoints.cs` (add prev-revision delta to breakdown DTO).
- **Acceptance.** Delta badge visible when ≥ 2 revisions exist; per-m² toggle round-trips a
  preference per estimate; tests cover the % calculation.

### Phase 26 — P0: design-system foundation (compound interest for every later feature)

Before more features ship, codify the visual primitives so the next batch lands faster and
more consistently. Every line of CSS variable / shared component pays back compound interest.

#### 26.1 — `D6` Design tokens — `--text`, `--danger`, `--warning`, `--success`, `--space-N`, `--radius-N` — **S**
- **Why.** `app/globals.css` already defines `--brand` / `--bg` / `--card` / `--border` /
  `--muted`. But literal Tailwind colors (`text-rose-600`, `text-amber-600`) appear in dozens
  of components. Future palette work is a 30-file PR; with tokens it's a one-file PR.
- **Steps.** Extend `globals.css` with the missing tokens (text, semantic colors, spacing
  scale, radius scale). Add a Tailwind v4 `@theme` block exposing them. Replace literal usages
  in the components touched by Phase 25 first; opportunistic replacement elsewhere.
- **Files.** `app/globals.css`, `tailwind.config.ts` (or v4 `@theme`), components touched.
- **Acceptance.** Every semantic color used in Phase 25 components routes through a token;
  `grep "text-rose-600\|text-amber-600\|text-emerald-"` in touched files returns nothing.

#### 26.2 — `D7` Color contrast sweep — close the 24.4 deferred debt — **M**
- **Why.** Phase 24.4 a11y gate intentionally deferred the slate-400 → slate-500 sweep across
  `/projects` and `/projects/[id]`. Dozens of hint texts in the BOQ, areas, activities, and
  cost-by-area panels still fail WCAG AA (2.6:1 vs 4.5:1 required).
- **Steps.** Replace every `text-slate-400` on text < 14pt with `text-[var(--muted)]` (already
  slate-500, 4.78:1 on white). Audit dark badges and icons separately — icons under 14pt also
  need 4.5:1 against background. Flip `A11Y_INCLUDE_SERIOUS=1` on the a11y spec to lock the
  new bar in CI.
- **Files.** All `app/projects/[id]/_components/*.tsx`, `components/projects-tree.tsx`,
  `app/resources/page.tsx`, `tests-e2e/a11y.spec.ts`.
- **Acceptance.** `A11Y_INCLUDE_SERIOUS=1 npx playwright test tests-e2e/a11y.spec.ts` returns
  0 violations; the existing critical-only CI gate stays green; visual regression negligible.

#### 26.3 — `D8` Typography scale — promote primary body text to 14px — **S**
- **Why.** The product currently uses `text-xs` (12px) and `text-sm` (14px) heavily for primary
  content. 12px is fine for hint text but uncomfortable for primary labels and form values
  during long sessions.
- **Steps.** Audit usages: keep `text-xs` for hint / caption / metadata; bump primary form
  labels and table cell content from `text-xs` → `text-sm`. Define the scale in `globals.css`
  comments so future contributors don't drift.
- **Files.** `components/projects-tree.tsx`, all `app/projects/[id]/_components/*.tsx`,
  `app/settings/*.tsx`, `app/resources/page.tsx`, `app/admin/page.tsx`.
- **Acceptance.** Primary content readable at arm's length on a 1080p screen; no a11y regression;
  layout/spacing visually unchanged (text-sm has same line-height bucket as text-xs).

#### 26.4 — `D9` `<DataTable>` component — single dense-grid wrapper — **M**
- **Why.** The BOQ table, area roll-up, cost-by-area, audit log, quotes register, subcontractor
  quotes, users table — all hand-rolled, each subtly different. A single `<DataTable>` lets us
  add column resize, sticky header, sort, and selection in one place.
- **Steps.** Wrap a slim API around TanStack Table (already used by virtualization elsewhere).
  Build the BOQ table on it as the first consumer; migrate other tables opportunistically.
- **Files.** `components/data-table.tsx` (new), `app/projects/[id]/_components/boq.tsx`
  (first consumer).
- **Acceptance.** BOQ scrolls smoothly with sticky header on a 1,000-row example; selection
  state for future bulk actions exposed via callback; happy-path E2E green.

#### 26.5 — `D10` `<EmptyState>` component + 6 first-time screens — **M**
- **Why.** A brand-new tenant lands on empty Projects, Resources, Assemblies, Quotes, etc. and
  sees a blank page with no guidance. Every empty state today is just *"No items yet."*
- **Steps.** Build `<EmptyState illustration title body cta />`. Design 6 empty states:
  Projects · Resources · Assemblies · Subcontractor Quotes · Quotes Register · Templates.
  Line-art SVG illustrations to keep file size tiny.
- **Files.** `components/empty-state.tsx` (new), `public/illustrations/*.svg` (new),
  every surface listed above.
- **Acceptance.** A fresh tenant can read what each surface does without opening docs; the CTA
  on each state opens the relevant create modal; a11y gate green.

### Phase 27 — P1: collaboration & honest mobile

Now that the IA is fixed and the design system codified, address the soft features that turn
the product from "complete tool" into "team-ready platform."

#### 27.1 — `C1` BOQ line comments + @mentions — **L**
- **Why.** Estimators currently use Slack/email to discuss specific BOQ lines. The status
  workflow ("Under review") implies an internal review process the UI doesn't actually
  support. A native commenting thread per line closes the gap.
- **Steps.** New `BoqLineComment` model (author, body, parentCommentId, mentions); endpoints
  `GET/POST/DELETE /api/estimates/{eid}/items/{iid}/comments`; small icon at the end of each
  BOQ row that opens a side-panel thread; @mention fires a notification using the existing
  notification system (20.3).
- **Files.** `api/Models/BoqLineComment.cs`, `api/Endpoints/CommentEndpoints.cs`,
  migration **AddBoqLineComments**, `app/projects/[id]/_components/boq.tsx`,
  `app/projects/[id]/_components/comment-panel.tsx` (new), tests.
- **Acceptance.** A reviewer leaves a comment on item id=N; the author gets a notification;
  resolving the comment removes it from the open-comments count; permissions: anyone with boq
  View can comment, only author + admin can delete.

#### 27.2 — `C2` Live presence cues on a revision — **M**
- **Why.** You already have optimistic concurrency (xmin → 409). But there's no proactive cue:
  if Sarah is editing right now, Ahmed only finds out when his save fails. For a multi-person
  bid this is friction-by-default.
- **Steps.** Lightweight presence — 30 s heartbeat ping (`POST /api/estimates/{id}/presence`)
  while the user is on the editor; `GET` returns the list of recent presences (last 90 s);
  small avatar cluster in the revision bar shows "Sarah is also viewing." Server-side cache,
  no SignalR required for v1.
- **Files.** `api/Endpoints/PresenceEndpoints.cs` (new), `lib/usePresence.ts` (new),
  `app/projects/[id]/_components/estimates-section.tsx`.
- **Acceptance.** Two browsers on the same revision see each other within ~30 s; presence
  drops within 90 s of leaving the page; no DB writes beyond cache invalidation.

#### 27.3 — `C3` Mobile honest-mode banner + read-only enforcement — **S**
- **Why.** Phase 20.7 shipped mobile-responsive reads, but the BOQ table, build-up modal, and
  what-if panel don't work usefully on a phone. Pretending they do is worse than admitting it.
- **Steps.** Detect viewport ≤ 768 px on edit surfaces; show a sticky banner: *"Editing BOQ
  requires a larger screen — switch to read-only view"*; hide edit affordances (add, delete,
  inline-edit cells) below that threshold while preserving all read views.
- **Files.** `lib/useViewport.ts` (new), `app/projects/[id]/_components/boq.tsx`,
  every other edit surface inside `app/projects/[id]/_components/`.
- **Acceptance.** A user on a 390-px viewport sees the read view + banner; can still tap a
  stat card to scroll; can still approve a bid. A 1024-px viewport sees the normal editor.

#### 27.4 — `C4` Notifications grouping + unread separator — **S**
- **Why.** The bell dropdown is a raw event list. Three approval requests appear as three
  separate items; "publish" events from the same project aren't grouped; there's no read /
  unread separator.
- **Steps.** Add `readAt` to notifications (migration); group by entity-type + entity-id in the
  dropdown; show "Unread (N)" header then "Earlier"; mark-all-read button.
- **Files.** `api/Models/Notification.cs`, migration **AddNotificationReadAt**,
  `api/Endpoints/NotificationEndpoints.cs`, `components/notification-bell.tsx`.
- **Acceptance.** 5 publish events on the same project show as one collapsed row with count;
  unread bold, read normal; mark-all-read updates the bell counter to 0.

#### 27.5 — `C5` Project favorites / pinned + recent — **S**
- **Why.** For a user with 50+ projects, finding the active 2-3 is painful. The sidebar tree
  groups by type but offers no personalization.
- **Steps.** Add per-user `pinnedProjectIds` to a new `UserPreferences` model; "Pin" action
  on the project header; pinned projects appear at the top of the sidebar tree in a "Pinned"
  section; "Recent" section right below shows last-5 visited.
- **Files.** `api/Models/UserPreferences.cs`, migration **AddUserPreferences**,
  `api/Endpoints/UserPreferenceEndpoints.cs`,
  `components/projects-tree.tsx`, `app/projects/[id]/page.tsx`.
- **Acceptance.** Pinning a project from any session moves it to the Pinned section across
  every session of the same user; recent list updates on every project-page visit.

### Phase 28 — P2: power-user features (after IA & system foundation)

Higher-effort or lower-frequency items deliberately deferred so they don't compete with the
foundation. Each is independently shippable.

#### 28.1 — `P1` Dark mode — **M**
- **Why.** Long sessions in dense data tables benefit from reduced eye strain. The token
  refactor in 26.1 makes dark mode a re-skinning exercise, not a rewrite.
- **Steps.** Add a `data-theme="dark"` set of CSS variable overrides in `globals.css`; system
  preference detection + manual override in the user menu; persist per user.
- **Files.** `app/globals.css`, `components/theme-toggle.tsx` (new), `lib/useTheme.ts` (new),
  every component verified for contrast in both themes (a11y gate now also runs dark).
- **Acceptance.** Toggle in 200 ms with no flash; a11y gate green for both themes; brand teal
  legible in both.

#### 28.2 — `P2` Bulk actions in BOQ — multi-select rows — **M**
- **Why.** Power users want to copy / move / delete several BOQ rows at once. The
  `<DataTable>` selection callback from 26.4 already exposes the state.
- **Steps.** Add a sticky action bar that appears when ≥ 1 row is selected: Delete, Move to
  section…, Duplicate, Tag with area. Endpoint accepts a list of item ids.
- **Files.** `app/projects/[id]/_components/boq.tsx`,
  `api/Endpoints/EstimateEndpoints.cs` (bulk endpoint).
- **Acceptance.** Selecting 10 lines and deleting them is one click instead of 10; happy-path
  E2E green; tests cover bulk endpoint authorisation per-line.

#### 28.3 — `P3` Export preview before download — **M**
- **Why.** A user clicks PDF and 60 KB lands in their downloads. If the data is wrong they
  send a broken document to the client. A preview catches mistakes in the loop.
- **Steps.** Render exports to a preview modal first (PDF.js for PDF, an Excel-to-HTML
  preview for xlsx) with a Download button. Add `?preview=1` query param on the existing
  export endpoints.
- **Files.** `app/projects/[id]/_components/estimates-section.tsx`,
  `components/export-preview-modal.tsx` (new).
- **Acceptance.** Clicking PDF opens an inline preview; clicking Download saves the file;
  user can close the preview without downloading.

#### 28.4 — `P4` Bid-letter templates — pick from 2–3 styles — **M**
- **Why.** The bid letter currently has one fixed layout. Different clients (government,
  private, international) expect different cover-letter tones / formats.
- **Steps.** Add a `BidLetterTemplate` model with a name + QuestPDF layout reference. Ship
  three: Formal (current), Concise, International (English+Arabic columns). User picks at
  generate time; admin can mark one as default.
- **Files.** `api/Models/BidLetterTemplate.cs`, `api/Services/ExportService.cs` (extract
  per-style renderers), `app/projects/[id]/_components/bid-letter-modal.tsx`.
- **Acceptance.** All three styles render with the same project data; tenant default
  preserved; existing branding text (24.5) honoured in all three.

#### 28.5 — `P5` Search palette upgrades — recent + filters + actions — **S**
- **Why.** The search palette is great but minimal. Power users want recent items + entity-
  type filters (`@project`, `@assembly`) + actions (`new project…`).
- **Steps.** Top section shows last-5 visited entities when query is empty; typing `@p` filters
  to projects only; typing `>` switches to action mode (`> new project`, `> sign out`).
- **Files.** `components/search-palette.tsx`,
  `lib/useRecentEntities.ts` (new).
- **Acceptance.** Recent shows after Ctrl+K with no query; `@p Sun` returns only Sunrise project;
  `> sign` shows the sign-out action; all keyboard-navigable.

### Conclusion & sequencing (Phases 25–28)

The product is **functionally complete and technically solid** after Phases 17–24. The four
phases above are about **presentation matching capability** — the visual + IA + system foundation
work that turns a feature-complete platform into a polished, enterprise-grade product.

1. **Phase 25 (P0) — design polish — do first.** Five small, frontend-only items that fix the
   most visible friction (the 14-section project page, the 14-card settings page, the
   currency-dup bug, the overloaded revision bar, the missing trend signals). Two-week sprint.
2. **Phase 26 (P0) — design-system foundation — do second.** Codify tokens, finish the a11y
   color-contrast debt from 24.4, promote body type to 14 px, extract `<DataTable>` and
   `<EmptyState>`. Every later phase becomes faster and more consistent. Two-week sprint.
3. **Phase 27 (P1) — collaboration & honest mobile.** Once the IA is fixed and the system is
   coherent, layer on BOQ line comments, presence cues, mobile honest-mode, notifications
   grouping, project pins. Two-to-three-week sprint.
4. **Phase 28 (P2) — power-user features.** Dark mode, bulk BOQ actions, export preview,
   bid-letter templates, search palette upgrades — all on top of the foundation from 25–26.
   Two-week sprint.

**Recommended first PR: 25.1** — the `<Money>` component + currency-dup audit. Half a day of
work, immediate trust impact, and zero coupling to the larger IA refactors. Every subsequent
phase compounds on the same proven flow: branch → CI green (backend + frontend + a11y +
happy-path) → squash-merge → rebuild → smoke.

---

## 12. Forward roadmap — Senior-auditor review → production-grade engineering uplift

After Phase 28 closed, an independent senior-software-auditor pass graded the system at
**7.5 / 10** — a strong product-grade score, but not yet "I'd bet a regulated customer on it."
The audit found that BidBuilder has very strong foundations (multi-tenant isolation, MFA + SSO,
optimistic concurrency, OTel, a11y + i18n CI gates, PITR claimed, dark mode, presence + comments)
but rough edges in **test isolation, supply-chain hygiene, mobile completeness, server-synced
preferences, change-management discipline, and external validation.**

The 10/10 ceiling is a category error — living software always has unknown unknowns. The
realistic ceiling is **9.5**, and even that requires external validation (pen test + SOC 2 / ISO
27001). Phase 29 is the path from **7.5 → 9.5**, broken into four tiers. Each item must produce
**auditable evidence in the repo or runbooks**, not just intent.

### Phase 29 — P0: production-grade engineering uplift (audit-driven)

Each tier is independently shippable. Tier A is mandatory hygiene; tiers B–D escalate into
discipline and external validation.

---

#### Tier A — `P0` close the obvious gaps (target: 7.5 → 8.0) — **2–3 weeks**

> **Status (2026-06-11):** Tier A **closed at 8.0 / 10**. See
> [docs/PHASE-29A-CLOSEOUT.md](./PHASE-29A-CLOSEOUT.md) for evidence.
> Shipped: 29.A.1 (PR #131, `515b160`), 29.A.2 (PR #132, `656932e`),
> 29.A.4 (PR #146, `ffe2291`). Deferred: 29.A.3 → Tier B.

##### 29.A.1 — ✅ **DONE** — `P0` Per-spec E2E test users — kill the shared-admin coupling — **M**
- **Why.** Every E2E spec currently logs in as `admin@bidbuilder.local`. With 13 specs from
  one runner IP, the 10/60s login rate-limit was tripped, and the symptom-fix in commit
  `f835459` was to bump the CI limit to 200/IP — a security control was loosened to paper
  over fragile tests. Per-spec users remove the coupling at the root.
- **Steps.** Add a `Development`-only `POST /api/test/users` endpoint that mints a scoped
  test user inside the default tenant. Each spec calls it in a `beforeAll`, uses the token,
  and tears down in `afterAll`. Revert the `RateLimiting__Login__PermitLimit: 200` override
  once green.
- **Files.** `api/Endpoints/TestSupportEndpoints.cs` (new, dev-only), `tests-e2e/_helpers/auth.ts`
  (new), every `tests-e2e/*.spec.ts`, `.github/workflows/ci.yml`.
- **Acceptance.** CI E2E runs `workers: 4` cleanly. No spec references `admin@bidbuilder.local`.
  Default login rate limit returns to 10/60s on CI.
- **Evidence-to-audit.** Grep `tests-e2e/` returns zero matches for `admin@bidbuilder.local`.
  `f835459` reverted in a follow-up commit.

##### 29.A.2 — ✅ **DONE** — `P0` Dependency scanning & SBOM — **M**
- **Why.** Public repo, paying-customer codebase, no automated dependency-vulnerability scan
  in CI. CVE in npm or NuGet today would land in prod silently.
- **Steps.** Add `.github/dependabot.yml` (npm + nuget + docker + github-actions); add CodeQL
  workflow on PR + weekly; add Trivy container scan in the docker-build job; document a
  CVE-triage SLA in `SECURITY.md` (e.g., critical < 7d, high < 30d). Generate SBOM (CycloneDX
  or SPDX) per build, attach as an artifact.
- **Files.** `.github/dependabot.yml`, `.github/workflows/codeql.yml`,
  `.github/workflows/trivy.yml`, `SECURITY.md`.
- **Acceptance.** First Dependabot PR opens within 24h. CodeQL run posts on every PR. Trivy
  fails the build on a `HIGH` finding without an explicit allow-list entry.
- **Evidence-to-audit.** Last 5 Dependabot PRs are merged or triaged with a reason logged —
  not stale.

##### 29.A.3 — ⏸️ **DEFERRED to Tier B** — `P0` Server-synced user preferences — recent entities + theme — **M**
- **Why.** `useRecentEntities` is localStorage-only. Two tabs / two devices show different
  recents; the design promise of "see what you were just working on" breaks on every reopen.
  The `UserPreferences` model from 27.5 already exists — extend it.
- **Steps.** Add `UserPreferences.RecentEntities` JSONB column + EF migration. New endpoints:
  `GET /api/me/preferences/recent`, `PUT` with the LRU list. localStorage becomes a
  write-through cache, server is source of truth. Same pattern for `theme` if not already
  server-backed.
- **Files.** `api/Models/UserPreferences.cs`, `api/Migrations/<date>_AddRecentEntitiesPref.cs`,
  `api/Endpoints/UserPreferencesEndpoints.cs`, `lib/useRecentEntities.ts`,
  `lib/useRecentEntities.test.ts`.
- **Acceptance.** Log in on machine A, visit a project. Log in on machine B — palette shows
  that project at top. E2E covers cross-session sync.
- **Evidence-to-audit.** `useRecentEntities` no longer reads localStorage on first mount when
  online; it fetches.

##### 29.A.4 — ✅ **DONE** — `P0` Security headers + CSP — **S**
- **Why.** Public-facing SaaS without a Content-Security-Policy, HSTS, and frame-options is
  one XSS away from a credential-stealer landing on a customer browser.
- **Steps.** Add header middleware on the API (and verify the Next.js side via
  `next.config.ts` `headers()`): `Content-Security-Policy` (strict, with explicit script-src
  for the bundled web), `Strict-Transport-Security` (max-age 1y, includeSubDomains, preload),
  `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`,
  `Permissions-Policy` (deny camera/mic/geolocation by default), `X-Content-Type-Options: nosniff`.
- **Files.** `api/Program.cs` (header middleware), `next.config.ts`.
- **Acceptance.** `securityheaders.com` returns A+ for the production hostname. CSP nonce
  on inline scripts; no `unsafe-inline` except where strictly required and documented.
- **Evidence-to-audit.** Screenshot of A+ result attached to the PR.

##### 29.A.5 — ✅ **DONE** — `P0` Tier-A close-out — verify, regress, PR, merge — **S**
- **Why.** Tier A removes the symptom-fix, adds CI scanning, syncs prefs, hardens headers.
  Validate as a coherent bundle.
- **Steps.** Full regression (vitest + axe + Playwright). Confirm `RateLimiting__Login`
  reverted. Confirm Dependabot, CodeQL, Trivy all green on the merge PR.
- **Acceptance.** Senior auditor re-grade with evidence ⇒ **8.0–8.2 / 10**.

---

#### Tier B — `P1` production discipline (target: 8.0 → 8.5) — **3–4 weeks**

##### 29.B.1 — `P1` Feature flags + staged rollout — **L**
- **Why.** Every Phase 25–28 PR merged straight to main and hit every tenant on the next
  `docker compose up`. A bad release blast-radius is currently *all tenants*. Need a
  per-tenant kill switch + gradual rollout.
- **Steps.** Pick a provider (GrowthBook self-hosted, Unleash self-hosted, or a homegrown
  `FeatureFlag` table — recommend GrowthBook for the eval UI). Wire the SDK on both API
  (`IFeatureService`) and web (`useFlag()`). Gate at least three real recent features
  (e.g., bid-letter templates, anomaly panel, export preview) behind flags with a documented
  rollout plan: 10% tenants → 50% → 100%. Add a kill-switch test in CI that asserts a
  disabled flag actually hides the surface.
- **Files.** `api/Services/FeatureService.cs`, `lib/useFlag.ts`, `docker-compose.yml`
  (growthbook service), `tests-e2e/feature-flag.spec.ts`.
- **Acceptance.** Toggling a flag in the GrowthBook UI flips the feature in < 60s without
  a redeploy. CI asserts kill-switch behavior.

##### 29.B.2 — `P1` Load testing — k6 scenarios + P95 budgets — **M**
- **Why.** We don't know the system's real ceiling. "It works for one user" is not a
  capacity statement. Need named scenarios with SLO budgets and CI tracking.
- **Steps.** Add `k6/` folder with three scenarios: (1) 100 concurrent users editing the
  same BOQ for 10 minutes; (2) 1000 concurrent estimate reads against the seeded project;
  (3) sustained 50 RPS mixed workload for 1 hour. Run nightly against a CI-spun stack.
  Publish results to a dashboard or commit to a `k6/results/` log. Define P95 latency
  budgets per endpoint class (read < 200ms, write < 500ms, export < 5s).
- **Files.** `k6/edit-boq.js`, `k6/read-estimates.js`, `k6/mixed-workload.js`,
  `.github/workflows/loadtest-nightly.yml`, `docs/PERFORMANCE-SLOS.md`.
- **Acceptance.** Nightly run posts P95s to a tracked file; budget regression fails the build.

##### 29.B.3 — `P1` Mobile beyond read-only — edit on small screens — **L**
- **Why.** 27.3 added an "honest-mode" banner that explicitly disables editing on ≤ 768px.
  That's defensible scoping, not a ceiling we should stay under forever. Mobile estimators
  on-site want at least: inline BOQ row edit, post a comment, approve.
- **Steps.** Audit edit surfaces, redesign three for mobile: BOQ inline rate edit, comment
  posting, approval action. Add Playwright specs at `viewport: { width: 375, height: 812 }`.
  Remove the honest-mode banner from those three surfaces; keep it on the genuinely
  desktop-only ones (e.g., full estimate-compare panel).
- **Files.** `app/projects/[id]/_components/boq.tsx`, comment panel, approval card,
  `tests-e2e/mobile-edit.spec.ts`.
- **Acceptance.** Mobile Playwright spec drives a full edit-and-save round-trip. Honest-mode
  banner only shows on truly desktop-only surfaces.

##### 29.B.4 — `P1` Synthetic monitoring — real user journey every 60s — **M**
- **Why.** `/healthz` says "API booted." A synthetic check that logs in, opens a project,
  saves a BOQ row, and logs out says "the product works." This is the difference between
  ping and SLO.
- **Steps.** Pick a provider (Better Stack / Checkly / Datadog Synthetic). Author one
  scripted browser journey covering the critical path. Run every 60s from 3 geographies.
  Wire to PagerDuty with a documented escalation policy.
- **Files.** `synthetics/critical-path.spec.ts` (provider-specific), `docs/ONCALL.md`.
- **Acceptance.** A simulated outage (block traffic for 90s) triggers a PagerDuty page
  within 2 minutes.

##### 29.B.5 — `P1` Automated backup-restore drill — **M**
- **Why.** 19.4 added PITR + WAL archiving. An untested backup is a story, not a control.
  Need an automated monthly restore that verifies row counts and posts a diff.
- **Steps.** Cron job (GitHub Action or k8s CronJob): spin a scratch Postgres, restore last
  backup, compare `\dt` row counts to a snapshot, post the diff to a Slack/Teams channel.
  Fail on > 0.1% row drift unexplained.
- **Files.** `.github/workflows/restore-drill-monthly.yml`, `ops/restore-drill.sh`,
  `docs/RUNBOOKS/restore.md`.
- **Acceptance.** First drill runs green; subsequent drift triggers an alert within 1 hour.

##### 29.B.6 — `P1` Tier-B close-out — verify, regress, PR, merge — **S**
- **Acceptance.** Senior auditor re-grade ⇒ **8.5 / 10**.

---

#### Tier C — `P2` incident & change discipline (target: 8.5 → 9.0) — **4–6 weeks**

##### 29.C.1 — `P2` Incident-response framework — runbooks + game-day — **L**
- **Why.** Today there is no documented severity matrix, no per-failure runbook, no logged
  drill. The first real incident will be improvised. That is the wrong time to invent the
  playbook.
- **Steps.** Author `docs/INCIDENT-RESPONSE.md` with SEV1/2/3 matrix (impact × scope ×
  duration). Author runbooks for the top 8 failure modes (API down, DB unreachable, migration
  stuck, auth provider outage, Hangfire dead, disk full, certificate expired, Redis down).
  Establish on-call rotation in PagerDuty. Run a **logged quarterly game-day** with a
  written postmortem.
- **Files.** `docs/INCIDENT-RESPONSE.md`, `docs/RUNBOOKS/*.md`, `docs/POSTMORTEMS/`.
- **Acceptance.** A new engineer can resolve a SEV2 from the runbook alone. Two completed
  quarterly drills logged with postmortems.

##### 29.C.2 — `P2` Change management — rollback documented per deploy — **M**
- **Why.** Phase 28 shipped 5 features to main without a documented rollback for any.
  Every prod-bound PR must include the rollback command, the post-deploy verification step,
  and the owner who approves the deploy.
- **Steps.** Add a PR template that mandates: rollback command, post-deploy verify command,
  feature-flag gate (links 29.B.1), data-migration reversibility note. Wire a deploy script
  that requires the verify step to pass before marking the deploy successful. Add a deploy
  log table (`audit_deploys`) tracking who, when, what, rollback result.
- **Files.** `.github/PULL_REQUEST_TEMPLATE.md`, `ops/deploy.sh`,
  `api/Migrations/<date>_AddAuditDeploys.cs`.
- **Acceptance.** No PR can merge to main without the template fields filled. Rollback
  drill (revert last green) takes < 5 minutes.

##### 29.C.3 — `P2` Blast-radius bulkheads — per-tenant rate limit + circuit breakers — **L**
- **Why.** One tenant's heavy export job can starve everyone else. One slow query can take
  the API down. Need per-tenant rate limiting on heavy endpoints, and circuit breakers
  around external calls (SMTP, webhook send, FX refresh).
- **Steps.** Extend the existing `AddRateLimiter` setup to partition by tenant id on
  `/exports/*` and `/recompute/*`. Wrap SMTP/webhook/FX in Polly circuit breakers with
  observability (OTel spans showing open/closed transitions). Add a test that proves
  tenant A's hammering doesn't impact tenant B's P95.
- **Files.** `api/Program.cs`, `api/Services/EmailService.cs`, `api/Services/WebhookSender.cs`,
  `api.Tests/IsolationTests.cs`.
- **Acceptance.** Load test (links 29.B.2) confirms cross-tenant isolation within budget.

##### 29.C.4 — `P2` Data classification + GDPR/PDPL deletion CLI — **M**
- **Why.** No column is tagged PII / Confidential / Internal / Public. A "right-to-be-
  forgotten" request from a UAE / EU user today requires manual SQL archaeology.
- **Steps.** Tag every column via an EF model attribute `[DataClass(DataClass.PII)]` (or a
  schema doc if attributes are too invasive). Export endpoints honor classification (redact
  PII unless caller has `pii:read`). CLI: `dotnet run --project api -- forget --user-id N`
  hard-deletes PII while preserving the audit trail (anonymized).
- **Files.** `api/Models/Attributes/DataClass.cs`, every model file, `api/Tools/ForgetUser.cs`.
- **Acceptance.** Running the forget command on a test user removes PII, preserves audit
  rows with anonymized identifiers, exports redact correctly.

##### 29.C.5 — `P2` Observability SLOs + error-budget burn-rate alerts — **M**
- **Why.** OTel ships telemetry; nothing converts that into SLOs. Alerts today are
  threshold-based, which is the wrong signal for "are we eating the budget too fast."
- **Steps.** Define SLOs per critical path: login success ≥ 99.9% / 30d, save success
  ≥ 99.95% / 30d, export P99 < 10s / 30d. Configure burn-rate alerts (fast 1h + slow 6h
  pages) in Grafana / Datadog. Publish SLO dashboard.
- **Files.** `docs/SLO.md`, `ops/grafana/dashboards/slo.json`.
- **Acceptance.** A simulated burn (kill the auth pod) pages within 5 minutes via the fast
  burn-rate window.

##### 29.C.6 — `P2` Tier-C close-out — verify, regress, PR, merge — **S**
- **Acceptance.** Senior auditor re-grade ⇒ **9.0 / 10**.

---

#### Tier D — `P3` external validation (target: 9.0 → 9.5) — **6–12 months calendar**

This is the wall. These items require external parties and calendar time, not just code.

##### 29.D.1 — `P3` Third-party penetration test — **L**
- **Why.** Internal review doesn't substitute for adversarial expertise. Hire Cure53, NCC
  Group, Bishop Fox, or equivalent. Triage findings to closure. Re-test passes.
- **Acceptance.** Public summary report (sanitized) showing all critical/high findings
  closed; pen-test re-test green.

##### 29.D.2 — `P3` SOC 2 Type II *or* ISO 27001 — **L**
- **Why.** "We have controls" needs an external attestation. Type II requires 6+ months of
  observation period — start early.
- **Steps.** Engage an auditor (Drata / Vanta / SecureFrame to accelerate evidence
  collection). Implement the controls gap. Pass the observation period. Publish the
  certificate.
- **Acceptance.** Type II report or ISO 27001 certificate on file; access-review cadence is
  logged and signed, not just talked about.

##### 29.D.3 — `P3` Chaos testing — **M**
- **Why.** Resilience claimed is not resilience verified. Kill pods mid-save. Kill the DB
  primary. Watch failover. Document what broke.
- **Steps.** Adopt a chaos tool (Chaos Mesh / LitmusChaos / homegrown scripts). Schedule
  monthly drills against staging. Log every drill outcome.
- **Acceptance.** 12 monthly drills logged in `docs/CHAOS-LOG.md` with what broke and what
  was fixed.

##### 29.D.4 — `P3` Reproducible builds + signed artifacts + SLSA — **M**
- **Why.** Supply-chain attacks land via build infrastructure. Reproducible builds + signed
  containers + provenance makes substitution detectable.
- **Steps.** Generate SBOM (CycloneDX) per build (extends 29.A.2). Sign container images
  with cosign. Adopt SLSA Level 2+ provenance attestation in the workflow.
- **Acceptance.** `cosign verify` passes on every prod image; provenance is
  inspectable via `slsa-verifier`.

##### 29.D.5 — `P3` Public status page — **S**
- **Why.** When customers ask "is it me or you?" they should self-serve. A green-forever
  status page is a worse signal than no status page at all.
- **Steps.** Stand up `status.bidbuilder.com` (StatusPage.io, Better Stack, or self-hosted
  Atlassian Statuspage). Wire synthetic checks from 29.B.4 as the signal source. Publish
  every SEV1/SEV2 incident with an RCA within 5 business days.
- **Acceptance.** Page lists at least one historical incident with a real RCA, not just
  green ticks.

##### 29.D.6 — `P3` Tier-D close-out — verify, regress, external attestations, announce — **S**
- **Acceptance.** Senior auditor re-grade with full evidence pack ⇒ **9.3–9.5 / 10**.

---

### Conclusion & sequencing (Phase 29)

The audit set the bar honestly: **7.5 today, 9.5 ceiling, 10 doesn't exist for living
software.** Phase 29 is the path between those two numbers, broken into four tiers that
correspond to four distinct disciplines:

1. **Tier A — engineering hygiene** (2–3 weeks). Per-spec test users, Dependabot + CodeQL +
   Trivy, server-synced prefs, security headers. Closes the "symptom-fix" smell that the
   audit flagged in commit `f835459`. Re-grade target: **8.0**.
2. **Tier B — production discipline** (3–4 weeks). Feature flags, k6 load tests, mobile
   beyond read-only, synthetic monitoring, automated restore drill. Re-grade target: **8.5**.
3. **Tier C — incident & change discipline** (4–6 weeks). Runbooks + game-days, change
   management with rollback, blast-radius bulkheads, data classification + deletion CLI,
   SLO burn-rate alerts. Re-grade target: **9.0**.
4. **Tier D — external validation** (6–12 months calendar). Pen test, SOC 2 / ISO 27001,
   chaos testing, signed artifacts + SLSA, public status page. Re-grade target: **9.3–9.5**.

**Recommended first PR: 29.A.1** — the per-spec test-user helper. It removes the security
control we loosened in `f835459`, unblocks parallel CI, and is the cleanest demonstration
that the audit findings are being addressed at the **root cause**, not the symptom.

Every subsequent item compounds on the same flow used in Phases 17–28: branch → CI green
(backend + frontend + a11y + happy-path) → squash-merge → rebuild → smoke → close-out task.
The discipline doesn't change. The bar rises.
