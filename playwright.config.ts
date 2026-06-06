import { defineConfig, devices } from "@playwright/test"

/**
 * Playwright config for the BidBuilder happy-path E2E (19.6).
 *
 * Targets the LIVE dev stack (web on :3100, api on :8081, db on :5433) — bring it
 * up first with `docker compose up -d --build api web`. We don't spin a webServer
 * here on purpose: the test exercises the real stack so a regression in API
 * wiring, auth, or DB seed shows up. CI doesn't run Playwright yet (the stack
 * needs more lift than the existing Postgres service container) — runs locally
 * via `npm run test:e2e`.
 */
const PORT = Number(process.env.WEB_PORT ?? 3100)

export default defineConfig({
  testDir: "./tests-e2e",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  retries: 0,
  reporter: process.env.CI ? "list" : [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: process.env.WEB_URL ?? `http://localhost:${PORT}`,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
  },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
  ],
})
