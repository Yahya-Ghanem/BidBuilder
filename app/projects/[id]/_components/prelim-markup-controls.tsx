"use client"
import { useState } from "react"
import { Plus } from "lucide-react"
import { Button, Input } from "@/components/ui"
import { Field, Select } from "@/components/form"
import { useT } from "@/lib/i18n"

/** Inline form to add a preliminary line. */
export function AddPrelim({ onAdd }: { onAdd: (v: unknown) => void }) {
  const t = useT()
  const [d, setD] = useState(""); const [kind, setKind] = useState("Fixed"); const [amt, setAmt] = useState("")
  return (
    <div className="mt-3 grid grid-cols-[1fr_110px_90px_auto] items-end gap-2 border-t border-[var(--border)] pt-3">
      <Field label={t("ed.pm.description")}><Input value={d} onChange={(e) => setD(e.target.value)} /></Field>
      <Field label={t("ed.pm.kind")}>
        <Select value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="Fixed">{t("ed.pm.kind.fixed")}</option>
          <option value="TimeRelated">{t("ed.pm.kind.timed")}</option>
        </Select>
      </Field>
      <Field label={t("ed.pm.amount")}><Input type="number" step="0.01" value={amt} onChange={(e) => setAmt(e.target.value)} /></Field>
      <Button variant="outline" onClick={() => { if (d) { onAdd({ description: d, kind, amount: Number(amt || 0), sortOrder: 0 }); setD(""); setAmt("") } }}><Plus className="h-4 w-4" /></Button>
    </div>
  )
}

/** Inline form to add a markup line (Overhead, Profit, Contingency, Escalation). */
export function AddMarkup({ onAdd }: { onAdd: (v: unknown) => void }) {
  const t = useT()
  const [type, setType] = useState("Overhead"); const [pct, setPct] = useState(""); const [order, setOrder] = useState("1")
  return (
    <div className="mt-3 grid grid-cols-[1fr_90px_70px_auto] items-end gap-2 border-t border-[var(--border)] pt-3">
      <Field label={t("ed.pm.type")}>
        <Select value={type} onChange={(e) => setType(e.target.value)}>
          <option value="Overhead">{t("ed.pm.type.overhead")}</option>
          <option value="Profit">{t("ed.pm.type.profit")}</option>
          <option value="Contingency">{t("ed.pm.type.cont")}</option>
          <option value="Escalation">{t("ed.pm.type.esc")}</option>
        </Select>
      </Field>
      <Field label={t("ed.pm.pct")}><Input type="number" step="0.01" value={pct} onChange={(e) => setPct(e.target.value)} /></Field>
      <Field label={t("ed.pm.order")}><Input type="number" value={order} onChange={(e) => setOrder(e.target.value)} /></Field>
      <Button variant="outline" onClick={() => { if (pct) { onAdd({ type, percentage: Number(pct), applyOrder: Number(order || 1) }); setPct("") } }}><Plus className="h-4 w-4" /></Button>
    </div>
  )
}
