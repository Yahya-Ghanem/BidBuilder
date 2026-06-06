"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { Plus, Pencil, Trash2, Power, PowerOff } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { ResourceRow, BulkResourceResult } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Button, Input, TableScroll } from "@/components/ui"
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

type BulkAction = "activate" | "deactivate" | "delete"

function ResourceTable({ cfg }: { cfg: TabCfg }) {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canAdd = can("resource-library", "add")
  const canEdit = can("resource-library", "edit")
  const canDelete = can("resource-library", "delete")
  const [editing, setEditing] = useState<ResourceRow | null>(null)
  const [adding, setAdding] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const { data, isLoading, error } = useQuery({ queryKey: ["res", cfg.path], queryFn: () => fetchApi<ResourceRow[]>(cfg.path) })

  // Selecting rows enables bulk actions; only meaningful when the user can edit
  // or delete. Keep selection valid as the list changes.
  const canSelect = canEdit || canDelete
  const rows = data ?? []
  const allChecked = rows.length > 0 && rows.every((r) => selected.has(r.id))
  const toggle = (id: number) => setSelected((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })
  const toggleAll = () => setSelected((s) => s.size === rows.length ? new Set() : new Set(rows.map((r) => r.id)))
  const clear = () => setSelected(new Set())

  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`${cfg.path}/${id}`, { method: "DELETE" }),
    onSuccess: () => { toast.success("Deleted"); qc.invalidateQueries({ queryKey: ["res", cfg.path] }) },
    onError: (e) => toast.error((e as Error).message),
  })

  const bulk = useMutation({
    mutationFn: (action: BulkAction) => fetchApi<BulkResourceResult>(`${cfg.path}/bulk`, {
      method: "POST", body: JSON.stringify({ ids: [...selected], action }),
    }),
    onSuccess: (r) => {
      qc.invalidateQueries({ queryKey: ["res", cfg.path] })
      const done = r.action === "delete" ? `${r.deleted} deleted` : `${r.updated} ${r.action}d`
      if (r.skipped.length) toast.warning(`${done} · ${r.skipped.length} skipped (in use by an assembly)`)
      else toast.success(done)
      clear()
    },
    onError: (e) => toast.error((e as Error).message),
  })

  const runBulk = (action: BulkAction) => {
    if (action === "delete" && !confirm(`Delete ${selected.size} selected ${cfg.label.toLowerCase()}? Items used by an assembly are skipped.`)) return
    bulk.mutate(action)
  }

  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
        <span className="text-sm font-semibold">{cfg.label}</span>
        {canAdd && <Button variant="ghost" className="h-7 px-2 text-xs" onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" /> Add</Button>}
      </div>

      {/* Bulk action bar — appears once rows are selected. */}
      {selected.size > 0 && (
        <div className="flex flex-wrap items-center gap-2 border-b border-[var(--border)] bg-slate-50 px-4 py-2 text-xs">
          <span className="font-medium text-slate-600">{selected.size} selected</span>
          {canEdit && <Button variant="outline" className="h-7 text-xs" disabled={bulk.isPending} onClick={() => runBulk("activate")}><Power className="h-3.5 w-3.5" /> Activate</Button>}
          {canEdit && <Button variant="outline" className="h-7 text-xs" disabled={bulk.isPending} onClick={() => runBulk("deactivate")}><PowerOff className="h-3.5 w-3.5" /> Deactivate</Button>}
          {canDelete && <Button variant="outline" className="h-7 text-xs text-rose-600 hover:bg-rose-50" disabled={bulk.isPending} onClick={() => runBulk("delete")}><Trash2 className="h-3.5 w-3.5" /> Delete</Button>}
          <button onClick={clear} className="ml-auto text-slate-400 hover:text-slate-600">Clear</button>
        </div>
      )}

      {isLoading ? <p className="p-4 text-sm text-slate-400">Loading…</p>
        : error ? <p className="p-4 text-sm text-rose-600">{(error as Error).message}</p>
        : !rows.length ? <p className="p-4 text-sm text-slate-400">None yet.</p>
        : (
          <TableScroll>
          <table className="w-full min-w-[34rem] text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500">
              <tr>
                {canSelect && <th className="px-3 py-2"><input type="checkbox" aria-label="Select all" checked={allChecked} onChange={toggleAll} /></th>}
                <th className="px-4 py-2">Code</th><th className="px-4 py-2">Name</th><th className="px-4 py-2">Unit</th><th className="px-4 py-2 text-right">{cfg.rateLabel}</th><th className="px-2 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id} className={`border-t border-[var(--border)] ${selected.has(r.id) ? "bg-[var(--brand)]/5" : ""} ${r.isActive ? "" : "text-slate-400"}`}>
                  {canSelect && <td className="px-3 py-2"><input type="checkbox" aria-label={`Select ${r.code}`} checked={selected.has(r.id)} onChange={() => toggle(r.id)} /></td>}
                  <td className="px-4 py-2 font-mono text-xs">{r.code}{!r.isActive && <span className="ml-1 rounded bg-slate-100 px-1 text-[10px] text-slate-500">inactive</span>}</td>
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
          </TableScroll>
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
