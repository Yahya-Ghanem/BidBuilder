"use client"

import { useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { TrendingUp } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { RateTrendDto } from "@/lib/types"

/**
 * 23.4 — Tiny inline rate-trend visualizer for a resource row.
 *
 * Renders a single trigger icon. On click, fetches the 24-month trend lazily
 * (caches via TanStack Query so subsequent toggles are free) and pops a small
 * SVG sparkline + a volatility badge (low / medium / high). Lazy on purpose:
 * a tenant with 200 resources shouldn't fire 200 trend requests on page load.
 */
type ApiType = "Labor" | "Material" | "Equipment" | "Subcontractor"

const TYPE_TO_PATH: Record<ApiType, string> = {
  Labor: "labor",
  Material: "materials",
  Equipment: "equipment",
  Subcontractor: "subcontractors",
}

export function RateTrendCell({ apiType, id }: { apiType: ApiType; id: number }) {
  const [open, setOpen] = useState(false)
  const path = TYPE_TO_PATH[apiType]
  const { data, isLoading } = useQuery({
    queryKey: ["rate-trend", apiType, id],
    queryFn: () => fetchApi<RateTrendDto>(`/api/resources/${path}/${id}/rate-trend`),
    enabled: open,
    staleTime: 60_000,
  })

  return (
    <span className="relative inline-flex items-center">
      <button
        type="button"
        title="Show rate trend"
        onClick={(e) => { e.stopPropagation(); setOpen((v) => !v) }}
        className="ms-1 rounded p-0.5 text-muted hover:bg-slate-100 hover:text-[var(--brand)]"
      >
        <TrendingUp className="h-3.5 w-3.5" />
      </button>
      {open && (
        <div className="absolute end-0 top-full z-10 mt-1 flex flex-col items-end gap-1 rounded-md border border-[var(--border)] bg-white p-2 shadow-md">
          {isLoading || !data ? (
            <span className="text-xs text-muted">Loading…</span>
          ) : data.points.length < 2 ? (
            <span className="text-xs text-muted">No history yet</span>
          ) : (
            <>
              <Sparkline points={data.points.map((p) => p.rate)} />
              <Summary trend={data} />
            </>
          )}
        </div>
      )}
    </span>
  )
}

/** Renders a 24-point sparkline as a single SVG <path>. Width 140px × height 28px. */
function Sparkline({ points }: { points: number[] }) {
  const w = 140, h = 28, padX = 1, padY = 2
  const min = Math.min(...points)
  const max = Math.max(...points)
  const span = max === min ? 1 : max - min   // avoid /0 for a flat series
  const stepX = (w - padX * 2) / Math.max(1, points.length - 1)
  const d = points
    .map((v, i) => {
      const x = padX + i * stepX
      const y = padY + (1 - (v - min) / span) * (h - padY * 2)
      return `${i === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`
    })
    .join(" ")
  return (
    <svg width={w} height={h} viewBox={`0 0 ${w} ${h}`} className="block">
      <path d={d} fill="none" stroke="currentColor" strokeWidth={1.5} className="text-[var(--brand)]" />
    </svg>
  )
}

/** "low / medium / high" volatility plus 12m min/max numbers, all in tiny tabular text. */
function Summary({ trend }: { trend: RateTrendDto }) {
  const v = trend.volatilityIndex ?? 0
  const label = v < 0.05 ? "low" : v < 0.2 ? "medium" : "high"
  const tone =
    label === "low"    ? "bg-emerald-100 text-emerald-700"
    : label === "medium" ? "bg-amber-100 text-amber-700"
    : "bg-rose-100 text-rose-700"

  // Compact numeric formatter (no currency on a per-row sparkline; the row already
  // shows the formatted live rate beside it).
  const fmt = (n: number | null) => n == null ? "—" : n.toLocaleString(undefined, { maximumFractionDigits: 2 })

  return (
    <div className="flex items-center gap-1 text-[10px] tabular-nums text-slate-500">
      <span className={`rounded px-1 py-0.5 font-medium uppercase ${tone}`}>{label}</span>
      <span>12m {fmt(trend.min12m)}–{fmt(trend.max12m)}</span>
    </div>
  )
}
