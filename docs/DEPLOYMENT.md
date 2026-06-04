# Deployment checklist

BidBuilder is a containerized ASP.NET Core API + Next.js frontend on PostgreSQL.
This is the minimum to take it from local dev to a real (staging/prod) deployment.

## Secrets (do this first)

The API **refuses to boot** outside `Development` unless these are set to strong,
non-default values (enforced in `Program.cs`):

| Config key | Env var (double-underscore) | Notes |
|---|---|---|
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | Full Npgsql connection string. Required (no fallback in prod). |
| `Jwt:SigningKey` | `Jwt__SigningKey` | ≥ 32 chars, **not** a `CHANGE-ME`/`dev-only` placeholder. |
| `AllowedOrigins` | `AllowedOrigins` | Comma-separated web origins. Required in prod; **must not** contain `localhost`/`127.0.0.1`. |
| `Seed:AdminPassword` | `Seed__AdminPassword` | Initial admin password. **Required to seed the first admin in prod** — no default credential is created without it. |
| `Seed:AdminEmail` | `Seed__AdminEmail` | Optional. Initial admin email (default `admin@bidbuilder.local`). |
| `Seed:DemoData` | `Seed__DemoData` | Optional `true`/`false`. Seeds the sample project outside Development (default off in prod). |

- Generate a signing key: `openssl rand -base64 48`.
- Set a strong `POSTGRES_PASSWORD` (the committed dev value is for local only — **rotate it**).
- Never commit real secrets. `.env` is gitignored; use your platform's secret store in prod.
- Set `ASPNETCORE_ENVIRONMENT=Production` so Swagger is off and the guards are active.

## Database

- Migrations apply automatically on API startup (`DbInitializer.RunAsync`).
- In **Development** the seed creates a demo tenant + admin (`admin@bidbuilder.local` /
  `Admin@12345`) and the sample project.
- Outside Development **no default credential is seeded**: set `Seed__AdminPassword`
  (and optionally `Seed__AdminEmail`) to create the first admin, or create it out-of-band.
  The sample project is only seeded when `Seed__DemoData=true`.
- Back up the Postgres volume; the bid data is the product.

## Network / TLS

A ready-to-use production stack is provided in **`docker-compose.prod.yml`** + **`deploy/Caddyfile`**:

- **Caddy** terminates TLS (auto Let's Encrypt for `PUBLIC_DOMAIN`) and is the only
  service with host ports. It serves everything from one origin — `/api/*` and
  `/healthz` proxy to the API, everything else to the Next.js web app.
- **db / api / web publish no host ports** — Postgres and the API are never exposed
  to the internet directly, only reachable on the internal `bbnet` network.
- The API runs as `ASPNETCORE_ENVIRONMENT=Production` (guards active); `AllowedOrigins`
  is set to `PUBLIC_URL` (the single public origin — not `localhost`).

Run it: `docker compose -f docker-compose.prod.yml up -d --build`
(set `PUBLIC_DOMAIN`, `PUBLIC_URL`, `ACME_EMAIL`, `SEED_ADMIN_PASSWORD` — see `.env.example`).

## CI

- GitHub Actions (`.github/workflows/ci.yml`) builds + runs all tests against a
  Postgres service container and type-checks the frontend on every push/PR.
- Recommended: protect `main` to require the CI checks before merge.

## Smoke test after deploy

1. `GET /healthz` → 200.
2. `POST /api/auth/login` with the admin creds → token.
3. `GET /api/projects` with the token → the seeded project.
