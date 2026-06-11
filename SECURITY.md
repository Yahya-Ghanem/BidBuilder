# Security Policy

BidBuilder is a multi-tenant SaaS handling commercially sensitive bid data.
This document covers how to report a vulnerability and how the project triages
the ones its own scanning finds.

## Reporting a vulnerability

**Do not open a public issue for a security problem.** Use GitHub's private
vulnerability reporting instead:

> https://github.com/Yahya-Ghanem/BidBuilder/security/advisories/new

Include reproduction steps, the affected endpoint/component, and the impact you
believe it has (cross-tenant read? privilege escalation? data loss?). You will
get an acknowledgement within **3 business days** and a triage verdict within
**7 days**.

## Supported versions

The `main` branch is the only supported line. Fixes ship forward; there are no
security backports to older tags.

## Automated scanning (what runs, when)

| Layer | Tool | Trigger |
| --- | --- | --- |
| npm / NuGet / Docker base images / GitHub Actions | Dependabot (`.github/dependabot.yml`) | Weekly (Mon), grouped minor+patch |
| Static analysis — TypeScript + C# | CodeQL (`.github/workflows/codeql.yml`) | Every PR + push to main + weekly cron |
| Container images (api + web) + SBOM | Trivy (`.github/workflows/trivy.yml`) | Every PR + push to main |

Trivy **fails the build** on any HIGH/CRITICAL finding that has a released fix,
unless the CVE has an explicit allow-list entry in `.trivyignore`. Every build
attaches a CycloneDX SBOM artifact per image (90-day retention) — the auditable
record of exactly what shipped.

## CVE-triage SLA

The clock starts when the finding first appears (Dependabot PR opened, CodeQL
alert raised, Trivy gate tripped), not when somebody reads it.

| Severity | Action due | What "done" means |
| --- | --- | --- |
| Critical | **< 7 days** | Fix merged, or workload-specific non-exploitability documented in the alert/PR |
| High | **< 30 days** | Same bar |
| Medium | < 90 days | Fix merged or accepted with a logged reason |
| Low | Best effort | Reviewed at the next dependency sweep |

A Dependabot PR is never closed silently: it is either **merged** or **closed
with a comment** stating the reason (false positive, not exploitable in this
deployment, superseded by a grouped update). "Stale and ignored" is an audit
finding, not a state.

## Allow-list policy (`.trivyignore`)

Suppressing a gate finding requires all three, in the file itself:

1. **Reason** — why the CVE is not exploitable here, or what blocks the fix.
2. **Owner** — a GitHub handle accountable for the entry.
3. **Expiry** — a date at most 90 days out. Expired entries are treated as new
   findings; the gate trips again.

## Secrets

- Real secrets live only in `.env` (gitignored) or deployment-time environment
  variables — never in tracked files. The repo is public; treat every tracked
  byte as published.
- `appsettings*.json` carry placeholders only (e.g. `Email__Password` is
  injected via environment).
- A leaked credential is a Critical finding: rotate first, then investigate.
