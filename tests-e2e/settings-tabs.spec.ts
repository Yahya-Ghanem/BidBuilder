import { test, expect } from "@playwright/test"
import { createTestUser, deleteTestUser, signIn, type TestUser } from "./_helpers/auth"

/**
 * 25.2 — Settings sub-navigation smoke.
 *
 * The settings page used to be 14 cards stacked in a 6,000-px column. Now it's
 * four tabs: Company · Workspace · Catalogues · Integrations. This spec
 * exercises:
 *   • Default landing = Company; "Company profile" card visible there.
 *   • Tab strip uses WAI-ARIA tablist semantics (role=tab + aria-selected).
 *   • Clicking Workspace surfaces a known card unique to Workspace
 *     ("Estimating defaults") AND updates ?tab=workspace in the URL.
 *   • Direct deep-link to `?tab=integrations` lands on Integrations and
 *     surfaces the SSO card (which only exists there now).
 *
 * Asserting card *containers* — not their inner form values — keeps this
 * resilient to seed drift the way 25.3's project-tabs spec does.
 */

// 29.A.1 — per-spec throwaway user, torn down after the test.
let user: TestUser | undefined
test.afterEach(async ({ request }) => { await deleteTestUser(request, user); user = undefined })

test("settings page renders four tabs and deep-links via ?tab=", async ({ page, request }) => {
  // Sign in — /settings is gated. Per-spec user, no login-UI round-trip.
  user = await createTestUser(request)
  await signIn(page, user)

  await page.goto("/settings")

  // 1. Tab strip with four tabs.
  const tablist = page.getByRole("tablist", { name: /settings sections/i })
  await expect(tablist).toBeVisible({ timeout: 10_000 })
  for (const name of ["Company", "Workspace", "Catalogues", "Integrations"]) {
    await expect(tablist.getByRole("tab", { name })).toBeVisible()
  }

  // 2. Default = Company.
  await expect(tablist.getByRole("tab", { name: "Company" })).toHaveAttribute("aria-selected", "true")
  // The "Company profile" card heading is unique to this tab.
  await expect(page.locator("text=Company profile").first()).toBeVisible()

  // 3. Click Workspace → its panel becomes the visible one + URL deep-link.
  await tablist.getByRole("tab", { name: "Workspace" }).click()
  await expect(tablist.getByRole("tab", { name: "Workspace" })).toHaveAttribute("aria-selected", "true")
  await expect(page.locator("#panel-workspace")).toBeVisible()
  await expect(page.locator("#panel-company")).toBeHidden()
  await expect(page.locator("text=Estimating defaults").first()).toBeVisible({ timeout: 5_000 })
  await expect(page).toHaveURL(/\?tab=workspace/)

  // 4. Direct deep-link via URL lands on Integrations.
  await page.goto("/settings?tab=integrations")
  const tablist2 = page.getByRole("tablist", { name: /settings sections/i })
  await expect(tablist2.getByRole("tab", { name: "Integrations" }))
    .toHaveAttribute("aria-selected", "true", { timeout: 10_000 })
  // The SSO card heading lives only in the Integrations tab now.
  await expect(page.locator("text=Single sign-on").first()).toBeVisible({ timeout: 5_000 })
})
