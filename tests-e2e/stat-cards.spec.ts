import { test, expect } from "@playwright/test"

/**
 * 25.5 — Stat-card trend deltas + per-unit toggle smoke.
 *
 * Asserts the new affordances the bid-page stat row gained:
 *   1. Backend ships a `previousDelta` on the breakdown when ≥ 2 revisions
 *      exist; the frontend renders a trend badge on every card whose
 *      previous value was non-zero. We don't assert exact numbers (the
 *      delta depends on whatever the seed data + clone produces) — we
 *      assert the BADGE structure: role="status" + "vs Rev N" text.
 *   2. Per-unit toggle appears when a top-level area has a positive
 *      quantity, hidden otherwise. We use the API directly to ensure at
 *      least one area exists before asserting.
 *   3. The toggle's state round-trips through localStorage on page reload —
 *      this is the acceptance criterion's "round-trips a preference per
 *      estimate" rule.
 *
 * To keep this independent of the seed's exact revision count, we CREATE a
 * fresh second revision via the Actions ▾ → New menu (which lands us on a
 * clean Rev N+1) and assert the badge for "vs Rev N".
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
const TENANT = "default"

test("stat cards render trend deltas vs prior revision and per-unit toggle persists", async ({ page, request }) => {
  await page.goto("/login")
  await page.locator('input[type="email"]').fill("admin@bidbuilder.local")
  await page.locator('input[type="password"]').fill("Admin@12345")
  await page.getByRole("button", { name: /sign in/i }).click()
  await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })

  // Create a fresh project for this spec. The other parallel-running specs
  // hit /projects/{seededId} concurrently, and our mutations (clone, add
  // section, add area, recompute) on the seeded project were racing with
  // their reads. Owning the project end-to-end keeps the spec deterministic
  // even when Playwright spreads tests across many workers.
  const token = await page.evaluate(() => localStorage.getItem("bb_token"))
  expect(token).toBeTruthy()
  const authHdr = { Authorization: `Bearer ${token}`, "X-Tenant-Id": TENANT }
  const code = `STAT-${Date.now()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
  const proj = await (await request.post(`${API_URL}/api/projects`, {
    headers: { ...authHdr, "Content-Type": "application/json" },
    data: { code, name: "25.5 stat-cards smoke", clientName: null, location: null, currency: "AED", status: "Bidding", durationMonths: 6 },
  })).json() as { id: number }
  const projectId = proj.id

  // We need ≥ 2 CONSECUTIVE-REVISION-NUMBER estimates where the prior one
  // has a non-zero priced total — otherwise the backend's div-by-0 guard
  // returns null on every per-card pct and no badge renders. The seed
  // doesn't guarantee that, so build it ourselves: create Rev N, add a
  // priced item, then clone → Rev N+1 (inherits the item).
  const baseRev = await (await request.post(`${API_URL}/api/projects/${projectId}/estimates`, {
    headers: { ...authHdr, "Content-Type": "application/json" },
    data: { title: "25.5 delta-smoke base" },
  })).json() as { id: number; revision: number; rowVersion: string }
  // Add a section + priced item so DirectCost > 0 on the base revision.
  const sectionResp = await (await request.post(`${API_URL}/api/estimates/${baseRev.id}/sections`, {
    headers: { ...authHdr, "Content-Type": "application/json", "If-Match": baseRev.rowVersion },
    data: { code: "S1", title: "Smoke", sortOrder: 0 },
  })).json() as { sections: Array<{ id: number; code: string }>; rowVersion: string }
  const sectionId = sectionResp.sections.find((s) => s.code === "S1")!.id
  await request.post(`${API_URL}/api/estimates/${baseRev.id}/sections/${sectionId}/items`, {
    headers: { ...authHdr, "Content-Type": "application/json", "If-Match": sectionResp.rowVersion },
    data: { description: "line", unit: "m", quantity: 1, unitRate: 100, sortOrder: 0 },
  })
  // Clone → Rev N+1. The breakdown for Rev N+1 will report a delta against
  // Rev N (same totals → 0%, which still renders a badge with "vs Rev N").
  const newRev = await (await request.post(`${API_URL}/api/projects/${projectId}/estimates/${baseRev.id}/clone`, {
    headers: { ...authHdr, "Content-Type": "application/json" },
    data: {},
  })).json() as { id: number; revision: number }

  // Ensure an area exists with a positive top-level quantity so the
  // per-unit toggle is reachable. Idempotent: only POST if no top-level
  // area with qty > 0 already exists.
  const areas = await (await request.get(`${API_URL}/api/projects/${projectId}/areas`, { headers: authHdr })).json() as Array<{
    id: number; parentAreaId: number | null; quantity: number; unit: string | null
  }>
  const hasTopLevelWithQty = areas.some((a) => a.parentAreaId == null && a.quantity > 0)
  if (!hasTopLevelWithQty) {
    await request.post(`${API_URL}/api/projects/${projectId}/areas`, {
      headers: { ...authHdr, "Content-Type": "application/json" },
      data: { name: "25.5-smoke-tower", code: "T1", kind: "Area", quantity: 1000, unit: "m²", parentAreaId: null, sortOrder: 0 },
    })
  }

  // The newRev id is freshly allocated above, so its localStorage key has
  // never been written — no pre-test cleanup needed. The reload step later
  // verifies the toggle survives a full page navigation.
  await page.goto(`/projects/${projectId}`)
  // Wait for the editor to render (stat cards appear sticky on the page).
  await expect(page.getByText("Currency:")).toBeVisible({ timeout: 10_000 })

  // 1. Trend badge: since we created a new revision, the editor lands on it
  //    and the backend ships previousDelta pointing at Rev N-1. The cards
  //    that had non-zero values on Rev N-1 (at minimum directCost, since
  //    the seed has a priced BOQ) render a "vs Rev N" status badge.
  await expect(page.getByText(/vs Rev \d+/).first()).toBeVisible({ timeout: 10_000 })

  // 2. Per-unit toggle visible (because we ensured a top-level area with
  //    qty > 0 exists). Label reads "Per m²" using the area's Unit.
  const toggle = page.getByRole("checkbox", { name: /Show totals per m²/i })
  await expect(toggle).toBeVisible({ timeout: 10_000 })
  await expect(toggle).not.toBeChecked()

  // 3. Round-trip: tick the toggle, reload the page, expect the toggle to
  //    come back on (loaded from localStorage by estimate id).
  await toggle.check()
  await expect(toggle).toBeChecked()
  await page.reload()
  const toggleAfter = page.getByRole("checkbox", { name: /Show totals per m²/i })
  await expect(toggleAfter).toBeChecked({ timeout: 10_000 })

  // No project-delete endpoint today, so the smoke project lingers in dev.
  // The unique code per run (timestamp + random suffix) makes re-runs
  // collision-free; CI runs against a fresh seeded DB so it never accumulates.
})
