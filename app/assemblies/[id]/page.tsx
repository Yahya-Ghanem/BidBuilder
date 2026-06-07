"use client"

import { use, useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { useRouter } from "next/navigation"
import { Plus, Trash2, Pencil } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { AssemblyDetail, ResourceRow } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Button, Input, TableScroll } from "@/components/ui"
import { Modal, Field, Select } from "@/components/form"
import { Money } from "@/components/money"

const RES_PATH: Record<string, string> = {
  Labor: "/api/resources/labor", Material: "/api/resources/materials",
  Equipment: "/api/resources/equipment", Subcontractor: "/api/resources/subcontractors",
}

export default function AssemblyDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params)
  return (
    <AppShell title="Assembly">
      <Detail id={Number(id)} />
    </AppShell>
  )
}

function Detail({ id }: { id: number }) {
  const qc = useQueryClient()
  const router = useRouter()
  const { can } = usePermissions()
  const canEdit = can("assemblies", "edit")
  const canDelete = can("assemblies", "delete")
  const [editing, setEditing] = useState(false)
  const key = ["assembly", id]
  const { data, isLoading, error } = useQuery({ queryKey: key, queryFn: () => fetchApi<AssemblyDetail>(`/api/assemblies/${id}`) })

  const del = useMutation({
    mutationFn: (cid: number) => fetchApi(`/api/assemblies/${id}/components/${cid}`, { method: "DELETE" }),
    onSuccess: (d) => { qc.setQueryData(key, d); qc.invalidateQueries({ queryKey: ["assemblies"] }); toast.success("Removed") },
    onError: (e) => toast.error((e as Error).message),
  })
  const updateHeader = useMutation({
    mutationFn: (v: { name: string; unit: string; isActive: boolean }) =>
      fetchApi(`/api/assemblies/${id}`, { method: "PUT", body: JSON.stringify({ code: data!.code, ...v }) }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: key }); qc.invalidateQueries({ queryKey: ["assemblies"] }); setEditing(false); toast.success("Assembly updated") },
    onError: (e) => toast.error((e as Error).message),
  })
  const deleteAssembly = useMutation({
    mutationFn: () => fetchApi(`/api/assemblies/${id}`, { method: "DELETE" }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["assemblies"] }); toast.success("Assembly deleted"); router.push("/assemblies") },
    onError: (e) => toast.error((e as Error).message),   // 409 message shown if the assembly is in use
  })

  if (isLoading) return <p className="text-muted">Loading…</p>
  if (error) return <p className="text-rose-600">{(error as Error).message}</p>
  const a = data!

  return (
    <div className="space-y-4">
      <Card className="flex items-center justify-between p-5">
        <div>
          <span className="font-mono text-xs text-muted">{a.code}</span>
          <h2 className="text-xl font-bold text-slate-800">{a.name}{!a.isActive && <span className="ml-2 text-xs font-normal text-muted">(inactive)</span>}</h2>
          <p className="text-sm text-slate-500">Build-up per <b>{a.unit || "unit"}</b></p>
        </div>
        <div className="flex items-center gap-4">
          <div className="text-right">
            <div className="text-xs text-slate-500">Computed unit rate</div>
            {/* 25.1 — TODO: confirm currency source */}
            <Money className="text-2xl font-bold text-[var(--brand)]" as="div" value={a.computedRate} currency="AED" />
          </div>
          {(canEdit || canDelete) && (
            <div className="flex flex-col gap-2">
              {canEdit && <Button variant="outline" className="h-8 text-xs" onClick={() => setEditing(true)}><Pencil className="h-3.5 w-3.5" /> Edit</Button>}
              {canDelete && <Button variant="outline" className="h-8 text-xs text-rose-600 hover:bg-rose-50" disabled={deleteAssembly.isPending}
                onClick={() => { if (confirm(`Delete assembly ${a.code}? This can't be undone.`)) deleteAssembly.mutate() }}><Trash2 className="h-3.5 w-3.5" /> Delete</Button>}
            </div>
          )}
        </div>
      </Card>

      {editing && <EditAssemblyModal a={a} busy={updateHeader.isPending} onClose={() => setEditing(false)} onSave={(v) => updateHeader.mutate(v)} />}

      <Card className="overflow-hidden">
        <div className="border-b border-[var(--border)] px-4 py-2 text-sm font-semibold">Components</div>
        <TableScroll>
        <table className="w-full min-w-[40rem] text-sm">
          <thead className="bg-slate-50 text-left text-xs text-slate-500">
            <tr><th className="px-4 py-2">Type</th><th className="px-4 py-2">Resource</th><th className="px-4 py-2 text-right">Factor</th><th className="px-4 py-2 text-right">Rate</th><th className="px-4 py-2 text-right">Cost</th><th className="px-2 py-2"></th></tr>
          </thead>
          <tbody>
            {a.components.map((c) => (
              <tr key={c.id} className="border-t border-[var(--border)]">
                <td className="px-4 py-2 text-slate-500">{c.resourceType}</td>
                <td className="px-4 py-2"><span className="font-mono text-xs text-muted">{c.resourceCode}</span> {c.resourceName}</td>
                <td className="px-4 py-2 text-right">{c.factor}</td>
                {/* 25.1 — TODO: confirm currency source */}
                <td className="px-4 py-2 text-right"><Money value={c.resourceRate} currency="AED" /></td>
                {/* 25.1 — TODO: confirm currency source */}
                <td className="px-4 py-2 text-right font-medium"><Money value={c.cost} currency="AED" /></td>
                <td className="px-2 py-2 text-right">
                  {canEdit && <button onClick={() => del.mutate(c.id)} className="rounded p-1 text-muted hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
                </td>
              </tr>
            ))}
            {!a.components.length && <tr><td colSpan={6} className="px-4 py-3 text-sm text-muted">No components yet — add one below.</td></tr>}
          </tbody>
        </table>
        </TableScroll>
        {canEdit && <AddComponent assemblyId={id} onUpdated={(d) => { qc.setQueryData(key, d); qc.invalidateQueries({ queryKey: ["assemblies"] }) }} />}
      </Card>
    </div>
  )
}

function AddComponent({ assemblyId, onUpdated }: { assemblyId: number; onUpdated: (d: AssemblyDetail) => void }) {
  const [type, setType] = useState("Labor")
  const [resourceId, setResourceId] = useState("")
  const [factor, setFactor] = useState("1")

  const resources = useQuery({ queryKey: ["res", RES_PATH[type]], queryFn: () => fetchApi<ResourceRow[]>(RES_PATH[type]) })

  const add = useMutation({
    mutationFn: () => fetchApi<AssemblyDetail>(`/api/assemblies/${assemblyId}/components`, {
      method: "POST",
      body: JSON.stringify({ resourceType: type, resourceId: Number(resourceId), factor: Number(factor), sortOrder: 0 }),
    }),
    onSuccess: (d) => { toast.success("Component added"); onUpdated(d); setResourceId(""); setFactor("1") },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <div className="grid grid-cols-[140px_1fr_110px_auto] items-end gap-3 border-t border-[var(--border)] bg-slate-50 p-4">
      <Field label="Type">
        <Select value={type} onChange={(e) => { setType(e.target.value); setResourceId("") }}>
          {Object.keys(RES_PATH).map((t) => <option key={t} value={t}>{t}</option>)}
        </Select>
      </Field>
      <Field label="Resource">
        <Select value={resourceId} onChange={(e) => setResourceId(e.target.value)}>
          <option value="">Select…</option>
          {resources.data?.map((r) => <option key={r.id} value={r.id}>{r.code} — {r.name}</option>)}
        </Select>
      </Field>
      <Field label="Factor"><Input type="number" step="0.0001" min={0} value={factor} onChange={(e) => setFactor(e.target.value)} /></Field>
      <Button disabled={!resourceId || add.isPending} onClick={() => add.mutate()}><Plus className="h-4 w-4" /> Add</Button>
    </div>
  )
}

function EditAssemblyModal({ a, busy, onClose, onSave }: {
  a: AssemblyDetail; busy: boolean; onClose: () => void; onSave: (v: { name: string; unit: string; isActive: boolean }) => void
}) {
  const [name, setName] = useState(a.name)
  const [unit, setUnit] = useState(a.unit)
  const [isActive, setIsActive] = useState(a.isActive)
  return (
    <Modal open onClose={onClose} title={`Edit ${a.code}`}>
      <form id="edit-asm" onSubmit={(e) => { e.preventDefault(); if (name.trim()) onSave({ name: name.trim(), unit: unit.trim(), isActive }) }} className="space-y-3">
        <Field label="Code"><Input value={a.code} disabled /></Field>
        <Field label="Name *"><Input value={name} onChange={(e) => setName(e.target.value)} required /></Field>
        <Field label="Unit"><Input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="m3, m2, no…" /></Field>
        <label className="flex items-center gap-2 text-sm text-slate-600">
          <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} /> Active
        </label>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="edit-asm" disabled={busy}>{busy ? "Saving…" : "Save"}</Button>
      </div>
    </Modal>
  )
}
