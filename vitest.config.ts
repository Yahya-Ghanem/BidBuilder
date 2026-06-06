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
    include: ["lib/**/*.test.ts", "tests/**/*.test.ts"],
  },
})
