"use client"

import { useCallback, useMemo } from "react"
import { useQuery } from "@tanstack/react-query"
import { fetchApi } from "@/lib/api"
import { useAuth } from "@/lib/auth"
import type { MePermissions, ModulePermission } from "@/lib/types"

export type ModuleAction = "view" | "add" | "edit" | "delete"

/**
 * Fetches the caller's effective module permissions once (cached) and exposes a
 * `can(moduleCode, action)` predicate. Admins are always allowed. This only hides
 * UI affordances — the API still enforces every mutation server-side.
 */
export function usePermissions() {
  const { isAuthenticated } = useAuth()
  const q = useQuery({
    queryKey: ["permissions"],
    queryFn: () => fetchApi<MePermissions>("/api/auth/permissions"),
    enabled: isAuthenticated,
    staleTime: 5 * 60_000,
  })

  const map = useMemo(() => {
    const m: Record<string, ModulePermission> = {}
    q.data?.modules.forEach((p) => { m[p.code] = p })
    return m
  }, [q.data])

  const isAdmin = q.data?.isAdmin ?? false

  const can = useCallback(
    (code: string, action: ModuleAction = "view") => {
      if (isAdmin) return true
      const p = map[code]
      if (!p) return false
      switch (action) {
        case "view": return p.canView
        case "add": return p.canAdd
        case "edit": return p.canEdit
        case "delete": return p.canDelete
      }
    },
    [map, isAdmin],
  )

  return { can, isAdmin, isLoading: q.isLoading, modules: q.data?.modules ?? [] }
}
