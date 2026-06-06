import { test, expect } from "@playwright/test"

/**
 * 19.6 — End-to-end happy path against the live dev stack.
 *
 * Prereqs:
 *   docker compose up -d --build api web
 *   (web on :3100, api on :8081, db on :5433 — see docker-compose.yml)
 *
 * Coverage:
 *   1. The login page renders and accepts the seeded admin credentials.
 *   2. After auth, /projects shows the seeded project (PRJ-2026-001) — proving
 *      the API auth + tenant resolution + projects query all wire end-to-end.
 *   3. Opening the project page loads the new _components-based detail view
 *      (header card, TeamsPanel, AreasPanel, EstimatesSection) — proving the
 *      19.6 split didn't break route composition.
 *
 * Deliberately NOT covered here: BOQ edits, publish flow, exports. Those have
 * solid API-level coverage in api.Tests/ and benefit less from a brittle UI test.
 * Add Playwright cases for them if a UI bug ever ships that the API suite missed.
 */
test.describe("Happy path", () => {
  test("login → projects → open project", async ({ page }) => {
    await page.goto("/login")

    await expect(page.getByRole("heading", { name: /sign in|bidbuilder/i }).or(page.locator("text=BidBuilder"))).toBeVisible()
    await page.locator('input[type="email"]').fill("admin@bidbuilder.local")
    await page.locator('input[type="password"]').fill("Admin@12345")
    await page.getByRole("button", { name: /sign in/i }).click()

    await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })
    // The seeded project's code is stable across runs.
    const projectLink = page.locator("text=PRJ-2026-001").first()
    await expect(projectLink).toBeVisible({ timeout: 10_000 })

    await projectLink.click()
    await expect(page).toHaveURL(/\/projects\/\d+/, { timeout: 10_000 })
    // Each landmark proves a different extracted module renders:
    //   • Currency: ... in the header card  → page.tsx Detail()
    //   • Teams                              → _components/teams-panel
    //   • Areas                              → _components/areas-panel
    //   • Revision                           → _components/estimates-section
    await expect(page.getByText("Currency:")).toBeVisible()
    await expect(page.getByRole("heading", { level: 3, name: /Teams/ }).or(page.locator("text=Teams").first())).toBeVisible()
    await expect(page.locator("text=Areas").first()).toBeVisible()
    await expect(page.locator("text=Revision").first()).toBeVisible()
  })
})
