import { describe, it, expect } from "vitest"
import { cn, money } from "./utils"

describe("cn", () => {
  it("joins class names", () => {
    expect(cn("a", "b")).toBe("a b")
  })
  it("drops falsy values", () => {
    expect(cn("a", false && "b", undefined, "c")).toBe("a c")
  })
  it("resolves conflicting tailwind classes (last wins)", () => {
    expect(cn("px-2", "px-4")).toBe("px-4")
  })
})

describe("money", () => {
  // Intl separates the currency code from the number with a non-breaking space;
  // normalise it so the assertions read naturally.
  const norm = (s: string) => s.replace(/ /g, " ")
  it("formats AED with two decimals and a thousands separator", () => {
    expect(norm(money(1234.5))).toBe("AED 1,234.50")
  })
  it("honours a different currency", () => {
    // en-US renders USD with the $ sign.
    expect(norm(money(1000, "USD"))).toBe("$1,000.00")
  })
  it("formats zero", () => {
    expect(norm(money(0))).toBe("AED 0.00")
  })
})
