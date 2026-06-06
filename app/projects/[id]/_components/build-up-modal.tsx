"use client"
import { useState } from "react"
import type { CostComponentType } from "@/lib/types"
import { Modal } from "@/components/form"
import { Input, Button } from "@/components/ui"
import { money } from "@/lib/utils"
import type { CompInput } from "./shared"

/** Edit a BOQ item / activity's unit-rate build-up. Amount components are entered as
 *  quantity × rate (e.g. Material 100 × 50, Manpower 40 × 50); Percent components (Waste,
 *  Overheads) apply to the amount subtotal. Live rate = Σ(qty×rate) × (1 + Σ%). */
export function BuildUpModal({ open, onClose, costTypes, currency, initial, onSave }: {
  open: boolean; onClose: () => void; costTypes: CostComponentType[]; currency: string
  initial: CompInput[]; onSave: (comps: CompInput[]) => void
}) {
  const active = costTypes.filter((t) => t.isActive)
  // For Amount types we keep qty + rate strings; for Percent types a single % string.
  const seed = (pick: (c: CompInput) => number | undefined) => {
    const m: Record<number, string> = {}
    initial.forEach((c) => { const v = pick(c); if (v != null) m[c.typeId] = String(v) })
    return m
  }
  const [qty, setQty] = useState<Record<number, string>>(() => {
    const m = seed((c) => c.quantity)
    // Back-compat: an Amount line stored as a direct value shows as 1 × value.
    initial.forEach((c) => { if (m[c.typeId] == null && c.quantity == null && c.value) m[c.typeId] = "1" })
    return m
  })
  const [rate, setRate] = useState<Record<number, string>>(() => {
    const m = seed((c) => c.rate)
    initial.forEach((c) => { if (m[c.typeId] == null && c.rate == null && c.value) m[c.typeId] = String(c.value) })
    return m
  })
  const [pct, setPct] = useState<Record<number, string>>(() => seed((c) => c.value))

  const n = (m: Record<number, string>, id: number) => Number(m[id]) || 0
  const lineAmount = (id: number) => n(qty, id) * n(rate, id)
  const amountSubtotal = active.filter((t) => t.calcKind === "Amount").reduce((s, t) => s + lineAmount(t.id), 0)
  const percentSum = active.filter((t) => t.calcKind === "Percent").reduce((s, t) => s + n(pct, t.id), 0)
  const unitRate = amountSubtotal + amountSubtotal * percentSum / 100

  function save() {
    const comps: CompInput[] = []
    for (const t of active) {
      if (t.calcKind === "Percent") {
        if ((pct[t.id] ?? "") !== "" && n(pct, t.id) !== 0) comps.push({ typeId: t.id, value: n(pct, t.id) })
      } else {
        const q = n(qty, t.id), r = n(rate, t.id)
        if (q !== 0 || r !== 0) comps.push({ typeId: t.id, quantity: q, rate: r })
      }
    }
    onSave(comps); onClose()
  }

  return (
    <Modal open={open} onClose={onClose} title="Cost build-up">
      <div className="space-y-2">
        {active.length === 0 && <p className="text-sm text-slate-400">No active cost-component types. Add some in Settings.</p>}
        <div className="grid grid-cols-[1fr_84px_96px_96px] items-center gap-2 text-xs text-slate-400">
          <span>Component</span><span className="text-right">Qty / %</span><span className="text-right">Rate</span><span className="text-right">Amount</span>
        </div>
        {active.map((t) => (
          <div key={t.id} className="grid grid-cols-[1fr_84px_96px_96px] items-center gap-2">
            <span className="text-sm">{t.name} <span className="text-xs text-slate-400">{t.calcKind === "Percent" ? "%" : "qty × rate"}</span></span>
            {t.calcKind === "Percent" ? (
              <>
                <Input type="number" step="0.0001" value={pct[t.id] ?? ""} placeholder="0"
                       onChange={(e) => setPct({ ...pct, [t.id]: e.target.value })} />
                <span />
                <span className="text-right text-xs text-slate-500">{money(amountSubtotal * n(pct, t.id) / 100, currency)}</span>
              </>
            ) : (
              <>
                <Input type="number" step="0.0001" value={qty[t.id] ?? ""} placeholder="0"
                       onChange={(e) => setQty({ ...qty, [t.id]: e.target.value })} />
                <Input type="number" step="0.0001" value={rate[t.id] ?? ""} placeholder="0"
                       onChange={(e) => setRate({ ...rate, [t.id]: e.target.value })} />
                <span className="text-right text-xs text-slate-500">{money(lineAmount(t.id), currency)}</span>
              </>
            )}
          </div>
        ))}
      </div>
      <div className="mt-4 flex items-center justify-between border-t border-[var(--border)] pt-3">
        <span className="text-sm font-semibold">Unit rate: {money(unitRate, currency)}</span>
        <div className="flex gap-2">
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button onClick={save}>Apply</Button>
        </div>
      </div>
    </Modal>
  )
}
