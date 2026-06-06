"use client"
import { useEffect, useState } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { RotateCcw, SlidersHorizontal } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { MarkupBreakdown, WhatIfResult } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"
import { money } from "@/lib/utils"
import { Row } from "./shared"

/** Non-persisting margin preview: tweak markup %s and see the bid price move.
 *  Apply writes the changed % values back through the markup PUT endpoint. */
export function WhatIfPanel({ estimateId, markups, currency, canEdit }: {
  estimateId: number; markups: MarkupBreakdown[]; currency: string; canEdit: boolean
}) {
  const qc = useQueryClient()
  const original = Object.fromEntries(markups.map((m) => [m.id, String(m.percentage)]))
  const [draft, setDraft] = useState<Record<number, string>>(original)
  const [result, setResult] = useState<WhatIfResult | null>(null)

  const lines = markups.map((m) => ({
    id: m.id, type: m.type, label: m.label, applyOrder: m.applyOrder,
    percentage: Number(draft[m.id] ?? m.percentage) || 0,
  }))
  const draftKey = JSON.stringify(lines.map((l) => [l.id, l.percentage]))

  const preview = useMutation({
    mutationFn: (body: unknown) => fetchApi<WhatIfResult>(`/api/estimates/${estimateId}/whatif`, { method: "POST", body: JSON.stringify(body) }),
    onSuccess: (r) => setResult(r),
    onError: (err) => toast.error((err as Error).message),
  })

  // Re-price whenever a percentage changes — debounced so typing "12.5" sends one
  // request after the user pauses, not four mid-keystroke.
  useEffect(() => {
    const t = setTimeout(() => {
      preview.mutate({ markups: lines.map((l) => ({ type: l.type, label: l.label, percentage: l.percentage, applyOrder: l.applyOrder })) })
    }, 250)
    return () => clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draftKey])

  const apply = useMutation({
    mutationFn: async () => {
      for (const m of markups) {
        const p = Number(draft[m.id] ?? m.percentage) || 0
        if (p !== m.percentage)
          await fetchApi(`/api/estimates/${estimateId}/markups/${m.id}`, {
            method: "PUT", body: JSON.stringify({ type: m.type, label: m.label, percentage: p, applyOrder: m.applyOrder }),
          })
      }
    },
    onSuccess: () => { toast.success("Margins applied"); qc.invalidateQueries({ queryKey: ["estimate", estimateId] }) },
    onError: (err) => toast.error((err as Error).message),
  })

  const changed = markups.some((m) => (Number(draft[m.id] ?? m.percentage) || 0) !== m.percentage)
  const delta = result ? result.bidPrice - result.baselineBidPrice : 0

  return (
    <Card className="p-4">
      <div className="mb-3 flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-semibold text-slate-700">
          <SlidersHorizontal className="h-4 w-4 text-[var(--brand)]" /> What-if margin
        </div>
        {changed && (
          <button onClick={() => setDraft(original)} className="flex items-center gap-1 text-xs text-slate-400 hover:text-slate-600">
            <RotateCcw className="h-3 w-3" /> Reset
          </button>
        )}
      </div>

      <Row left="Direct + indirect (base)" right={money((result?.directCost ?? 0) + (result?.indirectCost ?? 0), currency)} />
      {markups.map((m) => {
        const line = result?.markups.find((x) => x.applyOrder === m.applyOrder && x.type === m.type)
        return (
          <div key={m.id} className="flex items-center justify-between border-t border-[var(--border)] py-1.5 text-sm">
            <span className="text-slate-600">{m.label || m.type}</span>
            <div className="flex items-center gap-2">
              <Input className="w-20 py-1 text-right" type="number" step="0.01" min={0}
                value={draft[m.id] ?? ""} onChange={(ev) => setDraft({ ...draft, [m.id]: ev.target.value })} />
              <span className="w-8 text-xs text-slate-400">%</span>
              <span className="w-28 text-right font-medium">{money(line?.computedAmount ?? 0, currency)}</span>
            </div>
          </div>
        )
      })}

      <div className="mt-2 flex items-center justify-between border-t-2 border-[var(--border)] pt-2">
        <span className="text-sm font-semibold text-slate-700">Projected bid price</span>
        <div className="text-right">
          <div className="text-lg font-bold text-[var(--brand)]">{money(result?.bidPrice ?? 0, currency)}</div>
          {Math.abs(delta) >= 0.01 && (
            <div className={delta > 0 ? "text-xs text-emerald-600" : "text-xs text-rose-600"}>
              {delta > 0 ? "+" : ""}{money(delta, currency)} vs current
            </div>
          )}
        </div>
      </div>

      {canEdit && (
        <div className="mt-3 flex justify-end">
          <Button disabled={!changed || apply.isPending} onClick={() => apply.mutate()}>
            {apply.isPending ? "Applying…" : "Apply these margins"}
          </Button>
        </div>
      )}
    </Card>
  )
}
