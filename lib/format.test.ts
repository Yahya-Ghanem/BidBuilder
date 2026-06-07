import { describe, it, expect } from "vitest"
import { formatNumber, formatCurrency, formatDate } from "./format"

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
