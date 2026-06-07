"use client"
import { useState } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { CashFlowMonth, CashFlowProjection, EstimateBreakdown, RiskBreakdown } from "@/lib/types"
import { Card, Button } from "@/components/ui"
import { Money } from "@/components/money"
import { ifMatchHeaders } from "./shared"

/**
 * Risk register + suggested contingency + cash-flow S-curve (19.2).
 * Read-only view of the register with inline add/delete; one-click "apply
 * suggestion onto the Contingency markup". The S-curve renders as a tiny SVG
 * sparkline plus a per-month table.
 */
export function RiskAndCashFlowPanel({ estimateId, currency, risks, suggestedAmount, suggestedPct, cashFlow, canEdit }: {
  estimateId: number; currency: string; risks: RiskBreakdown[];
  suggestedAmount: number; suggestedPct: number; cashFlow: CashFlowProjection | undefined;
  canEdit: boolean
}) {
  const qc = useQueryClient()
  const key = ["estimate", estimateId]
  const apply = (d: unknown) => { qc.setQueryData(key, d) }
  const addRisk = useMutation({
    mutationFn: (v: unknown) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/risks`, { method: "POST", headers: ifMatchHeaders(qc, estimateId), body: JSON.stringify(v) }),
    onSuccess: apply, onError: (e) => toast.error((e as Error).message),
  })
  const delRisk = useMutation({
    mutationFn: (rid: number) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/risks/${rid}`, { method: "DELETE", headers: ifMatchHeaders(qc, estimateId) }),
    onSuccess: apply, onError: (e) => toast.error((e as Error).message),
  })
  const applyContingency = useMutation({
    mutationFn: () => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/risks/apply-as-contingency`, { method: "POST", headers: ifMatchHeaders(qc, estimateId), body: "{}" }),
    onSuccess: (d) => { apply(d); toast.success("Contingency updated from risk register") },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Card className="space-y-4 p-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <div className="text-sm font-semibold">Risk register & cash-flow</div>
          <div className="mt-0.5 text-xs text-slate-500">EV per row = probability × impact. Sum feeds the suggested contingency.</div>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <div className="rounded-md border border-[var(--border)] px-3 py-2 text-xs">
            <span className="text-slate-500">Suggested EV </span>
            <Money className="font-semibold tabular-nums" value={suggestedAmount} currency={currency} />
            <span className="ml-2 text-slate-500">≈ </span>
            <span className="font-semibold tabular-nums">{suggestedPct.toFixed(2)}%</span>
          </div>
          {canEdit && (
            <Button variant="outline" className="h-8 text-xs" disabled={applyContingency.isPending || risks.length === 0}
              onClick={() => applyContingency.mutate()}>
              Apply as Contingency markup
            </Button>
          )}
        </div>
      </div>

      {/* Risk table */}
      {risks.length === 0 ? (
        <p className="text-xs text-slate-400">No risks recorded yet. Add one to start building a defensible contingency.</p>
      ) : (
        <table className="w-full text-xs">
          <thead className="bg-slate-50 text-left text-[10px] uppercase tracking-wide text-slate-500">
            <tr>
              <th className="px-2 py-1">Title</th><th className="px-2 py-1">Category</th>
              <th className="px-2 py-1 text-right">Probability</th>
              <th className="px-2 py-1 text-right">Impact</th>
              <th className="px-2 py-1 text-right">EV</th>
              <th className="px-2 py-1"></th>
            </tr>
          </thead>
          <tbody>
            {risks.map((r) => (
              <tr key={r.id} className="border-t border-[var(--border)]">
                <td className="px-2 py-1">{r.title}{r.note ? <span className="ml-1 text-slate-400" title={r.note}>·</span> : null}</td>
                <td className="px-2 py-1 text-slate-600">{r.category}</td>
                <td className="px-2 py-1 text-right tabular-nums">{r.probabilityPct.toFixed(2)}%</td>
                <td className="px-2 py-1 text-right tabular-nums"><Money value={r.impactAmount} currency={currency} /></td>
                <td className="px-2 py-1 text-right tabular-nums font-medium"><Money value={r.expectedValue} currency={currency} /></td>
                <td className="px-2 py-1 text-right">
                  {canEdit && (
                    <button onClick={() => { if (confirm(`Remove "${r.title}"?`)) delRisk.mutate(r.id) }}
                      className="rounded p-0.5 text-slate-400 hover:bg-rose-50 hover:text-rose-600">×</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {canEdit && <AddRiskRow onAdd={(v) => addRisk.mutate(v)} />}

      {/* S-curve: SVG sparkline + table beneath */}
      {cashFlow && cashFlow.monthly.length > 0 && (
        <div className="rounded-md border border-[var(--border)] p-3">
          <div className="mb-2 flex items-center justify-between">
            <span className="text-xs font-semibold">Cash-flow S-curve</span>
            <span className="text-xs text-slate-500">{cashFlow.durationMonths} mo · total <Money value={cashFlow.total} currency={currency} /></span>
          </div>
          <SCurveSparkline months={cashFlow.monthly} />
          <table className="mt-2 w-full text-[11px]">
            <thead className="text-left text-slate-500"><tr>
              <th className="px-1 py-0.5">Mo</th>
              <th className="px-1 py-0.5 text-right">Spend</th>
              <th className="px-1 py-0.5 text-right">Cumulative</th>
              <th className="px-1 py-0.5 text-right">% of total</th>
            </tr></thead>
            <tbody>
              {cashFlow.monthly.map((m) => (
                <tr key={m.month} className="border-t border-[var(--border)]">
                  <td className="px-1 py-0.5">{m.month}</td>
                  <td className="px-1 py-0.5 text-right tabular-nums"><Money value={m.spend} currency={currency} /></td>
                  <td className="px-1 py-0.5 text-right tabular-nums"><Money value={m.cumulative} currency={currency} /></td>
                  <td className="px-1 py-0.5 text-right tabular-nums">{cashFlow.total > 0 ? ((m.cumulative / cashFlow.total) * 100).toFixed(1) : "0.0"}%</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}

function SCurveSparkline({ months }: { months: CashFlowMonth[] }) {
  const w = 320, h = 60, pad = 4
  const total = months.length > 0 ? months[months.length - 1].cumulative : 1
  if (months.length === 0 || total === 0) return null
  const pts = months.map((m, i) => {
    const x = pad + (i / Math.max(1, months.length - 1)) * (w - 2 * pad)
    const y = h - pad - (m.cumulative / total) * (h - 2 * pad)
    return `${x.toFixed(1)},${y.toFixed(1)}`
  }).join(" ")
  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="h-12 w-full text-[var(--brand)]" aria-label="Cash-flow S-curve">
      <polyline fill="none" stroke="currentColor" strokeWidth="2" points={pts} />
    </svg>
  )
}

function AddRiskRow({ onAdd }: { onAdd: (v: unknown) => void }) {
  const [title, setTitle] = useState("")
  const [category, setCategory] = useState("Other")
  const [prob, setProb] = useState("")
  const [impact, setImpact] = useState("")
  const submit = () => {
    if (!title.trim() || !prob || !impact) { toast.error("Title, probability and impact are required."); return }
    onAdd({ title: title.trim(), category, probabilityPct: Number(prob), impactAmount: Number(impact), note: null, sortOrder: 0 })
    setTitle(""); setProb(""); setImpact("")
  }
  return (
    <div className="grid grid-cols-12 gap-2 text-xs">
      <input className="col-span-4 rounded-md border border-[var(--border)] px-2 py-1" placeholder="Risk title"
        value={title} onChange={(e) => setTitle(e.target.value)} />
      <select className="col-span-2 rounded-md border border-[var(--border)] px-2 py-1"
        value={category} onChange={(e) => setCategory(e.target.value)}>
        {["Schedule","Cost","Design","Technical","External","Other"].map((c) => <option key={c}>{c}</option>)}
      </select>
      <input className="col-span-2 rounded-md border border-[var(--border)] px-2 py-1" type="number" min={0} max={100} step="0.01" placeholder="Probability %"
        value={prob} onChange={(e) => setProb(e.target.value)} />
      <input className="col-span-2 rounded-md border border-[var(--border)] px-2 py-1" type="number" min={0} step="0.01" placeholder="Impact"
        value={impact} onChange={(e) => setImpact(e.target.value)} />
      <Button variant="outline" className="col-span-2 h-8" onClick={submit}>Add risk</Button>
    </div>
  )
}
