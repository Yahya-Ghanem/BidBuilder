"use client"
import { useState } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Target } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { Card, Button, Input } from "@/components/ui"
import { Field } from "@/components/form"
import { money } from "@/lib/utils"

/** Solve the commercial adjustment to land the bid on a target price (the final
 *  "we must be at X" move). Preview is non-persisting; Apply writes the adjustment. */
export function TargetPanel({ estimateId, currency, currentBid, currentAdjustment, canApply }: {
  estimateId: number; currency: string; currentBid: number; currentAdjustment: number; canApply: boolean
}) {
  const qc = useQueryClient()
  const [target, setTarget] = useState(String(currentBid))
  const [preview, setPreview] = useState<{ requiredAdjustment: number; targetBidPrice: number } | null>(null)

  const solve = useMutation({
    mutationFn: (apply: boolean) => fetchApi<{ requiredAdjustment: number; targetBidPrice: number }>(
      `/api/estimates/${estimateId}/target`, { method: "POST", body: JSON.stringify({ targetPrice: Number(target || 0), apply }) }),
    onSuccess: (r, apply) => {
      if (apply) { toast.success("Target applied"); setPreview(null); qc.invalidateQueries({ queryKey: ["estimate", estimateId] }) }
      else setPreview({ requiredAdjustment: r.requiredAdjustment, targetBidPrice: r.targetBidPrice })
    },
    onError: (err) => toast.error((err as Error).message),
  })

  const clear = useMutation({
    mutationFn: () => fetchApi(`/api/estimates/${estimateId}/target`, { method: "POST", body: JSON.stringify({ adjustment: 0, apply: true }) }),
    onSuccess: () => { toast.success("Adjustment cleared"); setPreview(null); qc.invalidateQueries({ queryKey: ["estimate", estimateId] }) },
    onError: (err) => toast.error((err as Error).message),
  })

  return (
    <Card className="p-4">
      <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-slate-700">
        <Target className="h-4 w-4 text-[var(--brand)]" /> Target price
      </div>
      <div className="flex items-end gap-2">
        <Field label={`Target bid (${currency})`}><Input type="number" step="0.01" value={target} onChange={(e) => setTarget(e.target.value)} /></Field>
        <Button variant="outline" disabled={solve.isPending} onClick={() => solve.mutate(false)}>Solve</Button>
        {canApply && <Button disabled={solve.isPending} onClick={() => solve.mutate(true)}>Apply</Button>}
      </div>
      {preview && (
        <p className="mt-2 text-sm text-slate-600">
          Commercial adjustment{" "}
          <b className={preview.requiredAdjustment >= 0 ? "text-emerald-600" : "text-rose-600"}>
            {preview.requiredAdjustment >= 0 ? "+" : ""}{money(preview.requiredAdjustment, currency)}
          </b>{" "}→ bid {money(preview.targetBidPrice, currency)}
        </p>
      )}
      {currentAdjustment !== 0 && (
        <p className="mt-2 flex items-center justify-between text-sm text-slate-500">
          <span>Current adjustment: <b className="text-slate-700">{currentAdjustment > 0 ? "+" : ""}{money(currentAdjustment, currency)}</b></span>
          {canApply && <button onClick={() => clear.mutate()} className="text-xs text-slate-400 hover:text-rose-600">Clear</button>}
        </p>
      )}
    </Card>
  )
}
