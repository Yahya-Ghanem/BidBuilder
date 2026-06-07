"use client"
/**
 * Shared bits used across the project-detail components (19.6 split).
 *
 * Lives in `_components/` so Next.js treats it as a private module — the leading
 * underscore stops the file from being picked up as a route segment.
 *
 * The page used to be a single ~1800-line file. Extracting these primitives
 * (Stat, Row, the tree-collapse hook, the BOQ item-kinds enum, the optimistic-
 * concurrency If-Match header) lets every sibling component live in its own
 * focused file without duplicating helpers.
 */
import { useEffect, useRef, useState, type ReactNode } from "react"
import { ChevronDown, ChevronRight, Trash2, TrendingDown, TrendingUp, Minus } from "lucide-react"
import type { QueryClient } from "@tanstack/react-query"
import type { EstimateBreakdown } from "@/lib/types"
import { Card, Button } from "@/components/ui"
import { cn } from "@/lib/utils"

/** Headline number card (Direct cost, Bid price, etc.). The optional
 *  `delta` slot is rendered under the value — 25.5 wires a <DeltaBadge>
 *  there to show "↑ 4.2% vs Rev 1" trend deltas against the prior revision,
 *  or a <NewPill> when this card had no comparable prior value. */
export function Stat({ label, value, highlight, delta }: { label: string; value: string; highlight?: boolean; delta?: ReactNode }) {
  return (
    <Card className={highlight ? "p-4 ring-2 ring-[var(--brand)]" : "p-4"}>
      <div className="text-xs text-slate-500">{label}</div>
      <div className={highlight ? "text-lg font-bold text-[var(--brand)]" : "text-lg font-semibold"}>{value}</div>
      {delta}
    </Card>
  )
}

// 25.5 — Numbers above this cap on the trend badge get clamped to ">N%" so a
// runaway prior revision (e.g. a sub-cent base scaled up 1000× by a real
// estimate) doesn't render as a 9-digit percent that wraps the card.
const DELTA_CLAMP = 999

/** 25.5 — Trend badge against a prior revision.
 *
 *  Renders `↑ 4.2% vs Rev 1` with a color that reflects whether the movement
 *  is "good for the user". The CALLER decides which direction is better:
 *  cost-style cards (direct / indirect) lower-is-better; revenue-style cards
 *  (bid / bid incl. tax / markups — markups ARE the contractor's margin
 *  envelope and the same money that lands in the bid) higher-is-better.
 *
 *  The ±0.05% noise band reads as "no meaningful change" and paints neutral
 *  so estimators don't chase rounding noise. Inside the band the sign
 *  character is stripped (no "+0.0%" / "-0.0%" artifacts) and the icon flips
 *  to Minus for a consistent grey-dash-zero look.
 *
 *  Pct may be null when |previous| was effectively zero (the backend's
 *  sub-cent guard) — that's the "card had no comparable prior value" case.
 *  We render NOTHING here for null; the caller (StatCards) shows a separate
 *  <NewPill> in that slot so the user can tell the case apart from "this is
 *  revision 1".
 */
export function DeltaBadge({
  pct, lowerIsBetter, vsLabel, ariaLabel,
}: {
  pct: number | null
  /** True for cost cards (less is better). False for bid + markups cards (more is better). */
  lowerIsBetter: boolean
  /** What goes after "vs", e.g. "Rev 1" or "Rev 2". */
  vsLabel: string
  /** Optional accessible label override; default reads the rendered text. */
  ariaLabel?: string
}) {
  if (pct == null) return null
  const noise = Math.abs(pct) < 0.05
  // If pct < 0 (lower) AND lowerIsBetter ⇒ good. If pct > 0 (higher) AND
  // !lowerIsBetter ⇒ also good. (Inside the noise band the tone forces
  // neutral, so this only affects directional badges.)
  const better = pct < 0 ? lowerIsBetter : !lowerIsBetter
  const tone = noise
    ? "text-muted"
    : better
      ? "text-success"
      : "text-danger"
  const Icon = noise ? Minus : pct > 0 ? TrendingUp : TrendingDown
  const clamped = Math.min(Math.abs(pct), DELTA_CLAMP)
  const overCap = Math.abs(pct) > DELTA_CLAMP
  // Sign: stripped inside the noise band so a "+0.0%" doesn't shout "uplift"
  // and a "-0.0%" doesn't shout "drop" when the tone says "no change".
  const sign = noise ? "" : pct > 0 ? "+" : "−"
  // ">" prefix when over the cap so the user sees the value was huge without
  // committing the card to a 9-digit string they have to mentally parse.
  const formatted = `${overCap ? ">" : ""}${sign}${clamped.toFixed(1)}%`
  return (
    <div
      role="status"
      aria-label={ariaLabel ?? `${formatted} vs ${vsLabel}`}
      className={cn("mt-1 inline-flex items-center gap-1 text-xs font-medium", tone)}
    >
      <Icon className="h-3 w-3" aria-hidden />
      <span>{formatted}</span>
      <span className="text-muted">vs {vsLabel}</span>
    </div>
  )
}

/** 25.5 — "NEW" pill rendered under a stat card whose prior revision had no
 *  comparable value (server-side: |previous| < 0.005). Differentiates the
 *  case "card first appeared on this revision" from "no prior revision at
 *  all" (the latter renders no pill — the entire stat row has no delta slot
 *  populated). Says NEW to non-technical estimators rather than "∞%". */
export function NewPill({ vsLabel }: { vsLabel: string }) {
  return (
    <div
      role="status"
      aria-label={`New since ${vsLabel}`}
      className="mt-1 inline-flex items-center gap-1 text-xs font-medium text-info"
    >
      <span className="rounded bg-info-soft px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide">New</span>
      <span className="text-muted">vs {vsLabel}</span>
    </div>
  )
}

/** Two-column row inside the Prelims / Markups cards. */
export function Row({ left, right, onDelete }: { left: string; right: string; onDelete?: () => void }) {
  return (
    <div className="flex items-center justify-between border-t border-[var(--border)] py-1.5 text-sm first:border-0">
      <span className="text-slate-600">{left}</span>
      <div className="flex items-center gap-2">
        <span className="font-medium">{right}</span>
        {onDelete && <button onClick={onDelete} className="rounded p-1 text-muted hover:bg-danger-soft hover:text-danger"><Trash2 className="h-3 w-3" /></button>}
      </div>
    </div>
  )
}

/** Chevron that expands/collapses a tree node; renders a fixed-width spacer when
 *  the node has no children so labels stay aligned. */
export function CollapseToggle({ open, hasChildren, onToggle }: { open: boolean; hasChildren: boolean; onToggle: () => void }) {
  if (!hasChildren) return <span className="inline-block w-[18px]" />
  return (
    <button onClick={onToggle} className="rounded p-0.5 text-muted hover:text-slate-700" title={open ? "Collapse" : "Expand"}>
      {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
    </button>
  )
}

/** Expand-all / Collapse-all controls for a tree. */
export function ExpandCollapseAll({ onExpand, onCollapse }: { onExpand: () => void; onCollapse: () => void }) {
  return (
    <div className="flex gap-1">
      <Button variant="ghost" className="h-7 px-2 text-sm" onClick={onExpand}>Expand all</Button>
      <Button variant="ghost" className="h-7 px-2 text-sm" onClick={onCollapse}>Collapse all</Button>
    </div>
  )
}

/** Hook: a set of collapsed node ids + toggle/expand-all/collapse-all helpers.
 *  Pass startCollapsed to begin fully collapsed once `collapsibleIds` first
 *  arrive (data loads async) — keeps large trees light on initial render. */
export function useCollapse(collapsibleIds: Iterable<number> = [], startCollapsed = false) {
  const [collapsed, setCollapsed] = useState<Set<number>>(new Set())
  const seeded = useRef(false)
  useEffect(() => {
    if (!startCollapsed || seeded.current) return
    const arr = Array.from(collapsibleIds)
    if (arr.length === 0) return
    seeded.current = true
    setCollapsed(new Set(arr))
  })
  const toggle = (id: number) => setCollapsed((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n })
  const isOpen = (id: number) => !collapsed.has(id)
  const collapseAll = (ids: Iterable<number>) => setCollapsed(new Set(ids))
  const expandAll = () => setCollapsed(new Set())
  return { toggle, isOpen, collapseAll, expandAll }
}

/** Optimistic-concurrency If-Match header from the last cached estimate breakdown.
 *  The API uses xmin as the row version; sending it back lets the server return
 *  409 on a concurrent edit instead of silently clobbering. */
export function ifMatchHeaders(qc: QueryClient, estimateId: number): HeadersInit {
  const cached = qc.getQueryData<EstimateBreakdown>(["estimate", estimateId])
  return cached?.rowVersion ? { "If-Match": cached.rowVersion } : {}
}

/** A cost-component line as sent to the API: Amount types as quantity × rate
 *  (material qty×price, manpower hours×rate); Percent types as a value (%). */
export type CompInput = { typeId: number; value?: number; quantity?: number; rate?: number }

/** BOQ line-kind enum (Normal, ProvisionalSum, PcSum, Daywork, Alternate). */
export const ITEM_KINDS = [
  ["Normal", "Normal"],
  ["ProvisionalSum", "Provisional Sum"],
  ["PcSum", "PC Sum"],
  ["Daywork", "Daywork"],
  ["Alternate", "Alternate"],
] as const
export const kindLabel = (k: string) => ITEM_KINDS.find(([v]) => v === k)?.[1] ?? k
