import { test, expect } from "@playwright/test"

/**
 * 25.3 — Project detail page tab restructure smoke.
 *
 * Goes straight to /projects/{id} (skipping the sidebar tree which has
 * accumulated test fixtures over time) and asserts the new layout:
 *   • Sticky project header card with "Currency:" line
 *   • Always-visible revision strip
 *   • Tab strip with five tabs (Overview / Estimate / Insights / Risk /
 *     Activity), default = Estimate
 *   • Overview tab surfaces the Teams + Areas panels (which moved out of
 *     the page level into this tab in 25.3)
 *   • Insights tab surfaces "Cost by area" (the AreaRollupPanel)
 *   • URL deep-link via ?tab= works on both fresh load and tab click
 *
 * Complements the happy-path spec (which exercises the projects tree).
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
const TENANT = "default"

test("project detail page renders five tabs and deep-links via ?tab=", async ({ page, request }) => {
  // Auth via the UI so the JWT lands in localStorage where the SPA expects it.
  await page.goto("/login")
  await page.locator('input[type="email"]').fill("admin@bidbuilder.local")
  await page.locator('input[type="password"]').fill("Admin@12345")
  await page.getByRole("button", { name: /sign in/i }).click()
  await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })

  // Pick any accessible project — the tab restructure is orthogonal to the
  // project's content, so the first one in the list will do.
  const token = await page.evaluate(() => localStorage.getItem("bb_token"))
  expect(token).toBeTruthy()
  const projectsRes = await request.get(`${API_URL}/api/projects`, {
    headers: { Authorization: `Bearer ${token}`, "X-Tenant-Id": TENANT },
  })
  expect(projectsRes.ok(), `GET /api/projects → ${projectsRes.status()}`).toBeTruthy()
  const projects = (await projectsRes.json()) as Array<{ id: number; code: string }>
  expect(projects.length, "at least one accessible project").toBeGreaterThan(0)
  const projectId = projects[0].id

  // Land on the detail page with no ?tab= → Estimate is the default tab.
  await page.goto(`/projects/${projectId}`)

  // 1. Sticky project header card.
  await expect(page.getByText("Currency:")).toBeVisible({ timeout: 10_000 })

  // 2. Always-visible revision strip.
  await expect(page.locator("text=Revision").first()).toBeVisible()

  // 3. Five tabs in the strip.
  const tablist = page.getByRole("tablist", { name: /project sections/i })
  await expect(tablist).toBeVisible()
  const labels = ["Overview", "Estimate", "Insights", "Risk", "Activity"] as const
  for (const name of labels) {
    await expect(tablist.getByRole("tab", { name })).toBeVisible()
  }

  // 4. Default = Estimate (aria-selected=true on it, false on the others).
  await expect(tablist.getByRole("tab", { name: "Estimate" })).toHaveAttribute("aria-selected", "true")
  await expect(tablist.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "false")

  // 5. Click Overview → Teams + Areas panels appear (they used to live at
  //    page level pre-25.3 and now mount inside this tab).
  await tablist.getByRole("tab", { name: "Overview" }).click()
  await expect(tablist.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "true")
  await expect(page.locator("text=Teams").first()).toBeVisible({ timeout: 5_000 })
  await expect(page.locator("text=Areas").first()).toBeVisible()
  // URL deep-link writes the new tab id without scrolling.
  await expect(page).toHaveURL(/\?tab=overview/)

  // 6. Click Insights → Cost-by-area + Compare panels appear.
  await tablist.getByRole("tab", { name: "Insights" }).click()
  await expect(tablist.getByRole("tab", { name: "Insights" })).toHaveAttribute("aria-selected", "true")
  await expect(page.locator("text=/cost.by.area/i").first()).toBeVisible({ timeout: 5_000 })

  // 7. Direct deep-link via URL works on a fresh load. Land on Activity tab.
  await page.goto(`/projects/${projectId}?tab=activity`)
  await expect(page.getByRole("tablist", { name: /project sections/i }).getByRole("tab", { name: "Activity" }))
    .toHaveAttribute("aria-selected", "true", { timeout: 5_000 })
})
