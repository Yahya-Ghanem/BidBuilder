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

- Generate a signing key: `openssl rand -base64 48`.
- Set a strong `POSTGRES_PASSWORD` (the committed dev value is for local only — **rotate it**).
- Never commit real secrets. `.env` is gitignored; use your platform's secret store in prod.
- Set `ASPNETCORE_ENVIRONMENT=Production` so Swagger is off and the guards are active.

## Database

- Migrations apply automatically on API startup (`DbInitializer.RunAsync`).
- The startup seed creates a demo tenant + admin (`admin@bidbuilder.local` / `Admin@12345`)
  and the sample project. **Change/disable the demo admin** before going live.
- Back up the Postgres volume; the bid data is the product.

## Network / TLS

- Put the API and web behind a reverse proxy with TLS (e.g. Caddy/Nginx).
- Restrict `AllowedOrigins` (CORS) to the real web origin — not `localhost`.
- Don't expose Postgres publicly; keep it on the internal network.

## CI

- GitHub Actions (`.github/workflows/ci.yml`) builds + runs all tests against a
  Postgres service container and type-checks the frontend on every push/PR.
- Recommended: protect `main` to require the CI checks before merge.

## Smoke test after deploy

1. `GET /healthz` → 200.
2. `POST /api/auth/login` with the admin creds → token.
3. `GET /api/projects` with the token → the seeded project.
