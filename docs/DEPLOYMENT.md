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
- **Backups**: the production stack runs a `db-backup` sidecar that takes a daily
  gzipped `pg_dump` (configurable interval + retention) into the `dbbackups` volume.
  See **[docs/BACKUP.md](BACKUP.md)** for tuning, off-host durability, and the
  restore runbook. The bid data is the product — get the dumps off-host.

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

## Email / SMTP (21.1)

Outbound transactional email is **off by default**; the app never sends mail unless
the platform SMTP transport is both enabled and configured. When it is, BidBuilder emails:

- a copy of in-app notifications (estimate publish / approval-needed / new approval /
  subcontractor-quote received) to the same audience the bell notifies, and
- the portal link of a subcontractor RFQ to the contractor's address on creation.

Configure it via environment variables (see `.env.example`):

| Env var | Meaning |
|---|---|
| `Email__Enabled` | Master switch — must be `true` to send anything. |
| `Email__Host` / `Email__Port` | SMTP server (port defaults to 587). |
| `Email__Username` / `Email__Password` | SMTP credentials. **Secret** — set from the environment / a secret store, never in `appsettings`. |
| `Email__UseSsl` | STARTTLS / SSL (default `true`). |
| `Email__FromAddress` / `Email__FromName` | Envelope From. |
| `Email__AppBaseUrl` | Base URL for absolute links in emails (defaults to the first `AllowedOrigins` / `PUBLIC_URL`). |

Each tenant can opt its workspace out under **Settings → Email notifications**
(`NotificationEmailsEnabled`, default on). A tenant admin can verify the transport with
**Send test email** (`POST /api/settings/email/test`), which mails the calling admin and
reports whether the transport is configured and the send succeeded. Sending is best-effort:
a mail failure is logged and swallowed — it never breaks the action that triggered it.

## Programmatic API keys (21.2)

Headless callers (CI jobs, integrations, scripts) authenticate with a tenant-scoped
**API key** instead of a JWT. A tenant admin mints one under **Settings → API keys**
(`POST /api/admin/api-keys`); the secret (`bbk_…`) is shown **once** and only its
SHA-256 hash is stored. Present it in an `X-Api-Key` header — no `Authorization`
bearer and no `X-Tenant-Id` are needed (the tenant resolves from the key):

```
curl https://bid.example.com/api/projects -H "X-Api-Key: bbk_…"
```

A key acts **as the admin who created it** (inherits that user's role + team
permissions) and is disabled automatically if the key is revoked, its optional expiry
passes, or the owning user is deactivated. Revoke a key any time from the same screen.

## Per-tenant custom domains (20.11)

A workspace can be reached at its own host (e.g. `bids.acme.com`). A tenant admin
registers it under **Settings → Custom domain** (`PUT /api/settings/custom-domain`);
the host is stored lowercase and must be globally unique. When a request arrives with
no auth token and no `X-Tenant-Id` header, the tenant-resolution middleware matches the
request `Host` against the registered domain — so the vanity host resolves the tenant
with no header gymnastics.

Two ops steps are required for a registered domain to actually serve:

1. **DNS** — the customer points a `CNAME` (or `A`/`AAAA`) at the BidBuilder ingress.
2. **TLS** — the reverse proxy must obtain a certificate for the host. With Caddy, enable
   [on-demand TLS](https://caddyserver.com/docs/automatic-https#on-demand-tls) and gate it
   with an `ask` endpoint that confirms the host is a known custom domain, so certificates
   are only issued for hosts that belong to a tenant. Example:

   ```
   {
     on_demand_tls {
       ask http://api:8080/api/public/domain-allowed
     }
   }

   https:// {
     tls { on_demand }
     # ... existing reverse_proxy rules ...
   }
   ```

   (The `ask` endpoint is left as a deployment-time addition; the platform domain in
   `PUBLIC_DOMAIN` keeps its statically-issued certificate.)
