import { describe, it, expect, beforeEach } from "vitest"
import { renderHook, act } from "@testing-library/react"
import { useRecentEntities, type RecentEntity } from "@/lib/useRecentEntities"

/**
 * 28.5 — useRecentEntities unit tests.
 *
 * Focus: the LRU semantics + storage round-trip + corrupt-data tolerance.
 * jsdom ships a working localStorage so we don't need a mock; we just reset
 * it between tests. We assert on the hook's return value (not the storage
 * blob) so the test survives a future storage-key rename.
 */

const STORAGE_KEY = "bb_recent_entities"

function hit(over: Partial<RecentEntity>): RecentEntity {
  return { type: "project", title: "P", subtitle: null, link: "/projects/1", ...over }
}

describe("useRecentEntities", () => {
  beforeEach(() => {
    window.localStorage.clear()
  })

  it("starts empty when storage is empty", () => {
    const { result } = renderHook(() => useRecentEntities())
    expect(result.current.recent).toEqual([])
  })

  it("record() prepends and persists across remounts", () => {
    const { result, unmount } = renderHook(() => useRecentEntities())
    act(() => { result.current.record(hit({ link: "/projects/1", title: "Alpha" })) })
    expect(result.current.recent[0].title).toBe("Alpha")
    unmount()

    // Fresh mount must read the same list from storage.
    const { result: r2 } = renderHook(() => useRecentEntities())
    expect(r2.current.recent.map((e) => e.title)).toEqual(["Alpha"])
  })

  it("dedupes by link — re-recording moves an entry to the front, doesn't duplicate", () => {
    const { result } = renderHook(() => useRecentEntities())
    act(() => {
      result.current.record(hit({ link: "/projects/1", title: "Alpha" }))
      result.current.record(hit({ link: "/projects/2", title: "Beta" }))
      result.current.record(hit({ link: "/projects/1", title: "Alpha" }))
    })
    // Alpha returns to the front, Beta drops to position 2, length stays 2.
    expect(result.current.recent.map((e) => e.link)).toEqual(["/projects/1", "/projects/2"])
  })

  it("caps the list at 5 entries (oldest dropped)", () => {
    const { result } = renderHook(() => useRecentEntities())
    act(() => {
      for (let i = 1; i <= 7; i++)
        result.current.record(hit({ link: `/projects/${i}`, title: `P${i}` }))
    })
    // Last 5 written, most-recent-first → P7…P3.
    expect(result.current.recent.map((e) => e.title)).toEqual(["P7", "P6", "P5", "P4", "P3"])
  })

  it("clear() empties the list and persists the empty state", () => {
    const { result } = renderHook(() => useRecentEntities())
    act(() => {
      result.current.record(hit({ link: "/projects/1", title: "Alpha" }))
      result.current.clear()
    })
    expect(result.current.recent).toEqual([])
    // Fresh mount still sees the cleared state.
    const { result: r2 } = renderHook(() => useRecentEntities())
    expect(r2.current.recent).toEqual([])
  })

  it("tolerates corrupt JSON in storage (returns [] instead of throwing)", () => {
    window.localStorage.setItem(STORAGE_KEY, "{not json")
    const { result } = renderHook(() => useRecentEntities())
    expect(result.current.recent).toEqual([])
  })

  it("drops entries that don't match the expected shape", () => {
    // Mix one valid entry with two invalid ones; the hook must keep only the valid.
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify([
      { title: "OK", subtitle: null, link: "/projects/1", type: "project" },
      { title: "missing link", type: "project" },           // no .link
      { title: 42, subtitle: null, link: "/x", type: "project" }, // wrong .title type
    ]))
    const { result } = renderHook(() => useRecentEntities())
    expect(result.current.recent).toEqual([
      { title: "OK", subtitle: null, link: "/projects/1", type: "project" },
    ])
  })

  it("ignores incomplete records (missing type/link/title)", () => {
    const { result } = renderHook(() => useRecentEntities())
    act(() => {
      // Caller forgot to label the type — must be a no-op, not a poison entry.
      result.current.record({ type: "", title: "x", subtitle: null, link: "/y" })
      result.current.record({ type: "project", title: "", subtitle: null, link: "/y" })
      result.current.record({ type: "project", title: "ok", subtitle: null, link: "" })
    })
    expect(result.current.recent).toEqual([])
  })

  it("broadcasts to every mounted instance in the same tab", () => {
    const { result: a } = renderHook(() => useRecentEntities())
    const { result: b } = renderHook(() => useRecentEntities())
    act(() => {
      a.current.record(hit({ link: "/projects/9", title: "Nine" }))
    })
    // Instance B never called record() itself, but the broadcast should still
    // push the new list into its state — without this fan-out, the header
    // palette and any future mobile sheet would diverge after a click.
    expect(b.current.recent[0].link).toBe("/projects/9")
  })
})
