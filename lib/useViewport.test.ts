import { describe, it, expect, beforeEach, afterEach, vi } from "vitest"
import { renderHook, act } from "@testing-library/react"
import { useViewport, MOBILE_BREAKPOINT_PX } from "@/lib/useViewport"

/** Minimal MediaQueryList stub: lets the test flip `matches` and fire a 'change'. */
function makeMql(initial: boolean) {
  let matches = initial
  const listeners: ((e: MediaQueryListEvent) => void)[] = []
  return {
    get matches() { return matches },
    media: `(max-width: ${MOBILE_BREAKPOINT_PX}px)`,
    onchange: null,
    addEventListener(_t: string, l: (e: MediaQueryListEvent) => void) { listeners.push(l) },
    removeEventListener(_t: string, l: (e: MediaQueryListEvent) => void) {
      const i = listeners.indexOf(l); if (i >= 0) listeners.splice(i, 1)
    },
    addListener() {},
    removeListener() {},
    dispatchEvent: () => true,
    /** Test helper: simulate a viewport crossing the breakpoint. */
    set(v: boolean) {
      matches = v
      for (const l of listeners) l({ matches } as MediaQueryListEvent)
    },
  }
}

describe("useViewport", () => {
  let mql: ReturnType<typeof makeMql>

  beforeEach(() => {
    mql = makeMql(false)
    vi.stubGlobal("matchMedia", vi.fn().mockImplementation(() => mql))
    Object.defineProperty(window, "matchMedia", { writable: true, value: window.matchMedia })
  })
  afterEach(() => { vi.unstubAllGlobals() })

  it("starts at isMobile=false on first render (SSR-safe default)", () => {
    const { result } = renderHook(() => useViewport())
    // After mount the effect runs and reads matchMedia — but our stub starts false.
    expect(result.current.isMobile).toBe(false)
  })

  it("returns isMobile=true when the viewport matches the mobile media query on mount", () => {
    mql = makeMql(true)
    vi.stubGlobal("matchMedia", vi.fn().mockImplementation(() => mql))
    const { result } = renderHook(() => useViewport())
    expect(result.current.isMobile).toBe(true)
  })

  it("flips isMobile when the viewport crosses the breakpoint", () => {
    const { result } = renderHook(() => useViewport())
    expect(result.current.isMobile).toBe(false)
    act(() => mql.set(true))
    expect(result.current.isMobile).toBe(true)
    act(() => mql.set(false))
    expect(result.current.isMobile).toBe(false)
  })

  it("uses Tailwind's md breakpoint (768px)", () => {
    expect(MOBILE_BREAKPOINT_PX).toBe(768)
  })
})
