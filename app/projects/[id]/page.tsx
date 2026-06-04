"use client"

import { use, useState, useEffect, useRef, useMemo } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { useRouter } from "next/navigation"
import { Plus, Trash2, SlidersHorizontal, RotateCcw, Copy, Upload, FileDown, FolderInput, Pencil } from "lucide-react"
import { toast } from "sonner"
import { fetchApi, downloadFile, uploadFile, ApiError } from "@/lib/api"
import type { Project, EstimateSummary, EstimateBreakdown, SectionBreakdown, ItemBreakdown, AssemblyRow, ProjectTeam, GroupOption, MarkupBreakdown, WhatIfResult, ImportResult, CurrencyRates, CostComponentType, ActivityType, Area, AreaRollup, AreaRollupRow } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { useAuth } from "@/lib/auth"
import { usePermissions } from "@/lib/permissions"
import { Card, Badge, Button, Input, statusColor } from "@/components/ui"
import { Modal, Field, Select } from "@/components/form"
import { money, cn } from "@/lib/utils"
import { FileSpreadsheet, FileText, Table, Users, Layers, FolderTree } from "lucide-react"

export default function ProjectDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params)
  return (
    <AppShell title="Project">
      <Detail projectId={Number(id)} />
    </AppShell>
  )
}

function Detail({ projectId }: { projectId: number }) {
  const { user } = useAuth()
  const isAdmin = user?.role === "TenantAdmin" || user?.role === "SuperAdmin"
  const [editing, setEditing] = useState(false)
  const project = useQuery({ queryKey: ["project", projectId], queryFn: () => fetchApi<Project>(`/api/projects/${projectId}`) })

  if (project.isLoading) return <p className="text-slate-400">Loading…</p>
  if (project.error) return <p className="text-rose-600">{(project.error as Error).message}</p>
  const p = project.data!

  return (
    <div className="space-y-6">
      <Card className="p-5">
        <div className="flex items-start justify-between">
          <div>
            <span className="font-mono text-xs text-slate-400">{p.code}</span>
            <h2 className="text-xl font-bold text-slate-800">{p.name}</h2>
            <p className="text-sm text-slate-500">{p.clientName ?? "—"} · {p.location ?? "—"}</p>
          </div>
          <div className="flex items-center gap-2">
            <Badge className={statusColor(p.status)}>{p.status}</Badge>
            {isAdmin && <Button variant="outline" className="h-8 text-xs" onClick={() => setEditing(true)}><Pencil className="h-3.5 w-3.5" /> Edit</Button>}
          </div>
        </div>
        <div className="mt-4 flex gap-6 text-sm text-slate-600">
          <span>Currency: <b>{p.currency}</b></span>
          <span>Duration: <b>{p.durationMonths ?? "—"} mo</b></span>
          <span>Teams: <b>{p.teamCount}</b></span>
        </div>
      </Card>

      {editing && <EditProjectModal project={p} onClose={() => setEditing(false)} />}

      <TeamsPanel projectId={projectId} />

      <AreasPanel projectId={projectId} />

      <EstimatesSection projectId={projectId} />
    </div>
  )
}

function EditProjectModal({ project, onClose }: { project: Project; onClose: () => void }) {
  const qc = useQueryClient()
  const [f, setF] = useState({
    name: project.name,
    clientName: project.clientName ?? "",
    location: project.location ?? "",
    currency: project.currency,
    durationMonths: project.durationMonths != null ? String(project.durationMonths) : "",
    status: project.status,
    tenderDueAt: project.tenderDueAt ? project.tenderDueAt.slice(0, 10) : "",
  })
  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setF({ ...f, [k]: e.target.value })
  const save = useMutation({
    mutationFn: () => fetchApi<Project>(`/api/projects/${project.id}`, {
      method: "PUT",
      body: JSON.stringify({
        name: f.name, clientName: f.clientName || null, location: f.location || null, currency: f.currency,
        durationMonths: f.durationMonths ? Number(f.durationMonths) : null,
        tenderDueAt: f.tenderDueAt ? new Date(f.tenderDueAt).toISOString() : null,
        status: f.status,
      }),
    }),
    onSuccess: () => {
      toast.success("Project updated")
      qc.invalidateQueries({ queryKey: ["project", project.id] })
      qc.invalidateQueries({ queryKey: ["projects"] })
      qc.invalidateQueries({ queryKey: ["estimate"] })            // duration change re-prices breakdowns
      qc.invalidateQueries({ queryKey: ["estimates", project.id] })
      onClose()
    },
    onError: (e) => toast.error((e as Error).message),
  })
  const STATUSES = ["Draft", "Bidding", "Submitted", "Won", "Lost", "Archived"]
  return (
    <Modal open onClose={onClose} title={`Edit ${project.code}`}>
      <form id="edit-project" onSubmit={(e) => { e.preventDefault(); if (f.name.trim()) save.mutate() }} className="space-y-3">
        <Field label="Name *"><Input value={f.name} onChange={set("name")} required /></Field>
        <Field label="Client"><Input value={f.clientName} onChange={set("clientName")} /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Location"><Input value={f.location} onChange={set("location")} /></Field>
          <Field label="Currency"><Input value={f.currency} onChange={set("currency")} maxLength={3} /></Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Duration (months)"><Input type="number" min={0} value={f.durationMonths} onChange={set("durationMonths")} /></Field>
          <Field label="Tender due"><Input type="date" value={f.tenderDueAt} onChange={set("tenderDueAt")} /></Field>
          <Field label="Status"><Select value={f.status} onChange={set("status")}>{STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}</Select></Field>
        </div>
        <p className="text-xs text-slate-400">Changing the duration re-prices time-related preliminaries on this project's draft estimates.</p>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="edit-project" disabled={save.isPending}>{save.isPending ? "Saving…" : "Save"}</Button>
      </div>
    </Modal>
  )
}

/** Estimate revisions: pick a revision, create blank / duplicate, view & edit it. */
function EstimatesSection({ projectId }: { projectId: number }) {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canManage = can("estimate-admin", "add")
  const canEditMeta = can("estimate-admin", "edit")
  const canDelete = can("estimate-admin", "delete")
  const estimates = useQuery({ queryKey: ["estimates", projectId], queryFn: () => fetchApi<EstimateSummary[]>(`/api/projects/${projectId}/estimates`) })
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [copyOpen, setCopyOpen] = useState(false)
  const router = useRouter()

  const list = estimates.data ?? []
  const currentId = selectedId != null && list.some((e) => e.id === selectedId) ? selectedId : list[list.length - 1]?.id ?? null

  const createBlank = useMutation({
    mutationFn: (title: string) => fetchApi<EstimateSummary>(`/api/projects/${projectId}/estimates`, { method: "POST", body: JSON.stringify({ title }) }),
    onSuccess: (e) => { qc.invalidateQueries({ queryKey: ["estimates", projectId] }); qc.invalidateQueries({ queryKey: ["project", projectId] }); setSelectedId(e.id); toast.success(`Revision ${e.revision} created`) },
    onError: (er) => toast.error((er as Error).message),
  })
  const clone = useMutation({
    mutationFn: (srcId: number) => fetchApi<EstimateSummary>(`/api/projects/${projectId}/estimates/${srcId}/clone`, { method: "POST", body: JSON.stringify({}) }),
    onSuccess: (e) => { qc.invalidateQueries({ queryKey: ["estimates", projectId] }); qc.invalidateQueries({ queryKey: ["project", projectId] }); setSelectedId(e.id); toast.success(`Revision ${e.revision} created (copy)`) },
    onError: (er) => toast.error((er as Error).message),
  })
  const del = useMutation({
    mutationFn: (eid: number) => fetchApi(`/api/projects/${projectId}/estimates/${eid}`, { method: "DELETE" }),
    onSuccess: () => { setSelectedId(null); qc.invalidateQueries({ queryKey: ["estimates", projectId] }); qc.invalidateQueries({ queryKey: ["project", projectId] }); toast.success("Revision deleted") },
    onError: (er) => toast.error((er as Error).message),
  })

  if (estimates.isLoading) return <p className="text-slate-400">Loading estimates…</p>

  if (!list.length) {
    return (
      <Card className="p-6 text-center">
        <p className="mb-3 text-sm text-slate-500">No estimate yet for this project.</p>
        {canManage
          ? <Button onClick={() => createBlank.mutate("Base Estimate")} disabled={createBlank.isPending}><Plus className="h-4 w-4" /> Create first estimate</Button>
          : <p className="text-xs text-slate-400">You don't have permission to create estimates.</p>}
      </Card>
    )
  }

  return (
    <div className="space-y-4">
      <Card className="flex flex-wrap items-center justify-between gap-3 p-3">
        <div className="flex items-center gap-2">
          <span className="text-sm font-semibold text-slate-600">Revision</span>
          <Select className="w-auto py-1.5" value={String(currentId)} onChange={(e) => setSelectedId(Number(e.target.value))}>
            {list.map((e) => (
              <option key={e.id} value={e.id}>Rev {e.revision} · {e.status} · {money(e.bidPrice, e.currency)}</option>
            ))}
          </Select>
        </div>
        {(canManage || canDelete) && (
          <div className="flex gap-2">
            {canManage && <Button variant="outline" className="h-8 text-xs" disabled={createBlank.isPending} onClick={() => createBlank.mutate(`Revision ${(list[list.length - 1]?.revision ?? 0) + 1}`)}><Plus className="h-4 w-4" /> New</Button>}
            {canManage && <Button variant="outline" className="h-8 text-xs" disabled={clone.isPending || currentId == null} onClick={() => currentId != null && clone.mutate(currentId)}><Copy className="h-4 w-4" /> Duplicate</Button>}
            {canManage && <Button variant="outline" className="h-8 text-xs" disabled={currentId == null} onClick={() => setCopyOpen(true)}><FolderInput className="h-4 w-4" /> Copy to…</Button>}
            {canDelete && (
              <Button variant="outline" className="h-8 text-xs text-rose-600 hover:bg-rose-50" disabled={del.isPending || currentId == null}
                onClick={() => { const cur = list.find((e) => e.id === currentId); if (cur && confirm(`Delete Rev ${cur.revision}? This permanently removes its BOQ, preliminaries and markups.`)) del.mutate(cur.id) }}>
                <Trash2 className="h-4 w-4" /> Delete
              </Button>
            )}
          </div>
        )}
      </Card>

      {currentId != null && <EstimateEditor key={currentId} estimateId={currentId} canEditMeta={canEditMeta} />}

      {copyOpen && currentId != null && (
        <CopyToProjectModal projectId={projectId} estimateId={currentId}
          onClose={() => setCopyOpen(false)}
          onDone={(targetId, name, rev) => { setCopyOpen(false); toast.success(`Copied to ${name} as Rev ${rev}`); router.push(`/projects/${targetId}`) }} />
      )}
    </div>
  )
}

function CopyToProjectModal({ projectId, estimateId, onClose, onDone }: {
  projectId: number; estimateId: number; onClose: () => void; onDone: (targetId: number, name: string, revision: number) => void
}) {
  const projects = useQuery({ queryKey: ["projects"], queryFn: () => fetchApi<Project[]>("/api/projects") })
  const [target, setTarget] = useState("")
  const [title, setTitle] = useState("")
  const copy = useMutation({
    mutationFn: () => fetchApi<EstimateSummary>(`/api/projects/${projectId}/estimates/${estimateId}/copy`, {
      method: "POST", body: JSON.stringify({ targetProjectId: Number(target), title: title.trim() || null }),
    }),
    onSuccess: (e) => { const t = projects.data?.find((p) => p.id === Number(target)); onDone(Number(target), t?.name ?? "project", e.revision) },
    onError: (er) => toast.error((er as Error).message),
  })
  const others = (projects.data ?? []).filter((p) => p.id !== projectId)
  return (
    <Modal open onClose={onClose} title="Copy estimate to another project">
      {projects.isLoading ? <p className="text-sm text-slate-400">Loading projects…</p>
        : others.length === 0 ? <p className="text-sm text-slate-500">No other projects you can access.</p>
        : (
          <div className="space-y-3">
            <Field label="Target project">
              <Select value={target} onChange={(e) => setTarget(e.target.value)}>
                <option value="">— choose a project —</option>
                {others.map((p) => <option key={p.id} value={p.id}>{p.code} · {p.name}</option>)}
              </Select>
            </Field>
            <Field label="Title (optional)"><Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="defaults to the source title" /></Field>
            <p className="text-xs text-slate-400">Copies the BOQ, preliminaries and markups as a new Draft revision in the target project.</p>
          </div>
        )}
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button disabled={!target || copy.isPending} onClick={() => copy.mutate()}>{copy.isPending ? "Copying…" : "Copy"}</Button>
      </div>
    </Modal>
  )
}

/** Which teams (Groups) may access this project. Admins can assign/remove. */
function TeamsPanel({ projectId }: { projectId: number }) {
  const qc = useQueryClient()
  const { user } = useAuth()
  const isAdmin = user?.role === "TenantAdmin" || user?.role === "SuperAdmin"

  const teams = useQuery({
    queryKey: ["project-teams", projectId],
    queryFn: () => fetchApi<ProjectTeam[]>(`/api/projects/${projectId}/teams`),
  })
  const groups = useQuery({
    queryKey: ["groups"],
    queryFn: () => fetchApi<GroupOption[]>("/api/projects/groups"),
    enabled: isAdmin,
  })

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ["project-teams", projectId] })
    qc.invalidateQueries({ queryKey: ["project", projectId] }) // updates the header "Teams" count
  }
  const assign = useMutation({
    mutationFn: (v: { groupId: number; isLead: boolean }) =>
      fetchApi(`/api/projects/${projectId}/teams`, { method: "POST", body: JSON.stringify(v) }),
    onSuccess: () => { refresh(); toast.success("Team assigned") },
    onError: (e) => toast.error((e as Error).message),
  })
  const remove = useMutation({
    mutationFn: (groupId: number) =>
      fetchApi(`/api/projects/${projectId}/teams/${groupId}`, { method: "DELETE" }),
    onSuccess: () => { refresh(); toast.success("Team removed") },
    onError: (e) => toast.error((e as Error).message),
  })

  if (teams.isLoading) return null
  const assigned = teams.data ?? []
  const assignedIds = new Set(assigned.map((t) => t.groupId))
  const available = (groups.data ?? []).filter((g) => !assignedIds.has(g.id))

  return (
    <Card className="p-4">
      <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-slate-700">
        <Users className="h-4 w-4 text-[var(--brand)]" /> Teams
      </div>

      {assigned.length === 0 && <p className="text-sm text-slate-400">No teams assigned yet.</p>}
      <div className="space-y-1">
        {assigned.map((t) => (
          <div key={t.groupId} className="flex items-center justify-between rounded-md border border-[var(--border)] px-3 py-2 text-sm">
            <div className="flex items-center gap-2">
              <span className="font-mono text-xs text-slate-400">{t.groupCode}</span>
              <span className="font-medium text-slate-700">{t.groupName}</span>
              {t.isLead && <Badge className="bg-[var(--brand)]/10 text-[var(--brand)]">Lead</Badge>}
            </div>
            {isAdmin && (
              <button
                onClick={() => { if (confirm(`Remove "${t.groupName}" from this project?`)) remove.mutate(t.groupId) }}
                className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600"
              >
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            )}
          </div>
        ))}
      </div>

      {isAdmin && <AssignTeam available={available} busy={assign.isPending} onAssign={(v) => assign.mutate(v)} />}
      {isAdmin && available.length === 0 && groups.data && (
        <p className="mt-2 text-xs text-slate-400">All teams are already assigned.</p>
      )}
    </Card>
  )
}

function AssignTeam({ available, busy, onAssign }: {
  available: GroupOption[]; busy: boolean; onAssign: (v: { groupId: number; isLead: boolean }) => void
}) {
  const [groupId, setGroupId] = useState("")
  const [isLead, setIsLead] = useState(false)
  if (available.length === 0) return null

  function submit() {
    if (groupId === "") return
    onAssign({ groupId: Number(groupId), isLead })
    setGroupId(""); setIsLead(false)
  }

  return (
    <div className="mt-3 grid grid-cols-[1fr_auto_auto] items-end gap-2 border-t border-[var(--border)] pt-3">
      <Field label="Assign team">
        <Select value={groupId} onChange={(e) => setGroupId(e.target.value)}>
          <option value="">— choose a team —</option>
          {available.map((g) => <option key={g.id} value={g.id}>{g.code} · {g.name}</option>)}
        </Select>
      </Field>
      <label className="flex items-center gap-1.5 pb-2 text-sm text-slate-600">
        <input type="checkbox" checked={isLead} onChange={(e) => setIsLead(e.target.checked)} /> Lead
      </label>
      <Button variant="outline" disabled={busy || groupId === ""} onClick={submit}>
        <Plus className="h-4 w-4" /> Assign
      </Button>
    </div>
  )
}

function EstimateEditor({ estimateId, canEditMeta }: { estimateId: number; canEditMeta: boolean }) {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const boqAdd = can("boq", "add"), boqEdit = can("boq", "edit"), boqDelete = can("boq", "delete")
  const plmView = can("prelims-markups", "view"), plmAdd = can("prelims-markups", "add")
  const plmEdit = can("prelims-markups", "edit"), plmDelete = can("prelims-markups", "delete")
  const canReports = can("reports", "view")
  const key = ["estimate", estimateId]
  const { data, isLoading, error } = useQuery({ queryKey: key, queryFn: () => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}`) })
  const assemblies = useQuery({ queryKey: ["assemblies"], queryFn: () => fetchApi<AssemblyRow[]>("/api/assemblies") })
  const costTypes = useQuery({ queryKey: ["cost-types"], queryFn: () => fetchApi<CostComponentType[]>("/api/cost-components") })
  const areas = useQuery({ queryKey: ["areas", data?.projectId], queryFn: () => fetchApi<Area[]>(`/api/projects/${data!.projectId}/areas`), enabled: data?.projectId != null })

  // Every estimate mutation returns the recomputed breakdown — push it into cache.
  const apply = (d: EstimateBreakdown) => qc.setQueryData(key, d)
  // Send the row version we last saw so the API can detect a concurrent edit (409).
  const ifMatch = (): Record<string, string> => {
    const v = qc.getQueryData<EstimateBreakdown>(key)?.rowVersion
    return v ? { "If-Match": v } : {}
  }
  function useEstimateMut(fn: (vars: any) => Promise<EstimateBreakdown>) {
    return useMutation({
      mutationFn: fn,
      onSuccess: (d) => { apply(d); qc.invalidateQueries({ queryKey: ["areas-rollup", estimateId] }) },
      onError: (e) => {
        if (e instanceof ApiError && e.status === 409) {
          toast.error("This estimate was changed by someone else — reloading the latest.")
          qc.invalidateQueries({ queryKey: key })
        } else {
          toast.error((e as Error).message)
        }
      },
    })
  }

  const addSection = useEstimateMut((v: { code: string; title: string }) =>
    fetchApi(`/api/estimates/${estimateId}/sections`, { method: "POST", headers: ifMatch(), body: JSON.stringify({ ...v, sortOrder: 0 }) }))
  const delSection = useEstimateMut((sid: number) => fetchApi(`/api/estimates/${estimateId}/sections/${sid}`, { method: "DELETE", headers: ifMatch() }))
  const addItem = useEstimateMut((v: any) => fetchApi(`/api/estimates/${estimateId}/sections/${v.sectionId}/items`, { method: "POST", headers: ifMatch(), body: JSON.stringify(v) }))
  const updItem = useEstimateMut((v: any) => fetchApi(`/api/estimates/${estimateId}/items/${v.id}`, { method: "PUT", headers: ifMatch(), body: JSON.stringify(v) }))
  const delItem = useEstimateMut((iid: number) => fetchApi(`/api/estimates/${estimateId}/items/${iid}`, { method: "DELETE", headers: ifMatch() }))
  const addPrelim = useEstimateMut((v: any) => fetchApi(`/api/estimates/${estimateId}/preliminaries`, { method: "POST", headers: ifMatch(), body: JSON.stringify(v) }))
  const delPrelim = useEstimateMut((pid: number) => fetchApi(`/api/estimates/${estimateId}/preliminaries/${pid}`, { method: "DELETE", headers: ifMatch() }))
  const addMarkup = useEstimateMut((v: any) => fetchApi(`/api/estimates/${estimateId}/markups`, { method: "POST", headers: ifMatch(), body: JSON.stringify(v) }))
  const delMarkup = useEstimateMut((mid: number) => fetchApi(`/api/estimates/${estimateId}/markups/${mid}`, { method: "DELETE", headers: ifMatch() }))
  const updateMeta = useEstimateMut((v: { title: string; status?: string; secondaryCurrency?: string }) => fetchApi(`/api/estimates/${estimateId}`, { method: "PUT", headers: ifMatch(), body: JSON.stringify(v) }))
  // Clone a room: new unit + its activities. Also refreshes the project areas list.
  const cloneRoom = useMutation({
    mutationFn: (v: { areaId: number; name: string }) =>
      fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/areas/${v.areaId}/clone`, { method: "POST", headers: ifMatch(), body: JSON.stringify({ name: v.name }) }),
    onSuccess: (d) => { apply(d); qc.invalidateQueries({ queryKey: ["areas", d.projectId] }); qc.invalidateQueries({ queryKey: ["areas-rollup", estimateId] }); toast.success("Room cloned") },
    onError: (e) => { if (e instanceof ApiError && e.status === 409) { toast.error("This estimate was changed or is locked — reloading."); qc.invalidateQueries({ queryKey: key }) } else toast.error((e as Error).message) },
  })
  const { data: fxRates } = useQuery({ queryKey: ["currencies"], queryFn: () => fetchApi<CurrencyRates>("/api/settings/currencies") })

  // Excel BOQ import (multipart upload → returns counts + recomputed breakdown).
  const fileRef = useRef<HTMLInputElement>(null)
  const importMut = useMutation({
    mutationFn: (f: File) => uploadFile<ImportResult>(`/api/estimates/${estimateId}/import`, f, ifMatch()),
    onSuccess: (r) => { apply(r.estimate); toast.success(`Imported ${r.sectionsAdded} section(s), ${r.itemsAdded} item(s)`) },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) { toast.error("This estimate was changed by someone else — reloading the latest."); qc.invalidateQueries({ queryKey: key }) }
      else toast.error((e as Error).message)
    },
  })
  async function downloadTemplate() {
    try { await downloadFile("/api/estimates/import-template.xlsx", "boq-import-template.xlsx") }
    catch (err) { toast.error((err as Error).message) }
  }

  if (isLoading) return <p className="text-slate-400">Loading estimate…</p>
  if (error) return <p className="text-rose-600">{(error as Error).message}</p>
  const e = data!
  const c = e.currency
  // A Published/Superseded revision is locked: content edits are blocked server-side,
  // so suppress the affordances here too (the status dropdown stays usable to revert).
  const locked = e.status === "Published" || e.status === "Superseded"
  const editAdd = boqAdd && !locked, editEdit = boqEdit && !locked, editDelete = boqDelete && !locked
  const editPlmAdd = plmAdd && !locked, editPlmDelete = plmDelete && !locked

  async function dl(kind: "xlsx" | "pdf" | "csv") {
    try {
      await downloadFile(`/api/estimates/${estimateId}/export.${kind}`, `estimate.${kind}`)
    } catch (err) {
      toast.error((err as Error).message)
    }
  }

  // Add an activity under a unit: ensure the auto "Activities" section exists, then
  // create a BOQ item tagged to that unit with the material/manpower build-up.
  async function addActivity(areaId: number, v: { description: string; unit: string; components: CompInput[] }) {
    try {
      let sectionId = e.sections.find((s) => s.code === "ACT")?.id
      if (sectionId == null) {
        const bd = await addSection.mutateAsync({ code: "ACT", title: "Activities" })
        sectionId = bd.sections.find((s) => s.code === "ACT")?.id
      }
      if (sectionId == null) return
      await addItem.mutateAsync({
        sectionId, description: v.description, unit: v.unit || "", quantity: 1,
        assemblyId: null, unitRate: 0, components: v.components, areaId, sortOrder: 0,
      })
    } catch { /* useEstimateMut already toasts errors / handles 409 */ }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <h3 className="text-sm font-semibold text-slate-600">{e.title} — Rev {e.revision}</h3>
          {canEditMeta
            ? <Select className="w-auto py-1 text-xs" value={e.status} onChange={(ev) => updateMeta.mutate({ title: e.title, status: ev.target.value })}>
                <option value="Draft">Draft</option>
                <option value="UnderReview">UnderReview</option>
                <option value="Published">Published</option>
                <option value="Superseded">Superseded</option>
              </Select>
            : <Badge className={statusColor(e.status)}>{e.status}</Badge>}
          {canEditMeta && fxRates && (() => {
            const codes = [fxRates.baseCurrency, ...fxRates.rates.map((r) => r.code)]
              .filter((x, i, arr) => arr.indexOf(x) === i && x.toUpperCase() !== e.currency.toUpperCase())
            return (
              <label className="flex items-center gap-1 text-xs text-slate-500">
                Show in
                <Select className="w-auto py-1 text-xs" value={e.fx?.secondaryCurrency ?? ""}
                        onChange={(ev) => updateMeta.mutate({ title: e.title, secondaryCurrency: ev.target.value })}>
                  <option value="">— none —</option>
                  {codes.map((x) => <option key={x} value={x}>{x}</option>)}
                </Select>
              </label>
            )
          })()}
        </div>
        {canReports && (
          <div className="flex gap-2">
            <Button variant="outline" className="h-8 text-xs" onClick={() => dl("xlsx")}><FileSpreadsheet className="h-4 w-4" /> Excel</Button>
            <Button variant="outline" className="h-8 text-xs" onClick={() => dl("csv")}><Table className="h-4 w-4" /> CSV</Button>
            <Button variant="outline" className="h-8 text-xs" onClick={() => dl("pdf")}><FileText className="h-4 w-4" /> PDF</Button>
          </div>
        )}
      </div>
      {locked && (
        <div className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
          This revision is <b>{e.status}</b> and locked. Set its status to <b>Draft</b> to edit the BOQ, preliminaries or markups.
        </div>
      )}
      <div className="grid gap-4 sm:grid-cols-4">
        <Stat label="Direct cost" value={money(e.directCost, c)} />
        <Stat label="Indirect (prelims)" value={money(e.indirectCost, c)} />
        <Stat label="Markups" value={money(e.markupCost, c)} />
        <Stat label="Bid price" value={money(e.bidPrice, c)} highlight />
      </div>
      {e.fx && (
        <p className="text-sm text-slate-500">
          ≈ <b className="text-slate-700">{money(e.fx.convertedBidPrice, e.fx.secondaryCurrency)}</b>
          {" "}· 1 {c} = {e.fx.rate.toLocaleString(undefined, { maximumFractionDigits: 6 })} {e.fx.secondaryCurrency}
          {" "}· {e.fx.frozen ? `frozen${e.fx.frozenAt ? " " + e.fx.frozenAt.slice(0, 10) : ""}` : "live rate"}
        </p>
      )}

      {/* BOQ */}
      <Card className="overflow-hidden">
        <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
          <span className="text-sm font-semibold">Bill of Quantities</span>
          {editAdd && (
            <div className="flex items-center gap-2">
              <input ref={fileRef} type="file" accept=".xlsx" className="hidden"
                onChange={(ev) => { const f = ev.target.files?.[0]; if (f) importMut.mutate(f); ev.target.value = "" }} />
              <Button variant="ghost" className="h-7 px-2 text-xs" onClick={downloadTemplate}><FileDown className="h-3.5 w-3.5" /> Template</Button>
              <Button variant="ghost" className="h-7 px-2 text-xs" disabled={importMut.isPending} onClick={() => fileRef.current?.click()}>
                <Upload className="h-3.5 w-3.5" /> {importMut.isPending ? "Importing…" : "Import"}
              </Button>
              <AddSection onAdd={(v) => addSection.mutate(v)} />
            </div>
          )}
        </div>
        {e.sections.map((s) => (
          <SectionBlock key={s.id} section={s} currency={c} assemblies={assemblies.data ?? []} costTypes={costTypes.data ?? []} areas={areas.data ?? []}
            canAdd={editAdd} canEdit={editEdit} canDelete={editDelete}
            onAddItem={(v) => addItem.mutate({ ...v, sectionId: s.id })}
            onUpdItem={(v) => updItem.mutate(v)}
            onDelItem={(iid) => delItem.mutate(iid)}
            onDelSection={() => { if (confirm(`Delete section "${s.title}"?`)) delSection.mutate(s.id) }} />
        ))}
        {!e.sections.length && <p className="px-4 py-3 text-sm text-slate-400">No sections yet — add one above.</p>}
      </Card>

      {(areas.data?.length ?? 0) > 0 && (
        <ActivitiesPanel breakdown={e} areas={areas.data ?? []} costTypes={costTypes.data ?? []} currency={c}
          canAdd={editAdd} canEdit={editEdit} canDelete={editDelete}
          onAddActivity={addActivity} onUpdItem={(v) => updItem.mutate(v)} onDelItem={(iid) => delItem.mutate(iid)}
          onCloneRoom={(areaId, name) => cloneRoom.mutate({ areaId, name })} />
      )}

      <AreaRollupPanel estimateId={estimateId} currency={c} />

      {/* Prelims + markups */}
      <div className="grid gap-4 sm:grid-cols-2">
        <Card className="p-4">
          <div className="mb-2 text-sm font-semibold">Preliminaries</div>
          {e.preliminaries.map((p) => (
            <Row key={p.id} left={`${p.description} (${p.kind})`} right={money(p.computedTotal, c)} onDelete={editPlmDelete ? () => delPrelim.mutate(p.id) : undefined} />
          ))}
          {editPlmAdd && <AddPrelim onAdd={(v) => addPrelim.mutate(v)} />}
        </Card>
        <Card className="p-4">
          <div className="mb-2 text-sm font-semibold">Markups</div>
          {e.markups.map((m) => (
            <Row key={m.id} left={`${m.type} (${m.percentage}%)`} right={money(m.computedAmount, c)} onDelete={editPlmDelete ? () => delMarkup.mutate(m.id) : undefined} />
          ))}
          {editPlmAdd && <AddMarkup onAdd={(v) => addMarkup.mutate(v)} />}
        </Card>
      </div>

      {plmView && e.markups.length > 0 && (
        <WhatIfPanel key={e.markups.map((m) => m.id).join(",")}
          estimateId={estimateId} markups={e.markups} currency={c} canEdit={plmEdit && !locked} />
      )}
    </div>
  )
}

/** Non-persisting margin preview: tweak markup %s and see the bid price move. */
function WhatIfPanel({ estimateId, markups, currency, canEdit }: {
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

  // Re-price whenever a percentage changes.
  useEffect(() => {
    preview.mutate({ markups: lines.map((l) => ({ type: l.type, label: l.label, percentage: l.percentage, applyOrder: l.applyOrder })) })
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

function SectionBlock({ section, currency, assemblies, costTypes, areas, canAdd, canEdit, canDelete, onAddItem, onUpdItem, onDelItem, onDelSection }: {
  section: SectionBreakdown; currency: string; assemblies: AssemblyRow[]; costTypes: CostComponentType[]; areas: Area[]
  canAdd: boolean; canEdit: boolean; canDelete: boolean
  onAddItem: (v: any) => void; onUpdItem: (v: any) => void; onDelItem: (iid: number) => void; onDelSection: () => void
}) {
  return (
    <div className="border-t border-[var(--border)]">
      <div className="flex items-center justify-between bg-slate-50/60 px-4 py-2">
        <span className="font-semibold text-slate-700">{section.code} {section.title}</span>
        <div className="flex items-center gap-3">
          <span className="font-semibold">{money(section.sectionTotal, currency)}</span>
          {canDelete && <button onClick={onDelSection} className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
        </div>
      </div>
      <table className="w-full text-sm">
        <tbody>
          {section.items.map((i) => (
            <ItemRow key={i.id} item={i} currency={currency} costTypes={costTypes} areas={areas} canEdit={canEdit} canDelete={canDelete} onUpd={onUpdItem} onDel={() => onDelItem(i.id)} />
          ))}
        </tbody>
      </table>
      {canAdd && <AddItemForm assemblies={assemblies} costTypes={costTypes} areas={areas} onAdd={onAddItem} />}
    </div>
  )
}

function ItemRow({ item, currency, costTypes, areas, canEdit, canDelete, onUpd, onDel }: { item: ItemBreakdown; currency: string; costTypes: CostComponentType[]; areas: Area[]; canEdit: boolean; canDelete: boolean; onUpd: (v: any) => void; onDel: () => void }) {
  const adHoc = item.assemblyId == null
  const hasComps = item.components.length > 0
  const [buildup, setBuildup] = useState(false)
  // IMPORTANT: carry the current components so quantity/area edits don't wipe the build-up
  // (the PUT replaces components wholesale).
  const base = {
    id: item.id, itemCode: item.itemCode, description: item.description, unit: item.unit,
    assemblyId: item.assemblyId, unitRate: item.unitRate, sortOrder: item.sortOrder, areaId: item.areaId,
    components: hasComps ? item.components.map((c) => ({ typeId: c.typeId, value: c.value, quantity: c.quantity ?? undefined, rate: c.rate ?? undefined })) : undefined,
  }
  const areaName = areas.find((a) => a.id === item.areaId)?.name
  return (
    <tr className="border-t border-[var(--border)]">
      <td className="px-4 py-1.5">
        {item.description}
        {hasComps && (
          <div className="text-xs text-slate-400">
            {item.components.map((c) => `${c.code} ${c.calcKind === "Percent" ? c.value + "%" : (c.quantity != null && c.rate != null ? `${c.quantity}×${c.rate}` : c.value)}`).join(" + ")}
          </div>
        )}
        {canEdit && areas.length > 0
          ? <select value={item.areaId ?? ""} onChange={(ev) => onUpd({ ...base, quantity: item.quantity, areaId: ev.target.value === "" ? null : Number(ev.target.value) })}
                    className="mt-1 rounded border border-[var(--border)] bg-white px-1 py-0.5 text-xs text-slate-500">
              <option value="">— no area —</option>
              {areas.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
            </select>
          : areaName && <div className="text-xs text-slate-400">📍 {areaName}</div>}
      </td>
      <td className="px-2 py-1.5 text-slate-500">{item.unit}</td>
      <td className="px-2 py-1.5 text-right">
        {canEdit
          ? <Input className="w-20 py-1 text-right" type="number" step="0.0001" defaultValue={item.quantity}
              onBlur={(ev) => { const q = Number(ev.target.value); if (q !== item.quantity) onUpd({ ...base, quantity: q }) }} />
          : <span className="text-slate-600">{item.quantity}</span>}
      </td>
      <td className="px-2 py-1.5 text-right">
        {adHoc && canEdit && !hasComps
          ? <Input className="w-24 py-1 text-right" type="number" step="0.0001" defaultValue={item.unitRate}
              onBlur={(ev) => { const r = Number(ev.target.value); if (r !== item.unitRate) onUpd({ ...base, quantity: item.quantity, unitRate: r }) }} />
          : <span className="text-slate-600">{money(item.unitRate, currency)}</span>}
      </td>
      <td className="px-4 py-1.5 text-right font-medium">{money(item.lineTotal, currency)}</td>
      <td className="px-2 py-1.5 text-right">
        <div className="flex items-center justify-end gap-1">
          {adHoc && canEdit && (
            <button onClick={() => setBuildup(true)} title="Unit-rate build-up"
                    className={cn("rounded p-1 hover:bg-slate-100", hasComps ? "text-[var(--brand)]" : "text-slate-400")}>
              <Layers className="h-3.5 w-3.5" />
            </button>
          )}
          {canDelete && <button onClick={onDel} className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
        </div>
      </td>
      {buildup && (
        <BuildUpModal open={buildup} onClose={() => setBuildup(false)} costTypes={costTypes} currency={currency}
          initial={item.components.map((c) => ({ typeId: c.typeId, value: c.value, quantity: c.quantity ?? undefined, rate: c.rate ?? undefined }))}
          onSave={(comps) => onUpd({ ...base, quantity: item.quantity, components: comps })} />
      )}
    </tr>
  )
}

function AddItemForm({ assemblies, costTypes, areas, onAdd }: { assemblies: AssemblyRow[]; costTypes: CostComponentType[]; areas: Area[]; onAdd: (v: any) => void }) {
  const BUILDUP = "__buildup__"
  const [f, setF] = useState({ description: "", unit: "", quantity: "1", assemblyId: "", unitRate: "0", areaId: "" })
  const [comps, setComps] = useState<CompInput[]>([])
  const [modal, setModal] = useState(false)
  const set = (k: string) => (e: any) => setF({ ...f, [k]: e.target.value })
  const mode = f.assemblyId   // "" ad-hoc | BUILDUP | assembly id
  const useAsm = mode !== "" && mode !== BUILDUP
  const useBuildup = mode === BUILDUP
  function submit() {
    if (!f.description) return
    onAdd({
      description: f.description, unit: f.unit, quantity: Number(f.quantity || 0),
      assemblyId: useAsm ? Number(f.assemblyId) : null,
      unitRate: useAsm || useBuildup ? 0 : Number(f.unitRate || 0),
      components: useBuildup ? comps : undefined,
      areaId: f.areaId === "" ? null : Number(f.areaId), sortOrder: 0,
    })
    setF({ description: "", unit: "", quantity: "1", assemblyId: "", unitRate: "0", areaId: f.areaId }); setComps([])
  }
  return (
    <div className="grid grid-cols-[1fr_60px_60px_1fr_90px_110px_auto] items-end gap-2 bg-slate-50 px-4 py-3 text-sm">
      <Field label="Description"><Input value={f.description} onChange={set("description")} /></Field>
      <Field label="Unit"><Input value={f.unit} onChange={set("unit")} /></Field>
      <Field label="Qty"><Input type="number" step="0.0001" value={f.quantity} onChange={set("quantity")} /></Field>
      <Field label="Pricing">
        <Select value={f.assemblyId} onChange={set("assemblyId")}>
          <option value="">— ad-hoc rate —</option>
          <option value={BUILDUP}>— cost build-up —</option>
          {assemblies.map((a) => <option key={a.id} value={a.id}>{a.code} ({money(a.computedRate)})</option>)}
        </Select>
      </Field>
      {useBuildup
        ? <Field label="Build-up"><Button variant="outline" className="h-9 w-full text-xs" onClick={() => setModal(true)}><Layers className="h-3.5 w-3.5" /> {comps.length ? `${comps.length} parts` : "Define"}</Button></Field>
        : <Field label="Rate"><Input type="number" step="0.0001" value={f.unitRate} onChange={set("unitRate")} disabled={useAsm} /></Field>}
      <Field label="Area">
        <Select value={f.areaId} onChange={set("areaId")}>
          <option value="">— none —</option>
          {areas.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
        </Select>
      </Field>
      <Button variant="outline" onClick={submit}><Plus className="h-4 w-4" /></Button>
      {modal && (
        <BuildUpModal open={modal} onClose={() => setModal(false)} costTypes={costTypes} currency=""
          initial={comps} onSave={(c) => setComps(c)} />
      )}
    </div>
  )
}

/** A cost-component line as sent to the API: Amount types as quantity × rate
 *  (material qty×price, manpower hours×rate); Percent types as a value (%). */
type CompInput = { typeId: number; value?: number; quantity?: number; rate?: number }

/** Edit a BOQ item / activity's unit-rate build-up. Amount components are entered as
 *  quantity × rate (e.g. Material 100 × 50, Manpower 40 × 50); Percent components (Waste,
 *  Overheads) apply to the amount subtotal. Live rate = Σ(qty×rate) × (1 + Σ%). */
function BuildUpModal({ open, onClose, costTypes, currency, initial, onSave }: {
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

/** Project area breakdown: a nested Area → Sub-area → Unit tree (add/edit/delete). */
function AreasPanel({ projectId }: { projectId: number }) {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canAdd = can("projects", "add"), canEdit = can("projects", "edit"), canDelete = can("projects", "delete")
  const { data: areas } = useQuery({ queryKey: ["areas", projectId], queryFn: () => fetchApi<Area[]>(`/api/projects/${projectId}/areas`) })
  const [modal, setModal] = useState<{ parentAreaId: number | null; area?: Area } | null>(null)
  const inval = () => qc.invalidateQueries({ queryKey: ["areas", projectId] })
  const del = useMutation({
    mutationFn: (aid: number) => fetchApi(`/api/projects/${projectId}/areas/${aid}`, { method: "DELETE" }),
    onSuccess: () => { inval(); toast.success("Area deleted") }, onError: (e) => toast.error((e as Error).message),
  })
  const childrenOf = (id: number | null) => (areas ?? []).filter((a) => a.parentAreaId === id)

  function Node({ area, depth }: { area: Area; depth: number }) {
    return (
      <>
        <div className="flex items-center justify-between rounded py-1 pr-2 hover:bg-slate-50" style={{ paddingLeft: depth * 18 + 4 }}>
          <span className="text-sm">
            {area.code && <span className="mr-1 font-mono text-xs text-slate-400">{area.code}</span>}
            {area.name}<span className="ml-2 text-xs text-slate-400">{area.kind}{area.quantity > 0 ? ` · ${area.quantity}${area.unit ? ` ${area.unit}` : ""}` : ""}</span>
          </span>
          <div className="flex gap-1">
            {canAdd && <button onClick={() => setModal({ parentAreaId: area.id })} className="rounded p-1 text-slate-400 hover:text-[var(--brand)]" title="Add sub-area"><Plus className="h-3.5 w-3.5" /></button>}
            {canEdit && <button onClick={() => setModal({ parentAreaId: area.parentAreaId, area })} className="rounded p-1 text-slate-400 hover:text-slate-700" title="Edit"><Pencil className="h-3.5 w-3.5" /></button>}
            {canDelete && <button onClick={() => { if (confirm(`Delete area "${area.name}"?`)) del.mutate(area.id) }} className="rounded p-1 text-slate-400 hover:text-rose-600" title="Delete"><Trash2 className="h-3.5 w-3.5" /></button>}
          </div>
        </div>
        {childrenOf(area.id).map((k) => <Node key={k.id} area={k} depth={depth + 1} />)}
      </>
    )
  }

  return (
    <Card className="p-4">
      <div className="mb-2 flex items-center justify-between">
        <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600"><FolderTree className="h-4 w-4" /> Areas</h3>
        {canAdd && <Button variant="outline" className="h-8 text-xs" onClick={() => setModal({ parentAreaId: null })}><Plus className="h-4 w-4" /> Area</Button>}
      </div>
      {(areas?.length ?? 0) === 0
        ? <p className="text-xs text-slate-400">No areas yet. Break the project into areas, sub-areas and units — BOQ items tagged to them roll up by location.</p>
        : <div>{childrenOf(null).map((r) => <Node key={r.id} area={r} depth={0} />)}</div>}
      {modal && <AreaModal projectId={projectId} parentAreaId={modal.parentAreaId} area={modal.area} onClose={() => setModal(null)} onSaved={() => { inval(); setModal(null) }} />}
    </Card>
  )
}

function AreaModal({ projectId, parentAreaId, area, onClose, onSaved }: { projectId: number; parentAreaId: number | null; area?: Area; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(area?.name ?? "")
  const [code, setCode] = useState(area?.code ?? "")
  const [kind, setKind] = useState(area?.kind ?? (parentAreaId == null ? "Area" : "SubArea"))
  const [quantity, setQuantity] = useState(String(area?.quantity ?? ""))
  const [unit, setUnit] = useState(area?.unit ?? "")
  const [busy, setBusy] = useState(false)
  async function save() {
    if (!name.trim()) { toast.error("Name is required"); return }
    const qty = Number(quantity || 0)
    if (qty < 0 || Number.isNaN(qty)) { toast.error("Quantity must be zero or more"); return }
    setBusy(true)
    try {
      const body = JSON.stringify({ name: name.trim(), code: code.trim() || null, kind, parentAreaId: area ? area.parentAreaId : parentAreaId, sortOrder: area?.sortOrder ?? 0, quantity: qty, unit: unit.trim() || null })
      if (area) await fetchApi(`/api/projects/${projectId}/areas/${area.id}`, { method: "PUT", body })
      else await fetchApi(`/api/projects/${projectId}/areas`, { method: "POST", body })
      toast.success(area ? "Area saved" : "Area added"); onSaved()
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }
  return (
    <Modal open onClose={onClose} title={area ? "Edit area" : "New area"}>
      <div className="space-y-3">
        <Field label="Name *"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Building A" /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Code"><Input value={code} onChange={(e) => setCode(e.target.value)} placeholder="BLK-A" /></Field>
          <Field label="Level"><Select value={kind} onChange={(e) => setKind(e.target.value)}><option value="Area">Area</option><option value="SubArea">Sub-area</option><option value="Unit">Unit</option></Select></Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Quantity"><Input type="number" step="0.0001" min={0} value={quantity} onChange={(e) => setQuantity(e.target.value)} placeholder="e.g. 120" /></Field>
          <Field label="Measure unit"><Input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="m², unit, key…" maxLength={16} /></Field>
        </div>
        <p className="text-xs text-slate-400">Optional. Used to report cost per unit/m² on the area roll-up — it never changes the bid.</p>
      </div>
      <div className="mt-4 flex justify-end gap-2"><Button variant="outline" onClick={onClose}>Cancel</Button><Button disabled={busy} onClick={save}>{busy ? "Saving…" : "Save"}</Button></div>
    </Modal>
  )
}

/** Per-estimate cost roll-up: item line totals escalated up the area tree. */
function AreaRollupPanel({ estimateId, currency }: { estimateId: number; currency: string }) {
  const { data } = useQuery({ queryKey: ["areas-rollup", estimateId], queryFn: () => fetchApi<AreaRollup>(`/api/estimates/${estimateId}/areas-rollup`) })
  if (!data || data.areas.length === 0) return null
  const childrenOf = (id: number | null) => data.areas.filter((a) => a.parentAreaId === id)
  function Row({ a, depth }: { a: AreaRollupRow; depth: number }) {
    return (
      <>
        <div className="flex items-center justify-between py-1 text-sm" style={{ paddingLeft: depth * 18 }}>
          <span>{a.name} <span className="text-xs text-slate-400">{a.kind}{a.itemCount ? ` · ${a.itemCount} item${a.itemCount > 1 ? "s" : ""}` : ""}{a.quantity > 0 ? ` · ${a.quantity}${a.unit ? ` ${a.unit}` : ""}` : ""}</span></span>
          <span className="text-right">
            <span className="font-medium">{money(a.rollupTotal, currency)}</span>
            {a.costPerUnit != null && <span className="ml-2 text-xs text-slate-400">{money(a.costPerUnit, currency)}/{a.unit || "unit"}</span>}
          </span>
        </div>
        {childrenOf(a.id).map((k) => <Row key={k.id} a={k} depth={depth + 1} />)}
      </>
    )
  }
  return (
    <Card className="p-4">
      <h4 className="mb-2 flex items-center gap-2 text-sm font-semibold text-slate-600"><FolderTree className="h-4 w-4" /> Cost by area</h4>
      {childrenOf(null).map((r) => <Row key={r.id} a={r} depth={0} />)}
      <div className="mt-2 flex items-center justify-between border-t border-[var(--border)] pt-2 text-sm">
        <span className="text-slate-500">Assigned to areas{data.unassignedTotal > 0 ? ` · unassigned ${money(data.unassignedTotal, currency)}` : ""}</span>
        <span className="font-semibold">{money(data.assignedTotal, currency)}</span>
      </div>
    </Card>
  )
}

/** Unit-centric activities: the area tree with each unit's activities (BOQ items
 *  tagged to it) showing Material (M) + Manpower (L) + total, with add/edit/delete.
 *  Reuses BOQ items + the cost build-up, so everything flows into the bid. */
function ActivitiesPanel({ breakdown, areas, costTypes, currency, canAdd, canEdit, canDelete, onAddActivity, onUpdItem, onDelItem, onCloneRoom }: {
  breakdown: EstimateBreakdown; areas: Area[]; costTypes: CostComponentType[]; currency: string
  canAdd: boolean; canEdit: boolean; canDelete: boolean
  onAddActivity: (areaId: number, v: { description: string; unit: string; components: CompInput[] }) => void
  onUpdItem: (v: any) => void; onDelItem: (iid: number) => void
  onCloneRoom: (areaId: number, name: string) => void
}) {
  const items = breakdown.sections.flatMap((s) => s.items)
  const byArea = (aid: number) => items.filter((it) => it.areaId === aid)
  const childrenOf = (id: number | null) => areas.filter((a) => a.parentAreaId === id)
  const [adding, setAdding] = useState<Area | null>(null)
  const [editing, setEditing] = useState<ItemBreakdown | null>(null)
  const [cloning, setCloning] = useState<Area | null>(null)
  const compAmount = (it: ItemBreakdown, code: string) => it.components.find((c) => c.code === code)?.amount ?? 0
  const editBase = (it: ItemBreakdown) => ({
    id: it.id, itemCode: it.itemCode, description: it.description, unit: it.unit, assemblyId: it.assemblyId,
    unitRate: it.unitRate, sortOrder: it.sortOrder, areaId: it.areaId, quantity: it.quantity,
  })

  function Node({ area, depth }: { area: Area; depth: number }) {
    const acts = byArea(area.id)
    return (
      <>
        <div className="flex items-center justify-between border-t border-[var(--border)] py-1.5" style={{ paddingLeft: depth * 16 + 4 }}>
          <span className="text-sm">
            {area.code && <span className="mr-1 font-mono text-xs text-slate-400">{area.code}</span>}
            {area.name}<span className="ml-2 text-xs text-slate-400">{area.kind}</span>
          </span>
          <div className="flex items-center gap-1">
            {canAdd && area.kind === "Unit" && <Button variant="ghost" className="h-6 px-2 text-xs" onClick={() => setCloning(area)}><Copy className="h-3.5 w-3.5" /> Clone</Button>}
            {canAdd && <Button variant="ghost" className="h-6 px-2 text-xs" onClick={() => setAdding(area)}><Plus className="h-3.5 w-3.5" /> Activity</Button>}
          </div>
        </div>
        {acts.map((it) => (
          <div key={it.id} className="grid grid-cols-[1fr_96px_96px_100px_auto] items-center gap-2 py-1 text-sm" style={{ paddingLeft: depth * 16 + 22 }}>
            <span className="text-slate-700">{it.description}</span>
            <span className="text-right text-xs text-slate-500" title="Material">M {money(compAmount(it, "MAT"), currency)}</span>
            <span className="text-right text-xs text-slate-500" title="Manpower">L {money(compAmount(it, "LAB"), currency)}</span>
            <span className="text-right font-medium">{money(it.lineTotal, currency)}</span>
            <span className="flex justify-end gap-1">
              {canEdit && <button onClick={() => setEditing(it)} className="rounded p-1 text-slate-400 hover:text-[var(--brand)]" title="Material & manpower"><Layers className="h-3.5 w-3.5" /></button>}
              {canDelete && <button onClick={() => { if (confirm(`Delete activity "${it.description}"?`)) onDelItem(it.id) }} className="rounded p-1 text-slate-400 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
            </span>
          </div>
        ))}
        {childrenOf(area.id).map((k) => <Node key={k.id} area={k} depth={depth + 1} />)}
      </>
    )
  }

  return (
    <Card className="p-4">
      <h4 className="mb-1 flex items-center gap-2 text-sm font-semibold text-slate-600"><FolderTree className="h-4 w-4" /> Activities by unit</h4>
      <p className="mb-2 text-xs text-slate-400">Add work activities under each unit; each carries Material (qty × price) and Manpower (hours × rate). M = material, L = manpower; total includes any other components.</p>
      {childrenOf(null).map((r) => <Node key={r.id} area={r} depth={0} />)}
      {adding && (
        <ActivityModal area={adding} costTypes={costTypes} currency={currency}
          onClose={() => setAdding(null)}
          onSave={(v) => { onAddActivity(adding.id, v); setAdding(null) }} />
      )}
      {editing && (
        <BuildUpModal open onClose={() => setEditing(null)} costTypes={costTypes} currency={currency}
          initial={editing.components.map((c) => ({ typeId: c.typeId, value: c.value, quantity: c.quantity ?? undefined, rate: c.rate ?? undefined }))}
          onSave={(comps) => { onUpdItem({ ...editBase(editing), components: comps }); setEditing(null) }} />
      )}
      {cloning && (
        <CloneRoomModal area={cloning} onClose={() => setCloning(null)}
          onSave={(name) => { onCloneRoom(cloning.id, name); setCloning(null) }} />
      )}
    </Card>
  )
}

/** Clone a room: duplicates the unit and all its activities under a new name. */
function CloneRoomModal({ area, onClose, onSave }: { area: Area; onClose: () => void; onSave: (name: string) => void }) {
  const [name, setName] = useState(`${area.name} (copy)`)
  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { toast.error("Name is required"); return }
    onSave(name.trim())
  }
  return (
    <Modal open onClose={onClose} title={`Clone room — ${area.name}`}>
      <form id="clone-room-form" onSubmit={submit} className="space-y-3">
        <p className="text-xs text-slate-500">Creates a new {area.kind} with the same activities (material &amp; manpower). You can edit the copy independently afterwards.</p>
        <Field label="New room name"><Input value={name} onChange={(e) => setName(e.target.value)} autoFocus /></Field>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="clone-room-form">Clone</Button>
      </div>
    </Modal>
  )
}

/** New-activity dialog: pick from the activity catalog (built-in + user-added, with
 *  an inline "+ New") then define the Material/Manpower build-up (qty × rate). */
function ActivityModal({ area, costTypes, currency, onClose, onSave }: {
  area: Area; costTypes: CostComponentType[]; currency: string
  onClose: () => void; onSave: (v: { description: string; unit: string; components: CompInput[] }) => void
}) {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canAddActivity = can("boq", "add")
  const { data: activities } = useQuery({ queryKey: ["activities"], queryFn: () => fetchApi<ActivityType[]>("/api/activities") })
  const active = (activities ?? []).filter((a) => a.isActive)

  const [name, setName] = useState("")
  const [unit, setUnit] = useState("")
  const [components, setComponents] = useState<CompInput[]>([])
  const [buildup, setBuildup] = useState(false)
  const [showNew, setShowNew] = useState(false)
  const [newName, setNewName] = useState("")
  const [adding, setAdding] = useState(false)

  async function addCatalogActivity() {
    const nm = newName.trim()
    if (!nm) { toast.error("Activity name required"); return }
    setAdding(true)
    try {
      await fetchApi("/api/activities", { method: "POST", body: JSON.stringify({ name: nm, sortOrder: 0, isActive: true }) })
      await qc.invalidateQueries({ queryKey: ["activities"] })
      setName(nm); setNewName(""); setShowNew(false)
      toast.success(`Added "${nm}"`)
    } catch (e) { toast.error((e as Error).message) } finally { setAdding(false) }
  }

  function submit(ev: React.FormEvent) {
    ev.preventDefault()
    if (!name.trim()) { toast.error("Choose an activity"); return }
    onSave({ description: name.trim(), unit, components })
  }

  return (
    <Modal open onClose={onClose} title={`New activity — ${area.name}`}>
      <form id="activity-form" onSubmit={submit} className="space-y-3">
        <div>
          <span className="mb-1 block text-xs font-medium text-slate-600">Activity</span>
          <div className="flex gap-2">
            <Select value={name} onChange={(e) => setName(e.target.value)} className="flex-1">
              <option value="">— choose activity —</option>
              {active.map((a) => <option key={a.id} value={a.name}>{a.name}</option>)}
            </Select>
            {canAddActivity && (
              <Button type="button" variant="outline" className="h-9 whitespace-nowrap text-xs" onClick={() => setShowNew((s) => !s)}>
                <Plus className="h-3.5 w-3.5" /> New
              </Button>
            )}
          </div>
          {showNew && (
            <div className="mt-2 flex gap-2">
              <Input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="New activity name" autoFocus
                     onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addCatalogActivity() } }} />
              <Button type="button" variant="outline" className="h-9 text-xs" disabled={adding} onClick={addCatalogActivity}>Add</Button>
            </div>
          )}
        </div>
        <Field label="Unit (optional)"><Input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="m², no, ls" /></Field>
        <div>
          <span className="mb-1 block text-xs font-medium text-slate-600">Material &amp; manpower</span>
          <Button type="button" variant="outline" className="w-full text-xs" onClick={() => setBuildup(true)}>
            <Layers className="h-3.5 w-3.5" /> {components.length ? `${components.length} cost line(s) — edit` : "Add material & manpower"}
          </Button>
        </div>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="activity-form">Add activity</Button>
      </div>
      {buildup && (
        <BuildUpModal open onClose={() => setBuildup(false)} costTypes={costTypes} currency={currency}
          initial={components} onSave={(c) => setComponents(c)} />
      )}
    </Modal>
  )
}

function AddSection({ onAdd }: { onAdd: (v: { code: string; title: string }) => void }) {
  const [open, setOpen] = useState(false)
  const [code, setCode] = useState("")
  const [title, setTitle] = useState("")
  if (!open) return <Button variant="ghost" className="h-7 px-2 text-xs" onClick={() => setOpen(true)}><Plus className="h-3.5 w-3.5" /> Section</Button>
  return (
    <div className="flex items-center gap-2">
      <Input className="w-20 py-1" placeholder="Code" value={code} onChange={(e) => setCode(e.target.value)} />
      <Input className="w-48 py-1" placeholder="Title" value={title} onChange={(e) => setTitle(e.target.value)} />
      <Button className="h-8 px-3 text-xs" onClick={() => { if (title) { onAdd({ code, title }); setCode(""); setTitle(""); setOpen(false) } }}>Add</Button>
    </div>
  )
}

function AddPrelim({ onAdd }: { onAdd: (v: any) => void }) {
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

function AddMarkup({ onAdd }: { onAdd: (v: any) => void }) {
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

function Stat({ label, value, highlight }: { label: string; value: string; highlight?: boolean }) {
  return (
    <Card className={highlight ? "p-4 ring-2 ring-[var(--brand)]" : "p-4"}>
      <div className="text-xs text-slate-500">{label}</div>
      <div className={highlight ? "text-lg font-bold text-[var(--brand)]" : "text-lg font-semibold"}>{value}</div>
    </Card>
  )
}

function Row({ left, right, onDelete }: { left: string; right: string; onDelete?: () => void }) {
  return (
    <div className="flex items-center justify-between border-t border-[var(--border)] py-1.5 text-sm first:border-0">
      <span className="text-slate-600">{left}</span>
      <div className="flex items-center gap-2">
        <span className="font-medium">{right}</span>
        {onDelete && <button onClick={onDelete} className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3 w-3" /></button>}
      </div>
    </div>
  )
}
