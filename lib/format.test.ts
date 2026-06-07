import { describe, it, expect } from "vitest"
import { formatNumber, formatCurrency, formatDate, composeMoney } from "./format"

// Strip non-breaking spaces (Intl uses U+00A0 between currency code and amount).
const norm = (s: string) => s.replace(/ /g, " ")

// Arabic-Indic digit range (٠-٩). The whole point of format.ts is that these NEVER
// appear — business figures stay in Western digits regardless of locale.
const hasArabicIndicDigits = (s: string) => /[٠-٩]/.test(s)

describe("formatNumber", () => {
  it("groups thousands in English with Western digits", () => {
    expect(formatNumber(1234.5, "en")).toBe("1,234.5")
  })
  it("keeps Western digits under Arabic (no Arabic-Indic numerals)", () => {
    const out = formatNumber(1234.5, "ar")
    expect(hasArabicIndicDigits(out)).toBe(false)
    expect(out).toMatch(/1.?234/)   // grouping separator may differ, digits must be Latin
  })
})

describe("formatCurrency", () => {
  it("formats with 2 decimals and Western digits in English", () => {
    const out = formatCurrency(1000, "USD", "en")
    expect(out).toContain("1,000.00")
    expect(hasArabicIndicDigits(out)).toBe(false)
  })
  it("keeps Western digits for AED under Arabic", () => {
    const out = formatCurrency(1000, "AED", "ar")
    expect(hasArabicIndicDigits(out)).toBe(false)
    expect(out).toMatch(/1.?000/)
  })
})

describe("composeMoney", () => {
  it("renders the exact bug-class example as `AED 187,500.50` (one currency code, no dup suffix)", () => {
    // This is THE regression we are fixing. Before <Money>, the subcontractor-quotes
    // page rendered "AED 187,500.50 AED" because `money(value)` already prefixes
    // the code and the call site appended `{q.currency}` next to it.
    expect(norm(composeMoney(187500.5, "AED"))).toBe("AED 187,500.50")
  })

  it("matches formatCurrency exactly when there is no secondary (single source of truth)", () => {
    // Whatever Intl produces — `$1,000.00` for USD, `AED 1,000.00` for AED,
    // `SAR 1,000.00` for SAR — composeMoney returns it verbatim. So if Intl
    // emits the currency exactly once, composeMoney does too.
    for (const code of ["AED", "USD", "EUR", "SAR", "GBP"]) {
      expect(composeMoney(1234.56, code)).toBe(formatCurrency(1234.56, code))
    }
  })

  it("does not leak a default 'AED' when the currency is something else", () => {
    // Old call sites called `money(value)` without a currency arg, so it always
    // defaulted to AED — meaning a USD figure rendered as "AED 1,000.00 USD".
    // The new component requires currency as a prop, so the default cannot leak.
    expect(composeMoney(187500.5, "USD")).not.toContain("AED")
    expect(composeMoney(187500.5, "EUR")).not.toContain("AED")
    expect(composeMoney(187500.5, "SAR")).not.toContain("AED")
  })

  it("renders primary then secondary, each amount appearing exactly once", () => {
    // The ONLY case where two currency renderings legitimately sit next to each
    // other: an FX-converted suffix. Each amount appears once, separated by ≈.
    const out = norm(composeMoney(2724499.41, "SAR", "en", { value: 726533.18, currency: "USD" }))
    expect(out).toContain("2,724,499.41")
    expect(out).toContain("726,533.18")
    expect(out).toContain("≈")
    const expected = `${norm(formatCurrency(2724499.41, "SAR"))} ≈ ${norm(formatCurrency(726533.18, "USD"))}`
    expect(out).toBe(expected)
  })

  it("under Arabic keeps Western digits and contains the amount exactly once", () => {
    // Arabic locale must still use Latin digits for business figures.
    const out = norm(composeMoney(187500.5, "AED", "ar"))
    expect(hasArabicIndicDigits(out)).toBe(false)
    expect(out).toMatch(/187.?500/)                     // the digits show up
    expect(out.match(/187.?500/g)?.length ?? 0).toBe(1) // and ONLY once
  })
})

describe("formatDate", () => {
  it("formats an ISO date with Western digits", () => {
    const out = formatDate("2026-06-07", "en")
    expect(hasArabicIndicDigits(out)).toBe(false)
    expect(out).toContain("2026")
  })
  it("keeps Western digits under Arabic", () => {
    expect(hasArabicIndicDigits(formatDate("2026-06-07", "ar"))).toBe(false)
  })
  it("returns the input unchanged when it isn't a parseable date", () => {
    expect(formatDate("not-a-date", "en")).toBe("not-a-date")
  })
})
