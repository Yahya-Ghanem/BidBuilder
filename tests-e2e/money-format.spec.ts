import { test, expect } from "@playwright/test"

/**
 * 25.1 — Live smoke for the currency-dup fix.
 *
 * Reproduces the regression scenario: seed a subcontractor RFQ + a submitted
 * priced response with quotedAmount = 187,500.50, navigate to the register
 * page, and assert the rendered row contains the AED amount exactly ONCE
 * (no trailing "AED" suffix from the old `{money(x)} <span>{currency}</span>`
 * pattern, no leaked default-AED prefix when a different currency was passed).
 *
 * This complements the unit tests in lib/format.test.ts — those prove the
 * pure composeMoney() output, this proves the assembled UI doesn't reintroduce
 * the bug class on the actual page that originally exhibited it.
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
const TENANT = "default"

test("subcontractor-quotes: currency appears exactly once per quote row", async ({ page, request }) => {
  // 1. Auth via the UI so the JWT is in localStorage where the SPA expects it.
  await page.goto("/login")
  await page.locator('input[type="email"]').fill("admin@bidbuilder.local")
  await page.locator('input[type="password"]').fill("Admin@12345")
  await page.getByRole("button", { name: /sign in/i }).click()
  await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })

  const token = await page.evaluate(() => localStorage.getItem("bb_token"))
  expect(token).toBeTruthy()
  const authHdr = { Authorization: `Bearer ${token}`, "X-Tenant-Id": TENANT }

  // 2. Discover a project to anchor the RFQ on. The seeded sample has at least
  //    one project (PRJ-2026-001) — see happy-path.spec.ts.
  const projects = await (await request.get(`${API_URL}/api/projects`, { headers: authHdr })).json() as { id: number }[]
  expect(projects.length, "seeded projects present").toBeGreaterThan(0)
  const projectId = projects[0].id

  // 3. Create an RFQ for the test, then fetch its public portal token and
  //    submit a priced response WITH the exact value (187,500.50) that
  //    appeared in the original bug report.
  const rfq = await (await request.post(`${API_URL}/api/subcontractor-quotes`, {
    headers: { ...authHdr, "Content-Type": "application/json" },
    data: {
      projectId,
      trade: "Smoke — currency format",
      scope: "25.1 live smoke for the AED dup-currency fix",
      contractorName: "Smoke Contractor LLC",
      contractorEmail: null,
      currency: "AED",
      validDays: 1,
    },
  })).json() as { id: number; token: string }

  // The PortalLinkSigner currently allows bare tokens (no sig/exp) for the
  // back-compat release window; that's enough for this smoke test.
  const submitted = await request.post(`${API_URL}/api/portal/${rfq.token}`, {
    headers: { "Content-Type": "application/json" },
    data: { amount: 187500.5, respondentName: "Smoke Test", notes: null },
  })
  expect(submitted.ok(), `portal POST → ${submitted.status()}`).toBeTruthy()

  // 4. Navigate to the register and assert the amount cell renders the AED
  //    figure exactly once — i.e. NO trailing "AED" suffix the old code
  //    produced. The new <Money> uses Intl, which emits "AED 187,500.50".
  await page.goto("/subcontractor-quotes")
  // Stale rows from earlier failed runs may share the trade label; `.first()`
  // grabs any matching priced row — they all exercise the same render path.
  const row = page.locator("tr", { hasText: "Smoke — currency format" }).filter({ hasText: "187,500.50" }).first()
  await expect(row).toBeVisible({ timeout: 10_000 })

  const cellText = (await row.locator("td").nth(4).innerText()).replace(/\s+/g, " ").trim()
  // Strip non-breaking spaces Intl may emit between code and amount.
  const normalised = cellText.replace(/ /g, " ")

  expect(normalised).toContain("187,500.50")
  // The whole point of the fix: AED must appear EXACTLY once in this cell.
  const occurrences = (normalised.match(/AED/g) ?? []).length
  expect(occurrences, `AED count in "${normalised}"`).toBe(1)

  // 5. Cleanup so re-runs stay idempotent.
  await request.delete(`${API_URL}/api/subcontractor-quotes/${rfq.id}`, { headers: authHdr })
})
