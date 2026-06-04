"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { Plus, Pencil, Trash2 } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { ResourceRow } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Button, Input } from "@/components/ui"
import { Modal, Field } from "@/components/form"
import { money } from "@/lib/utils"

type Kind = "labor" | "materials" | "equipment" | "subcontractors"
interface TabCfg { key: Kind; label: string; path: string; rateField: "ratePerHour" | "unitPrice" | "unitRate"; rateLabel: string; material?: boolean }

const TABS: TabCfg[] = [
  { key: "labor",          label: "Labor",          path: "/api/resources/labor",          rateField: "ratePerHour", rateLabel: "Rate / hour" },
  { key: "materials",      label: "Materials",      path: "/api/resources/materials",      rateField: "unitPrice",   rateLabel: "Unit price", material: true },
  { key: "equipment",      label: "Equipment",      path: "/api/resources/equipment",      rateField: "ratePerHour", rateLabel: "Rate / hour" },
  { key: "subcontractors", label: "Subcontractors", path: "/api/resources/subcontractors", rateField: "unitRate",    rateLabel: "Unit rate" },
]

export default function ResourcesPage() {
  return (
    <AppShell title="Resource Library">
      <div className="grid gap-6 lg:grid-cols-2">
        {TABS.map((t) => <ResourceTable key={t.key} cfg={t} />)}
      </div>
    </AppShell>
  )
}

function ResourceTable({ cfg }: { cfg: TabCfg }) {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canAdd = can("resource-library", "add")
  const canEdit = can("resource-library", "edit")
  const canDelete = can("resource-library", "delete")
  const [editing, setEditing] = useState<ResourceRow | null>(null)
  const [adding, setAdding] = useState(false)
  const { data, isLoading, error } = useQuery({ queryKey: ["res", cfg.path], queryFn: () => fetchApi<ResourceRow[]>(cfg.path) })

  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`${cfg.path}/${id}`, { method: "DELETE" }),
    onSuccess: () => { toast.success("Deleted"); qc.invalidateQueries({ queryKey: ["res", cfg.path] }) },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
        <span className="text-sm font-semibold">{cfg.label}</span>
        {canAdd && <Button variant="ghost" className="h-7 px-2 text-xs" onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" /> Add</Button>}
      </div>
      {isLoading ? <p className="p-4 text-sm text-slate-400">Loading…</p>
        : error ? <p className="p-4 text-sm text-rose-600">{(error as Error).message}</p>
        : !data?.length ? <p className="p-4 text-sm text-slate-400">None yet.</p>
        : (
          <table className="w-full text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500">
              <tr><th className="px-4 py-2">Code</th><th className="px-4 py-2">Name</th><th className="px-4 py-2">Unit</th><th className="px-4 py-2 text-right">{cfg.rateLabel}</th><th className="px-2 py-2"></th></tr>
            </thead>
            <tbody>
              {data.map((r) => (
                <tr key={r.id} className="border-t border-[var(--border)]">
                  <td className="px-4 py-2 font-mono text-xs">{r.code}</td>
                  <td className="px-4 py-2">{r.name}</td>
                  <td className="px-4 py-2 text-slate-500">{r.unit}</td>
                  <td className="px-4 py-2 text-right">{money((r[cfg.rateField] as number) ?? 0)}</td>
                  <td className="px-2 py-2">
                    <div className="flex justify-end gap-1">
                      {canEdit && <button onClick={() => setEditing(r)} className="rounded p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700"><Pencil className="h-3.5 w-3.5" /></button>}
                      {canDelete && <button onClick={() => { if (confirm(`Delete ${r.code}?`)) del.mutate(r.id) }} className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
                      {!canEdit && !canDelete && <span className="text-xs text-slate-300">—</span>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      {(adding || editing) && (
        <ResourceEditor cfg={cfg} row={editing} onClose={() => { setAdding(false); setEditing(null) }} />
      )}
    </Card>
  )
}

function ResourceEditor({ cfg, row, onClose }: { cfg: TabCfg; row: ResourceRow | null; onClose: () => void }) {
  const qc = useQueryClient()
  const editing = row !== null
  const [f, setF] = useState({
    code: row?.code ?? "",
    name: row?.name ?? "",
    unit: row?.unit ?? (cfg.rateField === "ratePerHour" ? "hr" : ""),
    rate: String((row?.[cfg.rateField] as number | undefined) ?? ""),
    wastagePct: String(row?.wastagePct ?? "0"),
    supplier: row?.supplier ?? "",
    isActive: row?.isActive ?? true,
  })
  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value })

  const mut = useMutation({
    mutationFn: () => {
      const body: Record<string, unknown> = { code: f.code, name: f.name, unit: f.unit, isActive: f.isActive }
      body[cfg.rateField] = Number(f.rate || 0)
      if (cfg.material) { body.wastagePct = Number(f.wastagePct || 0); body.supplier = f.supplier || null }
      return fetchApi(editing ? `${cfg.path}/${row!.id}` : cfg.path, {
        method: editing ? "PUT" : "POST",
        body: JSON.stringify(body),
      })
    },
    onSuccess: () => { toast.success(editing ? "Updated" : "Created"); qc.invalidateQueries({ queryKey: ["res", cfg.path] }); onClose() },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Modal open onClose={onClose} title={`${editing ? "Edit" : "Add"} ${cfg.label.replace(/s$/, "")}`}>
      <form id="res-form" onSubmit={(e) => { e.preventDefault(); mut.mutate() }} className="space-y-3">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Code *"><Input value={f.code} onChange={set("code")} disabled={editing} required /></Field>
          <Field label="Unit"><Input value={f.unit} onChange={set("unit")} /></Field>
        </div>
        <Field label="Name *"><Input value={f.name} onChange={set("name")} required /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={`${cfg.rateLabel} *`}><Input type="number" step="0.0001" min={0} value={f.rate} onChange={set("rate")} required /></Field>
          {cfg.material && <Field label="Wastage %"><Input type="number" step="0.01" min={0} value={f.wastagePct} onChange={set("wastagePct")} /></Field>}
        </div>
        {cfg.material && <Field label="Supplier"><Input value={f.supplier} onChange={set("supplier")} /></Field>}
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="res-form" disabled={mut.isPending}>{mut.isPending ? "Saving…" : "Save"}</Button>
      </div>
    </Modal>
  )
}
