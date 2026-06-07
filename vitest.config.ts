import { defineConfig } from "vitest/config"
import { fileURLToPath } from "node:url"

// Lightweight unit-test runner for pure frontend logic (utils, guards). Runs in a
// node environment — no browser/stack needed — so it's fast and reliable in CI.
export default defineConfig({
  resolve: {
    // Mirror the tsconfig "@/*" path alias so tests can import the same way the app does.
    alias: { "@": fileURLToPath(new URL("./", import.meta.url)) },
  },
  test: {
    // jsdom needed by 26.4 — DataTable test uses @testing-library/react which
    // requires window/document. Node remains the default for everything else
    // via the per-test `@vitest-environment` pragma when needed; jsdom is the
    // safe default since it's a superset (Node globals still available).
    environment: "jsdom",
    // app/**/*.test.ts (25.5) for StatCards-side helpers.
    // components/**/*.test.{ts,tsx} (26.4) for the DataTable primitive and
    // future component tests that exercise React rendering directly.
    include: [
      "lib/**/*.test.ts",
      "tests/**/*.test.ts",
      "app/**/*.test.ts",
      "components/**/*.test.{ts,tsx}",
    ],
  },
})
