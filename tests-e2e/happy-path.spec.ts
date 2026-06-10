import { test, expect } from "@playwright/test"
import { createTestUser, deleteTestUser, type TestUser } from "./_helpers/auth"

/**
 * 19.6 + 20.1 — End-to-end happy path against the live stack.
 *
 * Prereqs (local):
 *   docker compose up -d --build api web
 *   (web on :3100, api on :8081, db on :5433 — see docker-compose.yml)
 *
 * In CI the `e2e` job in .github/workflows/ci.yml boots the same stack
 * directly on the GH runner and runs this spec.
 *
 * Coverage:
 *   1. The login page renders and accepts the seeded admin credentials.
 *   2. After auth, /projects loads — the projects tree's "Untyped" group
 *      (the seeded sample has no project type) expands to surface the
 *      PRJ-2026-001 row. Proves API auth + tenant resolution + the projects
 *      query all wire end-to-end.
 *   3. Direct navigation to the project detail page renders the new
 *      _components-based layout (header card, TeamsPanel, AreasPanel,
 *      EstimatesSection). Proves the 19.6 split kept route composition intact.
 *
 * Why fetch the project id via the API rather than click "Full display" in the
 * tree? The production UX (click Untyped → click project → click revision →
 * double-click "Full display") is four fragile interactions for a smoke. The
 * direct goto proves the route composition just as well with one navigation.
 *
 * Deliberately NOT covered here: BOQ edits, publish flow, exports. Those have
 * solid API-level coverage in api.Tests/ and benefit less from a brittle UI test.
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"

// 29.A.1 — this is the ONE spec that drives the real login UI (that's part of
// its purpose), but it signs in as its own throwaway user, not the shared
// admin. A single real login per run sits comfortably inside the default
// 10/60s rate limit.
let user: TestUser | undefined
test.afterEach(async ({ request }) => { await deleteTestUser(request, user); user = undefined })

test.describe("Happy path", () => {
  test("login → projects → open project", async ({ page, request }) => {
    user = await createTestUser(request)
    await page.goto("/login")

    await expect(page.getByRole("heading", { name: /sign in|bidbuilder/i }).or(page.locator("text=BidBuilder"))).toBeVisible()
    await page.locator('input[type="email"]').fill(user.email)
    await page.locator('input[type="password"]').fill(user.password)
    await page.getByRole("button", { name: /sign in/i }).click()

    await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })

    // The projects tree groups by project type; the seeded sample has none, so
    // it's under "Untyped". Expanding the group exposes the project row whose
    // hint is the code "PRJ-2026-001" — proves the API auth + tenant + projects
    // query are all wired correctly.
    await page.getByText(/^Untyped/).click()
    await expect(page.locator("text=PRJ-2026-001").first()).toBeVisible({ timeout: 10_000 })

    // Read the authenticated session and discover the project id via the API,
    // then goto the detail page directly — proves the extracted _components/
    // still compose into the same route.
    const token = await page.evaluate(() => localStorage.getItem("bb_token"))
    expect(token, "JWT should be in localStorage after login").toBeTruthy()
    const projectsRes = await request.get(`${API_URL}/api/projects`, {
      headers: { Authorization: `Bearer ${token}`, "X-Tenant-Id": "default" },
    })
    expect(projectsRes.ok(), `GET /api/projects → ${projectsRes.status()}`).toBeTruthy()
    const projects = (await projectsRes.json()) as Array<{ id: number; code: string }>
    const seeded = projects.find((p) => p.code === "PRJ-2026-001")
    expect(seeded, "seeded project PRJ-2026-001 should exist").toBeTruthy()

    await page.goto(`/projects/${seeded!.id}`)
    // Each landmark proves a different extracted module renders:
    //   • "Currency:" in the sticky project header card  → page.tsx Detail()
    //   • "Revision" in the always-visible revision strip → _components/estimates-section
    //   • Tab strip (25.3): the five-tab restructure landed Phase 25.3 — assert
    //     each tab button is present, then drive the Overview tab to prove the
    //     Teams + Areas panels still mount via their _components modules.
    await expect(page.getByText("Currency:")).toBeVisible({ timeout: 10_000 })
    await expect(page.locator("text=Revision").first()).toBeVisible()

    // Tabs render with the 5 labels. tablist scoping avoids accidental matches
    // against other text on the page (eg. "Risk" appearing in a panel below).
    const tablist = page.getByRole("tablist", { name: /project sections/i })
    await expect(tablist).toBeVisible()
    for (const name of ["Overview", "Estimate", "Insights", "Risk", "Activity"]) {
      await expect(tablist.getByRole("tab", { name })).toBeVisible()
    }

    // Drive the Overview tab → Teams + Areas panels appear (they used to render
    // at the page level pre-25.3 and now live in this tab).
    await tablist.getByRole("tab", { name: "Overview" }).click()
    await expect(page.locator("text=Teams").first()).toBeVisible({ timeout: 5_000 })
    await expect(page.locator("text=Areas").first()).toBeVisible()
  })
})
