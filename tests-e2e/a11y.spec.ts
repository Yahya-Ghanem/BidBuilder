import { test, expect } from "@playwright/test"
import AxeBuilder from "@axe-core/playwright"
import { createTestUser, deleteTestUser, signIn, type TestUser } from "./_helpers/auth"

/**
 * 24.4 — Axe-core accessibility gate.
 *
 * Scans WCAG 2.1 A + AA rules against:
 *   1. /login   — every visitor passes through here; smallest form-dense surface
 *                 to keep AA-clean while we iterate.
 *   2. /settings — densest admin form surface; catches label-for, fieldset,
 *                 button-name issues across half a dozen admin cards in one shot.
 *
 * Fails the build on any "critical" violation. "critical" means assistive tech
 * literally cannot operate the surface (unlabeled inputs/selects, buttons with
 * no accessible name) — those ARE shippable blockers.
 *
 * 26.2 — "serious" impact violations are now gated BY DEFAULT (the slate-400
 * → --muted sweep closed the deferred 24.4 debt). Set A11Y_INCLUDE_SERIOUS=0
 * locally to *opt out* if iterating on a separate cosmetic refactor and you
 * temporarily don't want the serious bar to block — the default is on.
 *
 * NOT yet gated (deliberate, tracked):
 *   • /projects (list) and /projects/[id] (detail). These surfaces have a long
 *     tail of icon-only buttons + selects from earlier increments that need
 *     a dedicated cleanup pass. Easier to land the gate + iterate forward than
 *     bundle a wide cosmetic sweep into the gate-enable PR.
 *
 * Test-only rule disables:
 *   • region — the /login layout intentionally omits a top-level <main>
 *     landmark (it's a centered card); re-enable when login moves into the
 *     authed shell.
 *
 * Prereqs (local): `docker compose up -d --build api web`.
 * CI: the existing `e2e` job runs `npx playwright test`, which picks this up
 * (testDir = tests-e2e, all *.spec.ts).
 */

// 26.2 — Default ON. Opt out with A11Y_INCLUDE_SERIOUS=0 if you specifically
// need the lower bar (e.g. wide cosmetic refactor in flight). Anything OTHER
// than the literal string "0" keeps the serious bar enabled.
const INCLUDE_SERIOUS = process.env.A11Y_INCLUDE_SERIOUS !== "0"

function axe(page: import("@playwright/test").Page) {
  return new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .disableRules(["region"])
}

function blocking(results: Awaited<ReturnType<AxeBuilder["analyze"]>>) {
  return results.violations.filter((v) =>
    v.impact === "critical" || (INCLUDE_SERIOUS && v.impact === "serious"),
  )
}

function format(results: Awaited<ReturnType<AxeBuilder["analyze"]>>) {
  return blocking(results)
    .map((v) => {
      const targets = v.nodes
        .slice(0, 3)
        .map((n) => `  ↳ ${n.target.join(" ")}`)
        .join("\n")
      return `[${v.impact}] ${v.id} — ${v.help}\n  ${v.helpUrl}\n${targets}`
    })
    .join("\n\n")
}

/**
 * 28.1 — Seed localStorage("bb.theme") on the origin BEFORE the first
 * navigation so the boot script in app/layout.tsx picks up the dark
 * preference and sets data-theme="dark" on first paint. Without this, the
 * page would load light (boot script reads empty storage → falls back to
 * prefers-color-scheme, which is "light" under default Playwright config),
 * defeating the point of running the gate in dark.
 *
 * Why addInitScript and not setStorage: Playwright's storageState is set
 * before any page load, but addInitScript runs in the page context BEFORE
 * any script tag — including our inline boot script — so the value is
 * already in place when boot reads it.
 */
async function seedTheme(page: import("@playwright/test").Page, theme: "light" | "dark") {
  await page.addInitScript((t: string) => {
    try { window.localStorage.setItem("bb.theme", t) } catch { /* private mode */ }
  }, theme)
}

// 28.1 — Run every a11y surface in BOTH themes. Dark surfaces use different
// tokens (slate-100 on slate-900, brightened brand teal, deep semantic soft
// backgrounds); each could regress contrast independently of the light path.
const THEMES = ["light", "dark"] as const

// 29.A.1 — the gated /settings scans sign in as a per-spec throwaway user via
// the dev-only test-support endpoint (token injected, no /api/auth/login call).
let user: TestUser | undefined
test.afterEach(async ({ request }) => { await deleteTestUser(request, user); user = undefined })

for (const theme of THEMES) {
  test.describe(`a11y [${theme}] — WCAG 2.1 A/AA, no critical violations`, () => {
    test(`login page (${theme})`, async ({ page }) => {
      await seedTheme(page, theme)
      await page.goto("/login")
      await page.locator('input[type="email"]').waitFor()
      // Sanity: the boot script applied the expected theme attribute. If this
      // fails the test is meaningless — we'd be silently re-running the light
      // path under a "dark" label.
      await expect(page.locator("html")).toHaveAttribute("data-theme", theme)
      const results = await axe(page).analyze()
      expect(blocking(results), format(results)).toEqual([])
    })

    test(`settings (admin form surface) (${theme})`, async ({ page, request }) => {
      await seedTheme(page, theme)
      // Sign in first — /settings is gated.
      user = await createTestUser(request)
      await signIn(page, user)

      await page.goto("/settings")
      await page.waitForLoadState("networkidle")
      await expect(page.locator("html")).toHaveAttribute("data-theme", theme)
      const results = await axe(page).analyze()
      expect(blocking(results), `settings [${theme}]:\n${format(results)}`).toEqual([])
    })
  })
}
