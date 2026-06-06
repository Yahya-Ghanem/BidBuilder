"use client"
import { useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { FileSpreadsheet, FileText, FolderTree, Table } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { AreaRollup, AreaRollupRow } from "@/lib/types"
import { Card, Button } from "@/components/ui"
import { money } from "@/lib/utils"
import { CollapseToggle, ExpandCollapseAll, useCollapse } from "./shared"

/** Per-estimate cost roll-up: item line totals escalated up the area tree. */
export function AreaRollupPanel({ estimateId, currency, canExport, onExport }: { estimateId: number; currency: string; canExport: boolean; onExport: (kind: "xlsx" | "csv" | "pdf", level: "detail" | "area" | "subarea" | "unit") => void }) {
  const { data } = useQuery({ queryKey: ["areas-rollup", estimateId], queryFn: () => fetchApi<AreaRollup>(`/api/estimates/${estimateId}/areas-rollup`) })
  // Export grouping level (Detail = full tree; Area / Sub-Area / Unit = rolled-up summary).
  const [level, setLevel] = useState<"detail" | "area" | "subarea" | "unit">("detail")
  const seedIds = data ? data.areas.filter((a) => data.areas.some((x) => x.parentAreaId === a.id)).map((a) => a.id) : []
  const { toggle, isOpen, collapseAll, expandAll } = useCollapse(seedIds, true)
  if (!data || data.areas.length === 0) return null
  const childrenOf = (id: number | null) => data.areas.filter((a) => a.parentAreaId === id)
  const parentIds = new Set(data.areas.filter((a) => childrenOf(a.id).length > 0).map((a) => a.id))
  function RollupRow({ a, depth }: { a: AreaRollupRow; depth: number }) {
    const kids = childrenOf(a.id)
    const open = isOpen(a.id)
    return (
      <>
        <div className="flex items-center justify-between py-1 text-sm" style={{ paddingLeft: depth * 18 }}>
          <span className="flex items-center">
            <CollapseToggle open={open} hasChildren={kids.length > 0} onToggle={() => toggle(a.id)} />
            {a.name} <span className="ml-1 text-xs text-slate-400">{a.kind}{a.itemCount ? ` · ${a.itemCount} item${a.itemCount > 1 ? "s" : ""}` : ""}{a.quantity > 0 ? ` · ${a.quantity}${a.unit ? ` ${a.unit}` : ""}` : ""}{!open && kids.length > 0 ? ` · ${kids.length} sub-area${kids.length > 1 ? "s" : ""}` : ""}</span>
          </span>
          <span className="text-right">
            <span className="font-medium">{money(a.rollupTotal, currency)}</span>
            {a.costPerUnit != null && <span className="ml-2 text-xs text-slate-400">{money(a.costPerUnit, currency)}/{a.unit || "unit"}</span>}
          </span>
        </div>
        {open && kids.map((k) => <RollupRow key={k.id} a={k} depth={depth + 1} />)}
      </>
    )
  }
  return (
    <Card className="p-4">
      <div className="mb-2 flex items-center justify-between">
        <h4 className="flex items-center gap-2 text-sm font-semibold text-slate-600"><FolderTree className="h-4 w-4" /> Cost by area</h4>
        <div className="flex items-center gap-2">
          {canExport && (
            <>
              <div className="flex items-center rounded-md border border-[var(--border)] p-0.5 text-xs" title="Choose how the export is grouped">
                {([["detail", "Detail"], ["area", "Area"], ["subarea", "Sub-Area"], ["unit", "Unit"]] as const).map(([v, lbl]) => (
                  <button
                    key={v}
                    onClick={() => setLevel(v)}
                    className={`rounded px-2 py-1 ${level === v ? "bg-[var(--brand)] text-white" : "text-slate-600 hover:bg-slate-100"}`}
                  >
                    {lbl}
                  </button>
                ))}
              </div>
              <Button variant="outline" className="h-8 text-xs" onClick={() => onExport("xlsx", level)}><FileSpreadsheet className="h-4 w-4" /> Excel</Button>
              <Button variant="outline" className="h-8 text-xs" onClick={() => onExport("csv", level)}><Table className="h-4 w-4" /> CSV</Button>
              <Button variant="outline" className="h-8 text-xs" onClick={() => onExport("pdf", level)}><FileText className="h-4 w-4" /> PDF</Button>
            </>
          )}
          {parentIds.size > 0 && <ExpandCollapseAll onExpand={expandAll} onCollapse={() => collapseAll(parentIds)} />}
        </div>
      </div>
      {childrenOf(null).map((r) => <RollupRow key={r.id} a={r} depth={0} />)}
      <div className="mt-2 flex items-center justify-between border-t border-[var(--border)] pt-2 text-sm">
        <span className="text-slate-500">Assigned to areas{data.unassignedTotal > 0 ? ` · unassigned ${money(data.unassignedTotal, currency)}` : ""}</span>
        <span className="font-semibold">{money(data.assignedTotal, currency)}</span>
      </div>
    </Card>
  )
}
