import { describe, it, expect } from "vitest"
import { divisorFromAreas } from "./stat-cards"
import type { Area } from "@/lib/types"

// Helper to build a minimal Area; defaults match the columns we don't care
// about so each test row reads as "one or two interesting fields".
const area = (over: Partial<Area> = {}): Area => ({
  id: 0, parentAreaId: null, name: "x", code: null,
  kind: "Area", sortOrder: 0, quantity: 0, unit: null, ...over,
})

describe("divisorFromAreas (25.5)", () => {
  it("returns zero divisor when there are no areas at all", () => {
    expect(divisorFromAreas(undefined)).toEqual({ quantity: 0, unit: "" })
    expect(divisorFromAreas([])).toEqual({ quantity: 0, unit: "" })
  })

  it("sums quantities across multiple top-level areas (sibling buildings)", () => {
    // Two top-level "buildings" tagged in m² → total project footprint.
    const result = divisorFromAreas([
      area({ id: 1, parentAreaId: null, quantity: 1200, unit: "m²" }),
      area({ id: 2, parentAreaId: null, quantity: 800, unit: "m²" }),
    ])
    expect(result).toEqual({ quantity: 2000, unit: "m²" })
  })

  it("ignores non-top-level areas when summing the divisor", () => {
    // Top-level (parentAreaId == null) is 500. The sub-area's 300 is rolled
    // up inside its parent and would double-count if we summed it too.
    const result = divisorFromAreas([
      area({ id: 1, parentAreaId: null, quantity: 500, unit: "m²" }),
      area({ id: 2, parentAreaId: 1, quantity: 300, unit: "m²" }),    // sub-area
      area({ id: 3, parentAreaId: 2, quantity: 100, unit: "m²" }),    // unit
    ])
    expect(result.quantity).toBe(500)
    expect(result.unit).toBe("m²")
  })

  it("picks the FIRST top-level area's unit (mixed units are the user's call)", () => {
    // The user mixed m² and rooms across two top-level areas; we don't enforce
    // consistency — we report the first defined unit and let them decide. The
    // sum is mathematically wrong (m² + rooms), but the toggle still appears.
    const result = divisorFromAreas([
      area({ id: 1, parentAreaId: null, quantity: 100, unit: "m²" }),
      area({ id: 2, parentAreaId: null, quantity: 5, unit: "room" }),
    ])
    expect(result.quantity).toBe(105)
    expect(result.unit).toBe("m²")
  })

  it("returns empty unit when no top-level area has a defined Unit", () => {
    // 25.5 — Returning "" (not "unit") is what gates the toggle in StatCards:
    // an unlabelled divisor produces meaningless "money per unit" values, so
    // we hide the toggle entirely in that case rather than mislead the user.
    const result = divisorFromAreas([
      area({ id: 1, parentAreaId: null, quantity: 100, unit: null }),
    ])
    expect(result.unit).toBe("")
  })

  it("trims whitespace from the returned unit label", () => {
    // Even though the AreaEndpoints write path trims, defensively the divisor
    // helper trims too so " m² " never leaks into the toggle label.
    const result = divisorFromAreas([
      area({ id: 1, parentAreaId: null, quantity: 100, unit: " m² " }),
    ])
    expect(result.unit).toBe("m²")
  })

  it("returns zero quantity when no top-level area has Quantity > 0", () => {
    // Quantity not set on any top-level area → divisor is zero → toggle hides.
    const result = divisorFromAreas([
      area({ id: 1, parentAreaId: null, quantity: 0, unit: "m²" }),
      area({ id: 2, parentAreaId: 1, quantity: 50, unit: "m²" }),
    ])
    expect(result.quantity).toBe(0)
  })

  it("skips a top-level area with empty Unit string when picking the label", () => {
    // The first top-level row has an empty-string unit; the second has "m²".
    // We want "m²" — empty strings are equivalent to unset for the label.
    const result = divisorFromAreas([
      area({ id: 1, parentAreaId: null, quantity: 100, unit: "" }),
      area({ id: 2, parentAreaId: null, quantity: 50, unit: "m²" }),
    ])
    expect(result.unit).toBe("m²")
  })
})
