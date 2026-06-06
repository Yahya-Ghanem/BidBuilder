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
import { useEffect, useRef, useState } from "react"
import { ChevronDown, ChevronRight, Trash2 } from "lucide-react"
import type { QueryClient } from "@tanstack/react-query"
import type { EstimateBreakdown } from "@/lib/types"
import { Card, Button } from "@/components/ui"

/** Headline number card (Direct cost, Bid price, etc.). */
export function Stat({ label, value, highlight }: { label: string; value: string; highlight?: boolean }) {
  return (
    <Card className={highlight ? "p-4 ring-2 ring-[var(--brand)]" : "p-4"}>
      <div className="text-xs text-slate-500">{label}</div>
      <div className={highlight ? "text-lg font-bold text-[var(--brand)]" : "text-lg font-semibold"}>{value}</div>
    </Card>
  )
}

/** Two-column row inside the Prelims / Markups cards. */
export function Row({ left, right, onDelete }: { left: string; right: string; onDelete?: () => void }) {
  return (
    <div className="flex items-center justify-between border-t border-[var(--border)] py-1.5 text-sm first:border-0">
      <span className="text-slate-600">{left}</span>
      <div className="flex items-center gap-2">
        <span className="font-medium">{right}</span>
        {onDelete && <button onClick={onDelete} className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3 w-3" /></button>}
      </div>
    </div>
  )
}

/** Chevron that expands/collapses a tree node; renders a fixed-width spacer when
 *  the node has no children so labels stay aligned. */
export function CollapseToggle({ open, hasChildren, onToggle }: { open: boolean; hasChildren: boolean; onToggle: () => void }) {
  if (!hasChildren) return <span className="inline-block w-[18px]" />
  return (
    <button onClick={onToggle} className="rounded p-0.5 text-slate-400 hover:text-slate-700" title={open ? "Collapse" : "Expand"}>
      {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
    </button>
  )
}

/** Expand-all / Collapse-all controls for a tree. */
export function ExpandCollapseAll({ onExpand, onCollapse }: { onExpand: () => void; onCollapse: () => void }) {
  return (
    <div className="flex gap-1">
      <Button variant="ghost" className="h-7 px-2 text-xs" onClick={onExpand}>Expand all</Button>
      <Button variant="ghost" className="h-7 px-2 text-xs" onClick={onCollapse}>Collapse all</Button>
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
