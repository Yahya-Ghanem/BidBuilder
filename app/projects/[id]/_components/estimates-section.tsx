"use client"
import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useRouter } from "next/navigation"
import { toast } from "sonner"
import { Copy, FolderInput, Plus, Trash2 } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { usePermissions } from "@/lib/permissions"
import type { EstimateSummary, Project } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"
import { Field, Modal, Select } from "@/components/form"
import { money } from "@/lib/utils"
import { EstimateEditor } from "./estimate-editor"

/** Estimate revision picker: pick a revision, create blank / duplicate, view & edit it.
 *  The displayed revision is whatever the user picked OR the latest if none. */
export function EstimatesSection({ projectId }: { projectId: number }) {
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
          : <p className="text-xs text-slate-400">You don&apos;t have permission to create estimates.</p>}
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
