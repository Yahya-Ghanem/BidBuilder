"use client"
import { useState } from "react"
import { Plus } from "lucide-react"
import { Button, Input } from "@/components/ui"
import { Field, Select } from "@/components/form"

/** Inline form to add a preliminary line. */
export function AddPrelim({ onAdd }: { onAdd: (v: unknown) => void }) {
  const [d, setD] = useState(""); const [kind, setKind] = useState("Fixed"); const [amt, setAmt] = useState("")
  return (
    <div className="mt-3 grid grid-cols-[1fr_110px_90px_auto] items-end gap-2 border-t border-[var(--border)] pt-3">
      <Field label="Description"><Input value={d} onChange={(e) => setD(e.target.value)} /></Field>
      <Field label="Kind"><Select value={kind} onChange={(e) => setKind(e.target.value)}><option>Fixed</option><option>TimeRelated</option></Select></Field>
      <Field label="Amount"><Input type="number" step="0.01" value={amt} onChange={(e) => setAmt(e.target.value)} /></Field>
      <Button variant="outline" onClick={() => { if (d) { onAdd({ description: d, kind, amount: Number(amt || 0), sortOrder: 0 }); setD(""); setAmt("") } }}><Plus className="h-4 w-4" /></Button>
    </div>
  )
}

/** Inline form to add a markup line (Overhead, Profit, Contingency, Escalation). */
export function AddMarkup({ onAdd }: { onAdd: (v: unknown) => void }) {
  const [type, setType] = useState("Overhead"); const [pct, setPct] = useState(""); const [order, setOrder] = useState("1")
  return (
    <div className="mt-3 grid grid-cols-[1fr_90px_70px_auto] items-end gap-2 border-t border-[var(--border)] pt-3">
      <Field label="Type"><Select value={type} onChange={(e) => setType(e.target.value)}><option>Overhead</option><option>Profit</option><option>Contingency</option><option>Escalation</option></Select></Field>
      <Field label="%"><Input type="number" step="0.01" value={pct} onChange={(e) => setPct(e.target.value)} /></Field>
      <Field label="Order"><Input type="number" value={order} onChange={(e) => setOrder(e.target.value)} /></Field>
      <Button variant="outline" onClick={() => { if (pct) { onAdd({ type, percentage: Number(pct), applyOrder: Number(order || 1) }); setPct("") } }}><Plus className="h-4 w-4" /></Button>
    </div>
  )
}
