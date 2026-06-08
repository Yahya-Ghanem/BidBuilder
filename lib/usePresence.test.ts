import { describe, it, expect, beforeEach, afterEach, vi } from "vitest"
import { renderHook, act } from "@testing-library/react"
import { usePresence, PRESENCE_HEARTBEAT_MS } from "@/lib/usePresence"
import type { PresenceUser } from "@/lib/types"

/** Build a fetch mock whose call number is reflected in the response body —
 *  callers can assert which beat returned which list. Each call resolves to
 *  `{ users: [...] }` matching the PresenceList wire shape. */
function makeFetchMock(usersPerCall: PresenceUser[][]) {
  let i = 0
  // Typed signature so `mock.calls[0]` resolves to `[RequestInfo, RequestInit?]`
  // and not the default `[]`-tuple, which tsc rejects when indexed.
  const fn: (input: RequestInfo, init?: RequestInit) => Promise<Response> = async () => {
    const users = usersPerCall[Math.min(i, usersPerCall.length - 1)] ?? []
    i++
    return {
      ok: true,
      status: 200,
      json: async () => ({ users }),
    } as unknown as Response
  }
  return vi.fn(fn)
}

/** Flush pending microtasks under fake timers — `await vi.advanceTimersByTimeAsync(0)`
 *  resolves the next tick of awaited promises (the mount-time `void beat()` chain). */
async function flush() {
  await act(async () => { await vi.advanceTimersByTimeAsync(0) })
}

const ALICE: PresenceUser = { id: 1, name: "Alice", email: "a@x", lastSeenAt: "2026-06-08T00:00:00Z" }
const BOB:   PresenceUser = { id: 2, name: "Bob",   email: "b@x", lastSeenAt: "2026-06-08T00:00:30Z" }

describe("usePresence", () => {
  beforeEach(() => {
    vi.useFakeTimers()
    // Avoid the 401 redirect path — fetchApi calls session.getToken() which
    // hits localStorage. jsdom provides it; we just need a token present so
    // the request goes out as Bearer-authed.
    localStorage.setItem("bb_token", "test-token")
    localStorage.setItem("bb_tenant", "default")
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
    localStorage.clear()
  })

  it("posts a heartbeat on mount and exposes the returned list", async () => {
    const fetchMock = makeFetchMock([[ALICE]])
    vi.stubGlobal("fetch", fetchMock)

    const { result } = renderHook(() => usePresence(42))
    await flush()

    expect(result.current.users).toEqual([ALICE])
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]
    expect(String(url)).toContain("/api/estimates/42/presence")
    expect((init as RequestInit).method).toBe("POST")
  })

  it("re-heartbeats every PRESENCE_HEARTBEAT_MS and surfaces the latest list", async () => {
    const fetchMock = makeFetchMock([[ALICE], [ALICE, BOB]])
    vi.stubGlobal("fetch", fetchMock)

    const { result } = renderHook(() => usePresence(7))
    await flush()
    expect(result.current.users).toEqual([ALICE])
    expect(fetchMock).toHaveBeenCalledTimes(1)

    // One full interval later → the second response with Bob added is observed.
    await act(async () => { await vi.advanceTimersByTimeAsync(PRESENCE_HEARTBEAT_MS) })
    expect(result.current.users).toEqual([ALICE, BOB])
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it("does not fire any request when estimateId is null", async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal("fetch", fetchMock)

    const { result } = renderHook(() => usePresence(null))
    await flush()
    expect(result.current.users).toEqual([])
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it("stops polling on unmount — no orphan interval keeps hitting the API", async () => {
    const fetchMock = makeFetchMock([[ALICE], [ALICE], [ALICE]])
    vi.stubGlobal("fetch", fetchMock)

    const { unmount } = renderHook(() => usePresence(9))
    await flush()
    expect(fetchMock).toHaveBeenCalledTimes(1)

    unmount()
    await act(async () => { await vi.advanceTimersByTimeAsync(PRESENCE_HEARTBEAT_MS * 3) })
    // Still 1 — the interval was cleared on cleanup.
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it("swallows transient errors and keeps the previously-known list", async () => {
    // Fail the FIRST call, succeed on the second — we should observe Alice after retry.
    const calls = vi.fn(async () => {
      const n = calls.mock.calls.length
      if (n === 1) throw new Error("transient")
      return { ok: true, status: 200, json: async () => ({ users: [ALICE] }) } as unknown as Response
    })
    vi.stubGlobal("fetch", calls)

    const { result } = renderHook(() => usePresence(11))
    await flush()
    // First beat rejected — no users, no thrown error escapes the hook.
    expect(result.current.users).toEqual([])

    await act(async () => { await vi.advanceTimersByTimeAsync(PRESENCE_HEARTBEAT_MS) })
    expect(result.current.users).toEqual([ALICE])
  })
})
