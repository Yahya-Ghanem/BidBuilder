"use client"

import { useEffect, useState } from "react"
import { fetchApi } from "@/lib/api"
import type { PresenceList, PresenceUser } from "@/lib/types"

/** 27.2 — heartbeat cadence in ms. Server-side TTL is 90 s, so a single
 *  skipped beat keeps us in the live list. Aligned with TanStack Query
 *  defaults (we POST every 30 s, refetch on its response — no separate GET
 *  poll). */
export const PRESENCE_HEARTBEAT_MS = 30_000

/**
 * 27.2 — Heartbeat the presence endpoint every 30 s and return the current
 * live viewer list as it comes back from the server. The list intentionally
 * INCLUDES the caller (server semantics) — the avatar cluster filters self
 * client-side so both halves of the same browser session agree on identity.
 *
 * Returns `[]` until the first POST resolves; null/undefined `estimateId`
 * is a no-op (used when the editor hasn't picked a revision yet). The hook
 * shuts down on unmount or id change — no leaked interval timers, no stale
 * fetch loops across the editor remounting on revision switch.
 *
 * Failure handling: a 4xx/5xx never throws out of the hook. Presence is a
 * soft cue; a transient blip should leave the previously-known list intact
 * rather than emptying the cluster. Errors are silently swallowed (no toast,
 * no error state) — the next tick retries.
 */
export function usePresence(estimateId: number | null | undefined): { users: PresenceUser[] } {
  const [users, setUsers] = useState<PresenceUser[]>([])

  useEffect(() => {
    if (estimateId == null) { setUsers([]); return }
    let cancelled = false

    const beat = async () => {
      try {
        const r = await fetchApi<PresenceList>(`/api/estimates/${estimateId}/presence`, { method: "POST" })
        if (!cancelled) setUsers(r.users ?? [])
      } catch {
        // Soft failure: keep the previously-known list — transient blips should
        // not empty the cluster. Next tick retries.
      }
    }

    void beat()
    const handle = window.setInterval(beat, PRESENCE_HEARTBEAT_MS)
    return () => { cancelled = true; window.clearInterval(handle) }
  }, [estimateId])

  return { users }
}
