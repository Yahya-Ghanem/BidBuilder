# BidBuilder

Multi-tenant **construction bid-estimating** web app. Break a project into a Bill of
Quantities, build up unit rates from a resource/assembly library or extensible cost
components (Material + Labor + Equipment + Waste % + Overheads % + custom), roll costs
up an Area → Sub-area → Unit breakdown, apply preliminaries and compounding markups to a
bid price, then export a branded priced-BOQ + bid summary (Excel / PDF / CSV).

## Stack

| Layer | Tech |
|------|------|
| Frontend | Next.js 16 (App Router) · React 19 · TypeScript · Tailwind v4 · TanStack Query |
| Backend | ASP.NET Core 8 minimal API · EF Core 8 · PostgreSQL 16 |
| Auth | JWT · multi-tenant (global query filters) · group/module RBAC |
| Exports | ClosedXML (Excel) · QuestPDF (PDF) |
| Infra | Docker Compose (web · api · db) |

## Key capabilities

- **Multi-tenancy** — every tenant-owned row is auto-scoped via EF global query filters.
- **RBAC** — per-module (projects, resource-library, assemblies, boq, …) view/add/edit/delete.
- **3-level scoping** — Tenant → Project → Estimate, with team-based project access.
- **Rate engine** — assembly unit-rate build-up, plus per-item extensible cost components.
- **Project areas** — nested location breakdown with a per-estimate cost roll-up.
- **Estimate workflow** — revisions, Draft → Published lifecycle lock, `xmin` optimistic concurrency (409).
- **FX** — present a bid in a second currency; rate frozen at publish.
- **Governance** — append-only audit trail with filters; referential-integrity delete guards.
- **Exports** — Excel / PDF (company branding, build-up detail, cost-by-area) and flat CSV.

## Run locally (Docker)

```bash
cp .env.example .env          # set POSTGRES_PASSWORD
docker compose up -d --build
```

- Web → http://localhost:3100
- API → http://localhost:8081
- Postgres → localhost:5433

The API applies EF migrations and seeds a demo tenant on startup:
**admin@bidbuilder.local / Admin@12345** (tenant `default`).

## Tests

Backend unit + HTTP integration tests run against a **real Postgres** (a throwaway
database created on the running compose instance, migrated, seeded, then dropped):

```bash
docker compose up -d db        # Postgres must be reachable
dotnet test api.Tests/BidBuilder.Api.Tests.csproj
```

The test fixture reads DB credentials from the environment, then the gitignored `.env`
(`POSTGRES_USER` / `POSTGRES_PASSWORD`), then non-secret defaults — no password is
committed. CI runs the same suite against a Postgres service container.

## Frontend type-check

```bash
npm ci
npx tsc --noEmit
```

## Layout

```
api/         ASP.NET Core API (Endpoints/, Services/, Models/, Data/, Migrations/)
api.Tests/   xUnit unit + integration tests
app/         Next.js routes
components/   React UI
lib/          API client, types, hooks
docs/         ROADMAP.md
```
