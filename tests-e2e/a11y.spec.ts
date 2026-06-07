import { test, expect } from "@playwright/test"
import AxeBuilder from "@axe-core/playwright"

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
 * NOT yet gated (deliberate, tracked):
 *   • /projects (list) and /projects/[id] (detail). These surfaces have a long
 *     tail of icon-only buttons + selects from earlier increments that need
 *     a dedicated cleanup pass. Easier to land the gate + iterate forward than
 *     bundle a wide cosmetic sweep into the gate-enable PR.
 *   • "serious" impact violations. Mostly color-contrast on the slate-400 muted
 *     palette — a real signal but a whole-app cosmetic refactor that earns its
 *     own PR (a new --muted CSS var bumped to AA contrast). Set
 *     A11Y_INCLUDE_SERIOUS=1 locally to see those today.
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

const INCLUDE_SERIOUS = process.env.A11Y_INCLUDE_SERIOUS === "1"

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

test.describe("a11y — WCAG 2.1 A/AA, no critical violations", () => {
  test("login page", async ({ page }) => {
    await page.goto("/login")
    await page.locator('input[type="email"]').waitFor()
    const results = await axe(page).analyze()
    expect(blocking(results), format(results)).toEqual([])
  })

  test("settings (admin form surface)", async ({ page }) => {
    // Sign in first — /settings is gated.
    await page.goto("/login")
    await page.locator('input[type="email"]').fill("admin@bidbuilder.local")
    await page.locator('input[type="password"]').fill("Admin@12345")
    await page.getByRole("button", { name: /sign in/i }).click()
    await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })

    await page.goto("/settings")
    await page.waitForLoadState("networkidle")
    const results = await axe(page).analyze()
    expect(blocking(results), `settings:\n${format(results)}`).toEqual([])
  })
})
