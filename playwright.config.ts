import { defineConfig, devices } from "@playwright/test"

/**
 * Playwright config for the BidBuilder happy-path E2E (19.6 + 20.1).
 *
 * Targets the LIVE stack — web on :3100, api on :8081, db on :5433 locally; the
 * GH-runner ports in CI. We don't spin a webServer here on purpose: the test
 * exercises the real stack so a regression in API wiring, auth, or DB seed
 * shows up.
 *
 *   Local:  docker compose up -d --build api web  →  npm run test:e2e
 *   CI:     the `e2e` job in .github/workflows/ci.yml boots dotnet + next,
 *           waits for /healthz, then runs `npm run test:e2e`.
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
