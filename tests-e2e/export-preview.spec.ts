import { test, expect, type APIRequestContext } from "@playwright/test"
import { createTestUser, deleteTestUser, signIn, type TestUser } from "./_helpers/auth"

/**
 * 28.3 — Acceptance bar: "Clicking PDF opens an inline preview; clicking
 * Download saves the file; user can close the preview without downloading."
 *
 * This spec proves the close-without-download lane is real (a user can verify
 * the figures, walk away, and nothing landed in their downloads folder), then
 * re-opens the modal and proves the Download button still produces a file.
 *
 *   1. Sign in (seeded admin) and create an isolated project + estimate via
 *      the API so the Export ▾ dropdown is guaranteed to render (the seeded
 *      sample project is shared with other parallel specs and was racy).
 *   2. Navigate to the project → click Export ▾ → PDF.
 *   3. Assert the preview modal is visible and contains an iframe with
 *      data-kind="pdf" — the iframe is the only way the preview body reaches
 *      the user, so its presence is the smoke signal.
 *   4. Click Close. Modal is gone. NO download was triggered.
 *   5. Re-open via Export ▾ → PDF. Click Download. A download event fires
 *      (Playwright's `waitForEvent("download")` rejects if it doesn't).
 *
 * The PDF lane is the one with the most-visible bug class (a wrong figure
 * mailed to a client), so the spec exercises it specifically. xlsx/csv reuse
 * the same modal component and the backend is exercised by ExportPreviewTests.
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
const TENANT = "default"

async function seedProjectWithEstimate(request: APIRequestContext, token: string) {
  const auth = { Authorization: `Bearer ${token}`, "X-Tenant-Id": TENANT }
  const code = `EXP-${Date.now()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
  const proj = await (await request.post(`${API_URL}/api/projects`, {
    headers: { ...auth, "Content-Type": "application/json" },
    data: { code, name: "28.3 export-preview smoke", currency: "AED", status: "Bidding", durationMonths: 6 },
  })).json() as { id: number }
  const est = await (await request.post(`${API_URL}/api/projects/${proj.id}/estimates`, {
    headers: { ...auth, "Content-Type": "application/json" },
    data: { title: "preview smoke" },
  })).json() as { id: number; rowVersion: string }
  // One priced line so the PDF has something to render — an empty estimate
  // still exports, but a populated one is a stronger smoke signal.
  const sec = await (await request.post(`${API_URL}/api/estimates/${est.id}/sections`, {
    headers: { ...auth, "Content-Type": "application/json", "If-Match": est.rowVersion },
    data: { code: "S", title: "smoke", sortOrder: 0 },
  })).json() as { sections: Array<{ id: number; code: string }>; rowVersion: string }
  const sid = sec.sections.find((s) => s.code === "S")!.id
  await request.post(`${API_URL}/api/estimates/${est.id}/sections/${sid}/items`, {
    headers: { ...auth, "Content-Type": "application/json", "If-Match": sec.rowVersion },
    data: { description: "line", unit: "m", quantity: 1, unitRate: 100, sortOrder: 0 },
  })
  return proj.id
}

// 29.A.1 — per-spec throwaway user, torn down after the test.
let user: TestUser | undefined
test.afterEach(async ({ request }) => { await deleteTestUser(request, user); user = undefined })

test.describe("BOQ export preview (28.3)", () => {
  test("preview opens, close without download, then download from preview", async ({ page, request }) => {
    // Sign in as a per-spec user — token injected, no login-UI round-trip.
    user = await createTestUser(request)
    await signIn(page, user)
    const projectId = await seedProjectWithEstimate(request, user.token)

    await page.goto(`/projects/${projectId}`)

    // The Export ▾ trigger lives in the estimate-editor's meta header (above
    // the tab strip per 25.3, so visible from every tab). Wait for it.
    const exportBtn = page.getByRole("button", { name: /export this revision/i })
    await expect(exportBtn).toBeVisible({ timeout: 15_000 })

    // Open Export ▾ → PDF.
    await exportBtn.click()
    await page.getByRole("menuitem", { name: "PDF" }).click()

    // The preview modal appears. The iframe is loaded asynchronously — give
    // the auth + render path a generous timeout; the PDF render hits QuestPDF.
    const modal = page.locator('[data-testid="export-preview-modal"]')
    await expect(modal).toBeVisible({ timeout: 10_000 })
    const iframe = page.locator('[data-testid="export-preview-iframe"][data-kind="pdf"]')
    await expect(iframe).toBeVisible({ timeout: 20_000 })

    // Close without downloading. The modal goes away; no file was saved.
    await page.locator('[data-testid="export-preview-close"]').click()
    await expect(modal).toBeHidden()

    // Re-open the preview and Download. The button is disabled until the
    // preview load completes (so the user can't double-fire while loading);
    // we wait for the iframe to be visible again first.
    await exportBtn.click()
    await page.getByRole("menuitem", { name: "PDF" }).click()
    await expect(modal).toBeVisible()
    await expect(iframe).toBeVisible({ timeout: 20_000 })

    // The Download button triggers a normal browser download; capture the
    // event to prove the file actually came through.
    const downloadPromise = page.waitForEvent("download", { timeout: 15_000 })
    await page.locator('[data-testid="export-preview-download"]').click()
    const dl = await downloadPromise
    expect(dl.suggestedFilename()).toMatch(/\.pdf$/)
  })
})
