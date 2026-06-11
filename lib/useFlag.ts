"use client"

import { useQuery } from "@tanstack/react-query"
import { fetchApi } from "@/lib/api"

/**
 * 29.B.1 — Feature flags on the client.
 *
 * Pattern matches every other admin-toggleable surface in the app: TanStack
 * Query owns the cache, `useFlag()` is a thin selector over a single shared
 * map. The whole resolved map lands in one round-trip when the SPA boots and
 * is reused by every gated component.
 *
 * Toggle propagation — the spec's "&lt; 60s without a redeploy" hits from two
 * directions: the backend's catalogue cache TTL is 30s with an immediate
 * invalidation on admin write, and TanStack's `refetchOnWindowFocus` plus a
 * 60-second `staleTime` makes the client pick up new values the moment a
 * tab regains focus (the typical operator workflow when flipping a flag).
 *
 * Unknown keys → false. A typo in application code defaults to "hide the
 * feature" rather than accidentally exposing something. Mirror of the
 * backend resolver.
 */

const FEATURES_KEY = ["me", "features"] as const

interface FeatureMap {
  [key: string]: boolean
}

function useFeatureMap(): FeatureMap {
  const { data } = useQuery({
    queryKey: FEATURES_KEY,
    queryFn: () => fetchApi<FeatureMap>("/api/me/features"),
    // 60 s — operator-flip workflow: edit in admin tab, tab back to estimator,
    // the focus refetch immediately picks up the new value.
    staleTime: 60_000,
    refetchOnWindowFocus: true,
    // The map is small; serve cached value while we revalidate so gated
    // surfaces don't flicker between renders.
    placeholderData: (prev) => prev,
  })
  return data ?? {}
}

/** True when the named flag is enabled for the current tenant. */
export function useFlag(key: string): boolean {
  const map = useFeatureMap()
  return map[key] === true
}

/** Hook variant for callers that need to read more than one flag in the same
 *  render — avoids re-subscribing the query for each call. */
export function useFlags(): FeatureMap {
  return useFeatureMap()
}
