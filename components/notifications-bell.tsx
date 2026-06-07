"use client"

import { useEffect, useRef, useState } from "react"
import { useRouter } from "next/navigation"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Bell, CheckCheck } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { NotificationItem, NotificationList } from "@/lib/types"

/** Compact relative time ("just now", "5m", "3h", "2d", else a date). */
function ago(iso: string) {
  const then = new Date(iso).getTime()
  const s = Math.max(0, Math.floor((Date.now() - then) / 1000))
  if (s < 60) return "just now"
  if (s < 3600) return `${Math.floor(s / 60)}m ago`
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`
  if (s < 604800) return `${Math.floor(s / 86400)}d ago`
  return new Date(iso).toLocaleDateString()
}

/**
 * 20.3 — Notifications bell for the top bar. Polls the unread count every 30s
 * for the badge; opening the dropdown fetches the recent list. Clicking an item
 * marks it read and follows its link; "Mark all read" clears the badge.
 */
export function NotificationsBell() {
  const qc = useQueryClient()
  const router = useRouter()
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  // Lightweight poll for just the count (drives the badge even while closed).
  const count = useQuery({
    queryKey: ["notif-unread"],
    queryFn: () => fetchApi<{ count: number }>("/api/notifications/unread-count"),
    refetchInterval: 30_000,
    staleTime: 0,
  })
  const unread = count.data?.count ?? 0

  // Full list — only fetched while the dropdown is open.
  const list = useQuery({
    queryKey: ["notif-list"],
    queryFn: () => fetchApi<NotificationList>("/api/notifications?take=30"),
    enabled: open,
    staleTime: 0,
  })

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ["notif-unread"] })
    qc.invalidateQueries({ queryKey: ["notif-list"] })
  }
  const markRead = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/notifications/${id}/read`, { method: "POST" }),
    onSuccess: refresh,
  })
  const markAll = useMutation({
    mutationFn: () => fetchApi("/api/notifications/read-all", { method: "POST" }),
    onSuccess: refresh,
  })

  // Close on outside-click / Escape.
  useEffect(() => {
    if (!open) return
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false)
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false)
    }
    document.addEventListener("mousedown", onDown)
    document.addEventListener("keydown", onKey)
    return () => {
      document.removeEventListener("mousedown", onDown)
      document.removeEventListener("keydown", onKey)
    }
  }, [open])

  function openItem(n: NotificationItem) {
    if (!n.isRead) markRead.mutate(n.id)
    setOpen(false)
    if (n.link) router.push(n.link)
  }

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen((o) => !o)}
        aria-label={`Notifications${unread > 0 ? ` (${unread} unread)` : ""}`}
        className="relative rounded p-2 text-slate-600 hover:bg-slate-100"
      >
        <Bell className="h-5 w-5" />
        {unread > 0 && (
          <span className="absolute -right-0.5 -top-0.5 grid min-w-[18px] place-items-center rounded-full bg-rose-600 px-1 text-[10px] font-bold leading-[18px] text-white">
            {unread > 99 ? "99+" : unread}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute right-0 z-50 mt-2 w-[22rem] max-w-[calc(100vw-2rem)] overflow-hidden rounded-lg border border-[var(--border)] bg-white shadow-lg">
          <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2.5">
            <span className="text-sm font-semibold text-slate-700">Notifications</span>
            {unread > 0 && (
              <button onClick={() => markAll.mutate()} disabled={markAll.isPending}
                className="flex items-center gap-1 text-xs text-[var(--brand)] hover:underline">
                <CheckCheck className="h-3.5 w-3.5" /> Mark all read
              </button>
            )}
          </div>

          <div className="max-h-[24rem] overflow-auto">
            {list.isLoading ? (
              <p className="px-4 py-6 text-center text-sm text-muted">Loading…</p>
            ) : !list.data?.items.length ? (
              <p className="px-4 py-6 text-center text-sm text-muted">You&apos;re all caught up.</p>
            ) : (
              <ul>
                {list.data.items.map((n) => (
                  <li key={n.id}>
                    <button onClick={() => openItem(n)}
                      className={`flex w-full flex-col gap-0.5 border-b border-[var(--border)] px-4 py-2.5 text-left transition hover:bg-slate-50 ${n.isRead ? "" : "bg-[var(--brand)]/5"}`}>
                      <span className="flex items-center gap-2">
                        {!n.isRead && <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-[var(--brand)]" />}
                        <span className={`text-sm ${n.isRead ? "text-slate-600" : "font-semibold text-slate-800"}`}>{n.title}</span>
                      </span>
                      {n.body && <span className="line-clamp-2 text-xs text-slate-500">{n.body}</span>}
                      <span className="text-[11px] text-muted">{ago(n.createdAt)}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
