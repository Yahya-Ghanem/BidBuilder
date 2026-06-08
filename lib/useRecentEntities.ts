"use client"

import { useCallback, useEffect, useState } from "react"
import type { SearchHit } from "@/lib/types"

/**
 * 28.5 — Recently-visited entities for the search palette.
 *
 * The palette opens with an empty query for ~80% of uses ("where's that
 * project I was on yesterday?"). Surfacing the last 5 things the user
 * navigated to makes that path one click instead of typing five characters
 * and waiting for /api/search.
 *
 * State lives in localStorage (LRU of 5, keyed by link) so it survives a
 * page reload but stays per-browser — there's no privacy/multi-device need
 * to round-trip this through the API. A `type` field on each entry rides
 * along so the palette can render the same icon as a search hit.
 */

/** A recent hit is a SearchHit + a coarse type tag for the icon. */
export interface RecentEntity extends SearchHit {
  /** Mirrors SearchGroup.type — "project" | "estimate" | "resource" | "assembly". */
  type: string
}

const STORAGE_KEY = "bb_recent_entities"
const CAP = 5

/**
 * Module-level subscriber set so multiple mounts of the palette stay in sync
 * within the same tab (the header palette and a future mobile sheet would
 * otherwise diverge after a record() call). `useState` only re-renders the
 * component that owns the state; this fan-out makes the record visible to
 * every hook mounted at the time.
 */
const listeners = new Set<(v: RecentEntity[]) => void>()
function broadcast(v: RecentEntity[]) {
  listeners.forEach((cb) => cb(v))
}

/** Defensively parse the stored JSON — corrupt storage shouldn't crash boot. */
function read(): RecentEntity[] {
  if (typeof window === "undefined") return [] // SSR safety
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    // Validate shape — anything missing the required fields gets dropped, so a
    // future migration that adds a field doesn't poison the runtime. We keep
    // the original `subtitle` if it's null (a valid SearchHit value).
    return parsed.filter((x): x is RecentEntity =>
      x != null && typeof x === "object"
      && typeof x.title === "string"
      && typeof x.link === "string"
      && typeof x.type === "string"
      && (x.subtitle === null || typeof x.subtitle === "string"),
    ).slice(0, CAP)
  } catch {
    return []
  }
}

function write(v: RecentEntity[]) {
  if (typeof window === "undefined") return
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(v))
  } catch {
    // Quota exceeded or storage disabled — the LRU is best-effort.
  }
}

/**
 * Hook returning the current recent list + writers. `record(entity)` moves
 * the entity to the front of the list and trims to CAP=5; `clear()` empties
 * the list. Both go through the broadcast channel so every palette instance
 * updates in lockstep.
 */
export function useRecentEntities() {
  // `read` is cheap (one localStorage hit + JSON.parse), but doing it
  // lazily via the useState initializer means SSR renders with an empty
  // list and the first client paint hydrates without a flicker.
  const [recent, setRecent] = useState<RecentEntity[]>(() => read())

  useEffect(() => {
    listeners.add(setRecent)
    return () => { listeners.delete(setRecent) }
  }, [])

  const record = useCallback((entity: RecentEntity) => {
    // Skip if the call site forgot to label it — better to drop than to
    // store an entry that won't render an icon.
    if (!entity.type || !entity.link || !entity.title) return
    const cur = read()
    // LRU: filter out the same link, prepend the new one, cap.
    const next = [entity, ...cur.filter((x) => x.link !== entity.link)].slice(0, CAP)
    write(next)
    broadcast(next)
  }, [])

  const clear = useCallback(() => {
    write([])
    broadcast([])
  }, [])

  return { recent, record, clear }
}
