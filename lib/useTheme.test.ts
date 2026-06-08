import { describe, it, expect, beforeEach, afterEach, vi } from "vitest"
import { renderHook, act } from "@testing-library/react"
import { useTheme, readStoredTheme, resolveTheme } from "@/lib/useTheme"

/**
 * 28.1 — useTheme unit tests.
 *
 * Focus: the pure / observable behaviour. We don't drive the network
 * (fetchApi) here — the hook fires-and-forgets the PUT and a failing
 * request must NOT roll back the local pick. Other tests (api integration)
 * cover the server side.
 *
 * jsdom doesn't ship a real matchMedia, so we install a small mock that
 * lets each test choose what the OS "prefers". The mock supports the
 * addEventListener / removeEventListener path useTheme uses.
 */

type Listener = (ev: MediaQueryListEvent) => void
let osPrefersDark = false
const listeners = new Set<Listener>()

function installMatchMedia() {
  // The hook only ever queries this one media string; keep the mock simple.
  // `matches` is a getter so the hook's onChange reads the CURRENT value, not
  // the snapshot at matchMedia() call time — emitOsThemeChange flips the var.
  vi.stubGlobal("matchMedia", (query: string) => {
    const isDarkQuery = query.includes("dark")
    return {
      get matches() { return isDarkQuery ? osPrefersDark : false },
      media: query,
      addEventListener: (_: "change", l: Listener) => { listeners.add(l) },
      removeEventListener: (_: "change", l: Listener) => { listeners.delete(l) },
      // Legacy API some libs still call; harmless no-op.
      addListener: () => {},
      removeListener: () => {},
      onchange: null,
      dispatchEvent: () => false,
    }
  })
}

function emitOsThemeChange(toDark: boolean) {
  osPrefersDark = toDark
  // Fire the listener synchronously inside act so React state updates flush.
  listeners.forEach(l => l({ matches: toDark } as MediaQueryListEvent))
}

// Silence the best-effort PUT — we don't care about its outcome.
const fetchStub = vi.fn(async () => ({ ok: true, status: 200, json: async () => ({}) }) as Response)

describe("useTheme", () => {
  beforeEach(() => {
    osPrefersDark = false
    listeners.clear()
    localStorage.clear()
    document.documentElement.removeAttribute("data-theme")
    installMatchMedia()
    vi.stubGlobal("fetch", fetchStub)
    fetchStub.mockClear()
  })
  afterEach(() => { vi.unstubAllGlobals() })

  it("readStoredTheme defaults to 'system' when nothing is persisted", () => {
    expect(readStoredTheme()).toBe("system")
  })

  it("readStoredTheme returns the persisted value when valid, ignores garbage", () => {
    localStorage.setItem("bb.theme", "dark")
    expect(readStoredTheme()).toBe("dark")
    localStorage.setItem("bb.theme", "neon")
    expect(readStoredTheme()).toBe("system")
  })

  it("resolveTheme follows the OS for 'system' and passes through otherwise", () => {
    osPrefersDark = false
    expect(resolveTheme("system")).toBe("light")
    osPrefersDark = true
    expect(resolveTheme("system")).toBe("dark")
    expect(resolveTheme("dark")).toBe("dark")
    expect(resolveTheme("light")).toBe("light")
  })

  it("setTheme persists locally, applies the data-theme attribute, and PUTs", () => {
    const { result } = renderHook(() => useTheme())
    act(() => { result.current.setTheme("dark") })

    expect(result.current.preference).toBe("dark")
    expect(result.current.resolved).toBe("dark")
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark")
    expect(localStorage.getItem("bb.theme")).toBe("dark")
    // Best-effort sync fired exactly once with the new value.
    expect(fetchStub).toHaveBeenCalledTimes(1)
    const body = JSON.parse((fetchStub.mock.calls[0][1] as RequestInit).body as string)
    expect(body).toEqual({ theme: "dark" })
  })

  it("rejects invalid theme values (no DOM mutation, no network)", () => {
    const { result } = renderHook(() => useTheme())
    act(() => {
      // @ts-expect-error — exercising the runtime guard, not the types.
      result.current.setTheme("neon")
    })
    expect(result.current.preference).toBe("system")
    expect(fetchStub).not.toHaveBeenCalled()
  })

  it("when preference is 'system', OS theme changes flip the resolved value live", () => {
    localStorage.setItem("bb.theme", "system")
    osPrefersDark = false
    const { result } = renderHook(() => useTheme())
    // After mount the listener is attached; flip the OS and assert the hook reacts.
    act(() => { emitOsThemeChange(true) })
    expect(result.current.resolved).toBe("dark")
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark")

    act(() => { emitOsThemeChange(false) })
    expect(result.current.resolved).toBe("light")
    expect(document.documentElement.getAttribute("data-theme")).toBe("light")
  })

  it("when preference is explicit 'light', OS dark does NOT override it", () => {
    localStorage.setItem("bb.theme", "light")
    const { result } = renderHook(() => useTheme())
    act(() => { emitOsThemeChange(true) })
    // The hook only attaches the matchMedia listener for "system". An explicit
    // pick stays put no matter what the OS reports.
    expect(result.current.preference).toBe("light")
    expect(result.current.resolved).toBe("light")
  })

  it("a failed PUT does not roll back the local pick", async () => {
    fetchStub.mockImplementationOnce(async () => { throw new Error("offline") })
    const { result } = renderHook(() => useTheme())
    await act(async () => { result.current.setTheme("dark") })
    expect(result.current.preference).toBe("dark")
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark")
  })
})
