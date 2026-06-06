import coreWebVitals from "eslint-config-next/core-web-vitals"
import typescript from "eslint-config-next/typescript"

// Flat ESLint config using Next 16's native flat-config exports (next lint was
// removed in Next 16 — run `eslint .` directly).
//
// Introducing lint to an existing codebase: a handful of rules have pre-existing
// violations (intentional `any` casts at fetch/JSON boundaries, setState-on-mount
// rehydration, a few unescaped apostrophes in copy). Those are downgraded to
// warnings so the gate can land now and catch NEW errors (syntax, undefined refs,
// bad hooks) without forcing a large, risky refactor. Tighten them back to "error"
// in a follow-up as the warnings are burned down.
export default [
  ...coreWebVitals,
  ...typescript,
  {
    rules: {
      "@typescript-eslint/no-explicit-any": "warn",
      "react-hooks/set-state-in-effect": "warn",
      "react/no-unescaped-entities": "warn",
    },
  },
  { ignores: [".next/**", "node_modules/**", "next-env.d.ts", "*.config.*"] },
]
