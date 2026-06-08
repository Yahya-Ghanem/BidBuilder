"use client"

import { useEffect, useRef, useState, type ReactNode } from "react"
import { useRouter } from "next/navigation"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Bell, CheckCheck } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { useT } from "@/lib/i18n"
import type { NotificationList } from "@/lib/types"
import { groupNotifications, partitionGroups, type NotificationGroup } from "@/lib/notifications"

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

function SectionHeader({ children }: { children: ReactNode }) {
  return (
    <li className="bg-slate-50 px-4 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-slate-500">
      {children}
    </li>
  )
}

/** A single (possibly collapsed) notification group row. */
function GroupRow({ g, onOpen }: { g: NotificationGroup; onOpen: (g: NotificationGroup) => void }) {
  const unread = g.unreadCount > 0
  return (
    <li>
      <button
        onClick={() => onOpen(g)}
        className={`flex w-full flex-col gap-0.5 border-b border-[var(--border)] px-4 py-2.5 text-left transition hover:bg-slate-50 ${unread ? "bg-[var(--brand)]/5" : ""}`}
      >
        <span className="flex items-center gap-2">
          {unread && <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-[var(--brand)]" aria-hidden />}
          <span className={`text-sm ${unread ? "font-semibold text-slate-800" : "text-slate-600"}`}>{g.latest.title}</span>
          {g.count > 1 && (
            <span className="ms-auto shrink-0 rounded-full bg-slate-100 px-1.5 text-[11px] font-medium text-slate-600">×{g.count}</span>
          )}
        </span>
        {g.latest.body && <span className="line-clamp-2 text-xs text-slate-500">{g.latest.body}</span>}
        <span className="text-[11px] text-muted">{ago(g.latest.createdAt)}</span>
      </button>
    </li>
  )
}

/**
 * 20.3 / 27.4 — Notifications bell for the top bar. Polls the unread count every 30s
 * for the badge; opening the dropdown fetches the recent list. 27.4: items are collapsed
 * by entity (5 publish events on one project → one row with a count) and split into an
 * "Unread" section above an "Earlier" section. Opening a row marks its members read and
 * follows the newest member's link; "Mark all read" clears the badge.
 */
export function NotificationsBell() {
  const t = useT()
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

  // Open a (possibly collapsed) group: mark every unread member read, then follow the
  // newest member's link. Marks are best-effort and fire in parallel.
  function openGroup(g: NotificationGroup) {
    setOpen(false)
    const unreadIds = g.items.filter((x) => !x.isRead).map((x) => x.id)
    if (unreadIds.length) {
      Promise.all(unreadIds.map((id) =>
        fetchApi(`/api/notifications/${id}/read`, { method: "POST" }).catch(() => {})
      )).then(refresh)
    }
    if (g.latest.link) router.push(g.latest.link)
  }

  const groups = groupNotifications(list.data?.items ?? [])
  const { unread: unreadGroups, earlier: earlierGroups } = partitionGroups(groups)

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen((o) => !o)}
        aria-label={`${t("notif.title")}${unread > 0 ? ` (${unread})` : ""}`}
        className="relative rounded p-2 text-slate-600 hover:bg-slate-100"
      >
        <Bell className="h-5 w-5" />
        {unread > 0 && (
          <span className="absolute -end-0.5 -top-0.5 grid min-w-[18px] place-items-center rounded-full bg-rose-600 px-1 text-[10px] font-bold leading-[18px] text-white">
            {unread > 99 ? "99+" : unread}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute end-0 z-50 mt-2 w-[22rem] max-w-[calc(100vw-2rem)] overflow-hidden rounded-lg border border-[var(--border)] bg-white shadow-lg">
          <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2.5">
            <span className="text-sm font-semibold text-slate-700">{t("notif.title")}</span>
            {unread > 0 && (
              <button onClick={() => markAll.mutate()} disabled={markAll.isPending}
                className="flex items-center gap-1 text-xs text-[var(--brand)] hover:underline">
                <CheckCheck className="h-3.5 w-3.5" /> {t("notif.markAll")}
              </button>
            )}
          </div>

          <div className="max-h-[24rem] overflow-auto">
            {list.isLoading ? (
              <p className="px-4 py-6 text-center text-sm text-muted">{t("common.loading")}</p>
            ) : !groups.length ? (
              <p className="px-4 py-6 text-center text-sm text-muted">{t("notif.caughtUp")}</p>
            ) : (
              <ul>
                {unreadGroups.length > 0 && <SectionHeader>{t("notif.unread")} ({unread})</SectionHeader>}
                {unreadGroups.map((g) => <GroupRow key={g.key} g={g} onOpen={openGroup} />)}
                {earlierGroups.length > 0 && <SectionHeader>{t("notif.earlier")}</SectionHeader>}
                {earlierGroups.map((g) => <GroupRow key={g.key} g={g} onOpen={openGroup} />)}
              </ul>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
