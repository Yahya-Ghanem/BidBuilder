"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { Plus, Trash2, AlertTriangle } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { SupplierQuote } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Button, Input, TableScroll } from "@/components/ui"
import { Modal, Field, ModalActions, Select } from "@/components/form"
import { money } from "@/lib/utils"

/**
 * Supplier-quote register (18.3). A flat, sortable view of every live and expired
 * quote in the tenant. Filter by resource type; spot expired quotes by the badge.
 * Quotes are independent of the live resource library (so a stale supplier offer
 * doesn't silently displace a current rate) but link to a resource for traceability.
 */
export default function QuotesPage() {
  return <AppShell title="Quotes Register"><QuotesTable /></AppShell>
}

const TYPES = ["", "Labor", "Material", "Equipment", "Subcontractor"] as const
type TypeFilter = (typeof TYPES)[number]

function QuotesTable() {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canAdd = can("resource-library", "add")
  const canDelete = can("resource-library", "delete")

  const [filter, setFilter] = useState<TypeFilter>("")
  const [adding, setAdding] = useState(false)

  const { data, isLoading, error } = useQuery({
    queryKey: ["quotes", filter],
    queryFn: () => fetchApi<SupplierQuote[]>(filter ? `/api/quotes?type=${filter}` : "/api/quotes"),
  })

  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/quotes/${id}`, { method: "DELETE" }),
    onSuccess: () => { toast.success("Quote deleted"); qc.invalidateQueries({ queryKey: ["quotes"] }) },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
        <div className="flex items-center gap-3">
          <span className="text-sm font-semibold">Supplier quotes</span>
          <Select value={filter} onChange={(e) => setFilter(e.target.value as TypeFilter)} className="h-7 text-xs">
            {TYPES.map((t) => <option key={t} value={t}>{t === "" ? "All types" : t}</option>)}
          </Select>
        </div>
        {canAdd && <Button variant="ghost" className="h-7 px-2 text-xs" onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" /> Add quote</Button>}
      </div>

      {isLoading ? <p className="p-4 text-sm text-slate-400">Loading…</p>
        : error ? <p className="p-4 text-sm text-rose-600">{(error as Error).message}</p>
        : !data?.length ? <p className="p-4 text-sm text-slate-400">No quotes yet.</p>
        : (
          <TableScroll>
          <table className="w-full min-w-[52rem] text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500">
              <tr>
                <th className="px-4 py-2">Supplier</th>
                <th className="px-4 py-2">Type</th>
                <th className="px-4 py-2">Quoted</th>
                <th className="px-4 py-2">Valid until</th>
                <th className="px-4 py-2 text-right">Price</th>
                <th className="px-4 py-2">Unit</th>
                <th className="px-4 py-2">Notes</th>
                <th className="px-2 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {data.map((q) => (
                <tr key={q.id} className="border-t border-[var(--border)]">
                  <td className="px-4 py-2">{q.supplier}</td>
                  <td className="px-4 py-2 text-slate-600">{q.resourceType}</td>
                  <td className="px-4 py-2 text-slate-500">{q.quotedOn}</td>
                  <td className="px-4 py-2">
                    {q.validUntil ?? <span className="text-slate-400">—</span>}
                    {q.isExpired && (
                      <span className="ml-2 inline-flex items-center gap-1 rounded bg-rose-50 px-1.5 py-0.5 text-[10px] font-medium uppercase text-rose-700">
                        <AlertTriangle className="h-3 w-3" /> expired
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-2 text-right tabular-nums">{money(q.price)} <span className="text-slate-400">{q.currency}</span></td>
                  <td className="px-4 py-2 text-slate-500">{q.unit}</td>
                  <td className="px-4 py-2 text-slate-500">{q.note ?? <span className="text-slate-300">—</span>}</td>
                  <td className="px-2 py-2">
                    {canDelete && (
                      <button onClick={() => { if (confirm(`Delete quote from ${q.supplier}?`)) del.mutate(q.id) }}
                        className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600">
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </TableScroll>
        )}

      {adding && <AddQuoteModal onClose={() => setAdding(false)} onSaved={() => { setAdding(false); qc.invalidateQueries({ queryKey: ["quotes"] }) }} />}
    </Card>
  )
}

function AddQuoteModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const today = new Date().toISOString().slice(0, 10)
  const [type, setType] = useState("Material")
  const [supplier, setSupplier] = useState("")
  const [price, setPrice] = useState("")
  const [currency, setCurrency] = useState("AED")
  const [unit, setUnit] = useState("")
  const [quotedOn, setQuotedOn] = useState(today)
  const [validUntil, setValidUntil] = useState("")
  const [note, setNote] = useState("")

  const save = useMutation({
    mutationFn: () => fetchApi("/api/quotes", {
      method: "POST",
      body: JSON.stringify({
        resourceType: type, resourceId: null,
        supplier: supplier.trim(), price: Number(price || 0), currency, unit,
        quotedOn, validUntil: validUntil || null,
        note: note.trim() || null, attachmentUrl: null,
      }),
    }),
    onSuccess: () => { toast.success("Quote added"); onSaved() },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Modal open onClose={onClose} title="Add supplier quote">
      <form onSubmit={(e) => { e.preventDefault(); save.mutate() }} className="grid gap-3">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Type">
            <Select value={type} onChange={(e) => setType(e.target.value)}>
              <option>Labor</option><option>Material</option><option>Equipment</option><option>Subcontractor</option>
            </Select>
          </Field>
          <Field label="Supplier"><Input value={supplier} onChange={(e) => setSupplier(e.target.value)} required /></Field>
          <Field label="Price"><Input type="number" step="0.0001" min={0} value={price} onChange={(e) => setPrice(e.target.value)} required /></Field>
          <Field label="Currency"><Input value={currency} onChange={(e) => setCurrency(e.target.value.toUpperCase())} maxLength={3} /></Field>
          <Field label="Unit"><Input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="t, m3, hr…" /></Field>
          <Field label="Quoted on"><Input type="date" value={quotedOn} onChange={(e) => setQuotedOn(e.target.value)} /></Field>
          <Field label="Valid until (optional)"><Input type="date" value={validUntil} onChange={(e) => setValidUntil(e.target.value)} /></Field>
        </div>
        <Field label="Notes"><Input value={note} onChange={(e) => setNote(e.target.value)} placeholder="rebar T16; ref Q-2026-014" /></Field>
        <ModalActions onCancel={onClose} busy={save.isPending} submitLabel="Save quote" />
      </form>
    </Modal>
  )
}
