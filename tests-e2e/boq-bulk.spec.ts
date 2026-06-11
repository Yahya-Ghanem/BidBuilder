import { test, expect, type APIRequestContext } from "@playwright/test"
import { createTestUser, deleteTestUser, signIn, type TestUser } from "./_helpers/auth"

/**
 * 28.2 — Acceptance bar: "Selecting 10 lines and deleting them is one click
 * instead of 10." This spec proves it end-to-end against the live stack:
 *
 *   1. Sign in (seeded admin) and prepare an isolated estimate with 10 BOQ lines
 *      under one section, all via the API (the goal is to test the SELECT-AND-
 *      DELETE interaction in the UI, not to grind through the AddItem form ten
 *      times — which would itself violate the "one click" point).
 *   2. Navigate to the estimate, switch to the Estimate tab, and verify all 10
 *      lines render.
 *   3. Click the table's header "Select all" checkbox → 10 rows are selected and
 *      the sticky bulk bar reports 10.
 *   4. Click Delete in the bulk bar and accept the confirm prompt — ONE click.
 *   5. The breakdown re-renders with zero lines remaining (the recompute returns
 *      a fresh tree); the bulk bar disappears.
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"

async function seedEstimateWithTenLines(request: APIRequestContext, token: string, projectId: number, title: string) {
  const headers = { Authorization: `Bearer ${token}`, "X-Tenant-Id": "default" }
  // Create a fresh estimate so this run doesn't depend on / corrupt any other
  // revision's BOQ.
  const est = await request.post(`${API_URL}/api/projects/${projectId}/estimates`, { headers, data: { title } })
  expect(est.ok(), `create estimate → ${est.status()}`).toBeTruthy()
  const eid = (await est.json() as { id: number }).id

  // One section, ten items. Each POST returns the recomputed breakdown — we only
  // need the section id from the FIRST call, then chain items.
  const sec = await request.post(`${API_URL}/api/estimates/${eid}/sections`, {
    headers, data: { code: "BULK", title: "Bulk test", sortOrder: 0 },
  })
  expect(sec.ok()).toBeTruthy()
  const sid = (await sec.json() as { sections: Array<{ id: number; code: string }> })
    .sections.find((s) => s.code === "BULK")!.id

  for (let n = 1; n <= 10; n++) {
    const r = await request.post(`${API_URL}/api/estimates/${eid}/sections/${sid}/items`, {
      headers,
      data: { itemCode: `B${n}`, description: `bulk line ${n}`, unit: "m", quantity: 1, unitRate: 100, sortOrder: n },
    })
    expect(r.ok(), `seed item ${n} → ${r.status()}`).toBeTruthy()
  }
  return { eid, projectId }
}

// 29.A.1 — per-spec throwaway user, torn down after the test.
let user: TestUser | undefined
test.afterEach(async ({ request }) => { await deleteTestUser(request, user); user = undefined })

test.describe("BOQ bulk actions (28.2)", () => {
  test("select 10 lines and delete is one click", async ({ page, request }) => {
    // Confirms are blocking — auto-accept so the test runs unattended. The
    // confirm gates Delete in the bulk bar (one extra modal — the "click" the
    // acceptance counts is the Delete button itself; the confirm is the safety
    // gate that any destructive bulk action carries).
    page.on("dialog", (d) => d.accept())

    // Sign in as a per-spec user — token injected, no login-UI round-trip.
    user = await createTestUser(request)
    await signIn(page, user)
    const token = user.token

    // Find a project to attach the estimate to. The seeded sample is fine.
    const projectsRes = await request.get(`${API_URL}/api/projects`, {
      headers: { Authorization: `Bearer ${token}`, "X-Tenant-Id": "default" },
    })
    expect(projectsRes.ok()).toBeTruthy()
    const projects = (await projectsRes.json()) as Array<{ id: number; code: string }>
    const seeded = projects.find((p) => p.code === "PRJ-2026-001")
    expect(seeded, "seeded project PRJ-2026-001 should exist").toBeTruthy()

    // Title gets the run-id so this revision is unique even if the suite re-runs.
    const title = `28.2-bulk-${Date.now()}`
    const { eid, projectId } = await seedEstimateWithTenLines(request, token!, seeded!.id, title)

    // Open the project, switch to Estimate tab, pick the just-seeded revision.
    await page.goto(`/projects/${projectId}`)
    const tablist = page.getByRole("tablist", { name: /project sections/i })
    await tablist.getByRole("tab", { name: "Estimate" }).click()

    // The estimates-section auto-selects the LATEST revision; our seeded revision
    // is the newest under this project so the editor loads it without any click.

    // The BOQ table renders ten rows. Wait for the section header, then expand
    // it — useCollapse starts every section collapsed on initial load (light
    // first paint for large trees), so the checkbox column isn't in the DOM yet.
    await expect(page.getByText(/BULK Bulk test/)).toBeVisible({ timeout: 10_000 })
    await page.getByRole("button", { name: "Expand all" }).first().click()
    await expect(page.locator('input[type="checkbox"][aria-label^="Select row "]')).toHaveCount(10, { timeout: 10_000 })

    // Click "Select all rows" — the leading checkbox column in the DataTable.
    // The aria-label is set in components/data-table.tsx.
    await page.getByLabel("Select all rows").first().click()

    // The sticky bulk bar reports 10 selected.
    const bar = page.locator('[data-testid="boq-bulk-bar"]')
    await expect(bar).toBeVisible()
    await expect(page.locator('[data-testid="boq-bulk-selected"]')).toContainText("10")

    // ONE click — Delete.
    await page.locator('[data-testid="boq-bulk-delete"]').click()

    // After the recompute the bulk bar disappears and no rows are left.
    await expect(bar).toBeHidden({ timeout: 10_000 })
    await expect(page.locator('input[type="checkbox"][aria-label^="Select row "]')).toHaveCount(0)

    // Verify against the API too — the bulk endpoint reported the truth.
    const bd = await request.get(`${API_URL}/api/estimates/${eid}`, {
      headers: { Authorization: `Bearer ${token}`, "X-Tenant-Id": "default" },
    })
    expect(bd.ok()).toBeTruthy()
    const body = await bd.json() as { sections: Array<{ items: unknown[] }> }
    const total = body.sections.reduce((acc, s) => acc + s.items.length, 0)
    expect(total).toBe(0)
  })
})
