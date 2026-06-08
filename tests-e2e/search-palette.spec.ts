import { test, expect, type APIRequestContext } from "@playwright/test"

/**
 * 28.5 — Search palette upgrades. Acceptance bar from the roadmap:
 *
 *   • Recent shows after Ctrl+K with no query
 *   • `@p Sun` returns only the matching project (e.g. Sunrise)
 *   • `> sign` shows the sign-out action
 *   • All keyboard-navigable
 *
 * This spec drives the live stack (web :3100 + api :8081). It seeds a project
 * with a known prefix so the @p filter assertion is deterministic across runs.
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
const TENANT = "default"

async function seedProject(request: APIRequestContext, token: string, name: string) {
  const code = `PAL-${Date.now()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
  const r = await request.post(`${API_URL}/api/projects`, {
    headers: { Authorization: `Bearer ${token}`, "X-Tenant-Id": TENANT, "Content-Type": "application/json" },
    data: { code, name, clientName: null, location: null, currency: "AED", status: "Bidding", durationMonths: 6 },
  })
  expect(r.ok(), `seed project → ${r.status()}`).toBeTruthy()
  return (await r.json()) as { id: number; code: string; name: string }
}

test.describe("Search palette upgrades (28.5)", () => {
  test("recent → @-filter → > action lanes all work and are keyboard-navigable", async ({ page, request }) => {
    // Sign in.
    await page.goto("/login")
    await page.locator('input[type="email"]').fill("admin@bidbuilder.local")
    await page.locator('input[type="password"]').fill("Admin@12345")
    await page.getByRole("button", { name: /sign in/i }).click()
    await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })

    const token = await page.evaluate(() => localStorage.getItem("bb_token"))
    expect(token).toBeTruthy()

    // Seed a project so the @p Sunrise filter assertion has something to find.
    // The prefix is random so parallel test runs don't collide on the same name.
    const sunrise = await seedProject(request, token!, `Sunrise Tower ${Date.now()}`)

    // Clear any recents accumulated by another spec in the same browser tab.
    await page.evaluate(() => localStorage.removeItem("bb_recent_entities"))

    // ── Action lane: `> sign` shows the sign-out action ─────────────────────
    await page.locator('[data-testid="search-palette-trigger"]').click()
    const palette = page.locator('[data-testid="search-palette"]')
    await expect(palette).toBeVisible()
    await page.locator('[data-testid="search-palette-input"]').fill("> sign")
    await expect(page.locator('[data-testid="palette-action-sign-out"]')).toBeVisible()
    // Close the palette without firing the action (we don't want this spec to
    // sign the test user out mid-run).
    await page.keyboard.press("Escape")
    await expect(palette).toBeHidden()

    // ── @-filter lane: `@p Sun…` returns only project hits ─────────────────
    await page.locator('[data-testid="search-palette-trigger"]').click()
    await expect(palette).toBeVisible()
    await page.locator('[data-testid="search-palette-input"]').fill(`@p ${sunrise.name}`)
    // Scope every assertion to the palette's results panel — the sidebar has
    // its own "Assemblies" / "Resources" nav links that would otherwise match.
    const results = page.locator('[data-testid="search-palette-results"]')
    await expect(results).toBeVisible()
    // The seeded project's row must be visible inside the results panel.
    const projectRow = results.getByRole("button").filter({ hasText: sunrise.name }).first()
    await expect(projectRow).toBeVisible({ timeout: 10_000 })
    // No estimate/resource/assembly group header should be rendered — the
    // filter is `project` only.
    await expect(results.getByText("Estimates", { exact: true })).toHaveCount(0)
    await expect(results.getByText("Resources", { exact: true })).toHaveCount(0)
    await expect(results.getByText("Assemblies", { exact: true })).toHaveCount(0)

    // Keyboard-navigate to the project row and Enter to open it.
    await page.locator('[data-testid="search-palette-input"]').focus()
    await page.keyboard.press("ArrowDown") // wrap to first if not already
    await page.keyboard.press("Enter")
    await expect(page).toHaveURL(new RegExp(`/projects/${sunrise.id}\\b`), { timeout: 15_000 })

    // ── Recent lane: re-open palette, empty query → recents include the project ─
    // Open the palette via the keyboard shortcut to prove ⌘K still works.
    await page.keyboard.press(process.platform === "darwin" ? "Meta+k" : "Control+k")
    await expect(palette).toBeVisible()
    // Input must be empty — palette resets the query on open.
    await expect(page.locator('[data-testid="search-palette-input"]')).toHaveValue("")
    // The recent row at index 0 is the most-recently-visited entity = sunrise.
    const recentFirst = page.locator('[data-testid="palette-recent-0"]')
    await expect(recentFirst).toBeVisible()
    await expect(recentFirst).toContainText(sunrise.name)
  })
})
