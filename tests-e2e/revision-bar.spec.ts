import { test, expect } from "@playwright/test"
import { createTestUser, deleteTestUser, signIn, type TestUser } from "./_helpers/auth"

/**
 * 25.4 — Revision-bar consolidation smoke.
 *
 * The revision strip used to render 5–6 lifecycle buttons (New / Duplicate /
 * Copy to… / Save as template / From template / Delete) inline; the estimate
 * editor's meta header rendered 4 export buttons (Excel / CSV / PDF / Bid
 * Letter) inline. Both clusters now live behind a single labelled trigger:
 *   • Revision strip: "Actions ▾" dropdown
 *   • Editor header:  "Export ▾"  dropdown
 *
 * Asserts:
 *   1. Neither cluster's items are visible inline (no stray "New" /
 *      "Duplicate" / "Excel" / "CSV" buttons on the strip / header).
 *   2. The "Actions" trigger opens a menu containing the lifecycle items.
 *   3. The "Export" trigger opens a menu containing the export items.
 *   4. Escape and click-outside dismiss the open menu (basic a11y/keyboard).
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
const TENANT = "default"

// 29.A.1 — per-spec throwaway user, torn down after the test.
let user: TestUser | undefined
test.afterEach(async ({ request }) => { await deleteTestUser(request, user); user = undefined })

test("revision bar collapses lifecycle into Actions ▾ and exports into Export ▾", async ({ page, request }) => {
  // Sign in as a per-spec user — token injected, no login-UI round-trip.
  user = await createTestUser(request)
  await signIn(page, user)
  const token = user.token
  const projectsRes = await request.get(`${API_URL}/api/projects`, {
    headers: { Authorization: `Bearer ${token}`, "X-Tenant-Id": TENANT },
  })
  const projects = (await projectsRes.json()) as Array<{ id: number; code: string }>
  expect(projects.length, "at least one project").toBeGreaterThan(0)
  const projectId = projects[0].id

  await page.goto(`/projects/${projectId}`)
  await expect(page.locator("text=Revision").first()).toBeVisible({ timeout: 10_000 })

  // 1. No inline lifecycle buttons. (Plain buttons named "New" / "Duplicate" /
  //    "Excel" / "CSV" should NOT exist anywhere on the page now — they are
  //    only reachable via the dropdowns.)
  await expect(page.getByRole("button", { name: /^Duplicate$/ })).toHaveCount(0)
  await expect(page.getByRole("button", { name: /^Copy to…$/ })).toHaveCount(0)
  await expect(page.getByRole("button", { name: /^Excel$/ })).toHaveCount(0)
  await expect(page.getByRole("button", { name: /^CSV$/ })).toHaveCount(0)

  // 2. Actions ▾ opens a menu with the lifecycle items.
  const actionsTrigger = page.getByRole("button", { name: /Revision actions/i })
  await expect(actionsTrigger).toBeVisible()
  await actionsTrigger.click()
  const actionsMenu = page.getByRole("menu", { name: /Revision actions/i })
  await expect(actionsMenu).toBeVisible({ timeout: 2_000 })
  for (const label of ["New", "Duplicate", "Copy to…", "Save as template", "From template"]) {
    await expect(actionsMenu.getByRole("menuitem", { name: label })).toBeVisible()
  }
  // Escape dismisses.
  await page.keyboard.press("Escape")
  await expect(actionsMenu).toBeHidden({ timeout: 2_000 })

  // 3. Export ▾ opens a menu with the four export items.
  const exportTrigger = page.getByRole("button", { name: /Export this revision/i })
  await expect(exportTrigger).toBeVisible()
  await exportTrigger.click()
  const exportMenu = page.getByRole("menu", { name: /Export this revision/i })
  await expect(exportMenu).toBeVisible({ timeout: 2_000 })
  for (const label of ["Excel", "CSV", "PDF", "Bid Letter"]) {
    await expect(exportMenu.getByRole("menuitem", { name: label })).toBeVisible()
  }
  // 4. Click-outside dismisses.
  await page.locator("body").click({ position: { x: 5, y: 5 } })
  await expect(exportMenu).toBeHidden({ timeout: 2_000 })
})
