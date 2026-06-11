import { test, expect } from "@playwright/test"
import { createTestUser, deleteTestUser, authHeaders, signIn, API_URL, type TestUser } from "./_helpers/auth"

/**
 * 29.B.1 — Kill-switch acceptance: with the "export-preview" flag forced OFF
 * for the current tenant, the Export ▾ dropdown must not show xlsx / csv / pdf
 * entries (those are the surfaces the flag gates in estimate-editor.tsx).
 * Forcing the flag back ON brings them back without a redeploy.
 *
 * Implementation choice — flips through the dev-only test endpoint
 * (PUT /api/test/feature-flags/{key}/override), which sets a per-tenant
 * override in the catalogue and invalidates the cache. The SuperAdmin admin
 * endpoint is the production path; the dev test endpoint exists only so
 * Playwright doesn't need to mint a SuperAdmin (the per-spec users are
 * TenantAdmins).
 */

const TENANT = "default"

async function forceFlag(request: import("@playwright/test").APIRequestContext, user: TestUser, key: string, enabled: boolean | null) {
  // POST/PUT on the dev test endpoint; we don't authenticate (it's anonymous in
  // Development, and the override applies to the X-Tenant-Id slug).
  const res = await request.put(`${API_URL}/api/test/feature-flags/${key}/override`, {
    headers: { ...authHeaders(user), "Content-Type": "application/json" },
    data: { enabled },
  })
  expect(res.ok(), `force-flag ${key}=${enabled} → ${res.status()}`).toBeTruthy()
}

let user: TestUser | undefined
test.afterEach(async ({ request }) => {
  // Restore the catalogue to its default before letting the next spec run.
  if (user) await forceFlag(request, user, "export-preview", null).catch(() => {})
  await deleteTestUser(request, user)
  user = undefined
})

test.describe("Feature flag kill switch (29.B.1)", () => {
  test("flipping export-preview off hides the xlsx/pdf/csv entries", async ({ page, request }) => {
    user = await createTestUser(request)
    await signIn(page, user)

    // Find a project to navigate into — the seeded sample is fine.
    const projects = await request.get(`${API_URL}/api/projects`, { headers: authHeaders(user) })
    expect(projects.ok()).toBeTruthy()
    const list = (await projects.json()) as Array<{ id: number; code: string }>
    const seeded = list.find((p) => p.code === "PRJ-2026-001")
    expect(seeded, "seeded project PRJ-2026-001 should exist").toBeTruthy()

    await page.goto(`/projects/${seeded!.id}`)
    await page.getByRole("tablist", { name: /project sections/i }).getByRole("tab", { name: "Estimate" }).click()

    // ── Flag ON (default) — the three lanes are present ────────────────────
    const exportBtn = page.getByRole("button", { name: /export this revision/i })
    await expect(exportBtn).toBeVisible({ timeout: 15_000 })
    await exportBtn.click()
    await expect(page.getByRole("menuitem", { name: "Excel" })).toBeVisible()
    await expect(page.getByRole("menuitem", { name: "PDF" })).toBeVisible()
    // Close the dropdown by pressing Escape.
    await page.keyboard.press("Escape")

    // ── Flip the flag OFF for this tenant ──────────────────────────────────
    await forceFlag(request, user, "export-preview", false)

    // Invalidate the SPA's /api/me/features cache the way a real operator
    // workflow would — switch tabs (refetch on focus) or reload. Reloading
    // is faster and deterministic.
    await page.reload()
    await page.getByRole("tablist", { name: /project sections/i }).getByRole("tab", { name: "Estimate" }).click()
    await expect(exportBtn).toBeVisible({ timeout: 15_000 })
    await exportBtn.click()
    // The three modal-driven lanes are gone; only Bid Letter (gated by a
    // different flag, default on) remains.
    await expect(page.getByRole("menuitem", { name: "Excel" })).toHaveCount(0)
    await expect(page.getByRole("menuitem", { name: "PDF" })).toHaveCount(0)
    await expect(page.getByRole("menuitem", { name: /bid letter/i })).toBeVisible()
  })
})
