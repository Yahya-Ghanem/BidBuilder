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
    environment: "node",
    // app/**/*.test.ts added in 25.5 so the StatCards divisor helper can be
    // unit-tested next to its source. lib/ remains the home for shared helpers;
    // app/ tests should be small + pure (no React rendering).
    include: ["lib/**/*.test.ts", "tests/**/*.test.ts", "app/**/*.test.ts"],
  },
})
