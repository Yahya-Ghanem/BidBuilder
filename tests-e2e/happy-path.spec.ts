import { test, expect } from "@playwright/test"

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
 *   2. After auth, /projects loads — the projects tree's portfolio group that
 *      contains PRJ-2026-001 expands to surface the project row. Proves API
 *      auth + tenant resolution + the projects query all wire end-to-end.
 *   3. Direct navigation to the project detail page renders the new
 *      _components-based layout (header card, TeamsPanel, AreasPanel,
 *      EstimatesSection). Proves the 19.6 split kept route composition intact.
 *
 * Why fetch the project id via the API rather than click "Full display" in the
 * tree? The production UX (click portfolio → click project → click revision →
 * double-click "Full display") is four fragile interactions for a smoke. The
 * direct goto proves the route composition just as well with one navigation.
 *
 * Why discover the portfolio name from the API? On the seed it's "Untyped",
 * but on long-lived dev DBs the sample project drifts as admin/settings runs
 * reclassify it. The spec used to hard-code "Untyped" and broke on live smoke
 * once that happened. Reading projectTypeName from /api/projects mirrors the
 * tree's own grouping (components/projects-tree.tsx:102) and stays correct
 * regardless of drift.
 *
 * Deliberately NOT covered here: BOQ edits, publish flow, exports. Those have
 * solid API-level coverage in api.Tests/ and benefit less from a brittle UI test.
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
const UNTYPED = "Untyped"

test.describe("Happy path", () => {
  test("login → projects → open project", async ({ page, request }) => {
    await page.goto("/login")

    await expect(page.getByRole("heading", { name: /sign in|bidbuilder/i }).or(page.locator("text=BidBuilder"))).toBeVisible()
    await page.locator('input[type="email"]').fill("admin@bidbuilder.local")
    await page.locator('input[type="password"]').fill("Admin@12345")
    await page.getByRole("button", { name: /sign in/i }).click()

    await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })

    // Read the authenticated session and discover the seeded project via the
    // API. Done before any tree interaction so the click target (portfolio
    // header) reflects the project's *current* projectTypeName rather than a
    // hard-coded "Untyped" that drifts on long-lived dev DBs.
    const token = await page.evaluate(() => localStorage.getItem("bb_token"))
    expect(token, "JWT should be in localStorage after login").toBeTruthy()
    const projectsRes = await request.get(`${API_URL}/api/projects`, {
      headers: { Authorization: `Bearer ${token}`, "X-Tenant-Id": "default" },
    })
    expect(projectsRes.ok(), `GET /api/projects → ${projectsRes.status()}`).toBeTruthy()
    const projects = (await projectsRes.json()) as Array<{ id: number; code: string; projectTypeName: string | null }>
    const seeded = projects.find((p) => p.code === "PRJ-2026-001")
    expect(seeded, "seeded project PRJ-2026-001 should exist").toBeTruthy()

    // Expand the portfolio group that owns PRJ-2026-001. The header label is
    // exactly projectTypeName (or "Untyped" when null) — matches the grouping
    // logic in components/projects-tree.tsx. .first() guards against the name
    // also appearing elsewhere in the tree (e.g. inside a project name row).
    const portfolioName = seeded!.projectTypeName ?? UNTYPED
    await page.getByText(portfolioName, { exact: true }).first().click()
    await expect(page.locator("text=PRJ-2026-001").first()).toBeVisible({ timeout: 10_000 })

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
