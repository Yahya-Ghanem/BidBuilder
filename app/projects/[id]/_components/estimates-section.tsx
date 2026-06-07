"use client"
import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useRouter } from "next/navigation"
import { toast } from "sonner"
import { Copy, FileStack, FolderInput, Plus, Save, Trash2 } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { usePermissions } from "@/lib/permissions"
import type { EstimateSummary, EstimateTemplate, Project } from "@/lib/types"
import { Card, Button, DropdownButton, Input, type DropdownItem } from "@/components/ui"
import { Field, Modal, Select, Textarea } from "@/components/form"
import { money } from "@/lib/utils"
import { useT } from "@/lib/i18n"
import { EstimateEditor } from "./estimate-editor"

/** Estimate revision picker: pick a revision, create blank / duplicate, view & edit it.
 *  The displayed revision is whatever the user picked OR the latest if none.
 *
 *  25.4 — Six lifecycle buttons (New / Duplicate / Copy to… / Save as template /
 *  From template / Delete) used to wrap to a second row on a 1366-px viewport.
 *  Now they live behind a single "Actions ▾" dropdown alongside the revision
 *  picker, dropping the visible-control count from 7 to 2. The Delete entry is
 *  rendered in destructive color and confirms via the existing `confirm()`
 *  guard so a mis-click can't silently destroy a revision. */
export function EstimatesSection({ projectId }: { projectId: number }) {
  const t = useT()
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canManage = can("estimate-admin", "add")
  const canEditMeta = can("estimate-admin", "edit")
  const canDelete = can("estimate-admin", "delete")
  const estimates = useQuery({ queryKey: ["estimates", projectId], queryFn: () => fetchApi<EstimateSummary[]>(`/api/projects/${projectId}/estimates`) })
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [copyOpen, setCopyOpen] = useState(false)
  const [saveTplOpen, setSaveTplOpen] = useState(false)
  const [fromTplOpen, setFromTplOpen] = useState(false)
  const router = useRouter()

  const list = estimates.data ?? []
  const currentId = selectedId != null && list.some((e) => e.id === selectedId) ? selectedId : list[list.length - 1]?.id ?? null
  // 25.3 — Compare moved into the Insights tab (rendered by <EstimateEditor>),
  // so the revision-bar toggle is no longer needed here.

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
  // 21.3 — spin up a new revision from a saved template.
  const fromTemplate = useMutation({
    mutationFn: (body: { templateId: number; title: string | null }) => fetchApi<EstimateSummary>(`/api/projects/${projectId}/estimates/from-template`, { method: "POST", body: JSON.stringify(body) }),
    onSuccess: (e) => { qc.invalidateQueries({ queryKey: ["estimates", projectId] }); qc.invalidateQueries({ queryKey: ["project", projectId] }); setSelectedId(e.id); setFromTplOpen(false); toast.success(`Revision ${e.revision} created from template`) },
    onError: (er) => toast.error((er as Error).message),
  })

  if (estimates.isLoading) return <p className="text-slate-400">Loading estimates…</p>

  if (!list.length) {
    return (
      <Card className="p-6 text-center">
        <p className="mb-3 text-sm text-slate-500">No estimate yet for this project.</p>
        {canManage
          ? <div className="flex flex-wrap justify-center gap-2">
              <Button onClick={() => createBlank.mutate("Base Estimate")} disabled={createBlank.isPending}><Plus className="h-4 w-4" /> Create first estimate</Button>
              <Button variant="outline" onClick={() => setFromTplOpen(true)}><FileStack className="h-4 w-4" /> Start from template</Button>
            </div>
          : <p className="text-xs text-slate-400">You don&apos;t have permission to create estimates.</p>}
        {fromTplOpen && <FromTemplateModal pending={fromTemplate.isPending} onClose={() => setFromTplOpen(false)} onPick={(templateId, title) => fromTemplate.mutate({ templateId, title })} />}
      </Card>
    )
  }

  // 25.4 — Lifecycle actions collapsed into a single Actions ▾ dropdown so the
  // revision strip fits in one row on a 1366-px viewport. Each entry mirrors
  // the old button's disabled state; Delete keeps its destructive coloring and
  // confirm() guard so a mis-click can't silently destroy a revision.
  const actionItems: DropdownItem[] = []
  if (canManage) {
    actionItems.push({
      key: "new",
      label: <><Plus className="h-4 w-4" /> {t("rev.new")}</>,
      disabled: createBlank.isPending,
      onSelect: () => createBlank.mutate(`Revision ${(list[list.length - 1]?.revision ?? 0) + 1}`),
    })
    actionItems.push({
      key: "duplicate",
      label: <><Copy className="h-4 w-4" /> {t("rev.duplicate")}</>,
      disabled: clone.isPending || currentId == null,
      onSelect: () => { if (currentId != null) clone.mutate(currentId) },
    })
    actionItems.push({
      key: "copyTo",
      label: <><FolderInput className="h-4 w-4" /> {t("rev.copyTo")}</>,
      disabled: currentId == null,
      onSelect: () => setCopyOpen(true),
    })
    actionItems.push({
      key: "saveAsTemplate",
      label: <><Save className="h-4 w-4" /> {t("rev.saveAsTemplate")}</>,
      disabled: currentId == null,
      onSelect: () => setSaveTplOpen(true),
    })
    actionItems.push({
      key: "fromTemplate",
      label: <><FileStack className="h-4 w-4" /> {t("rev.fromTemplate")}</>,
      onSelect: () => setFromTplOpen(true),
    })
  }
  if (canDelete) {
    actionItems.push({
      key: "delete",
      label: <><Trash2 className="h-4 w-4" /> {t("rev.delete")}</>,
      danger: true,
      disabled: del.isPending || currentId == null,
      onSelect: () => {
        const cur = list.find((e) => e.id === currentId)
        if (cur && confirm(`Delete Rev ${cur.revision}? This permanently removes its BOQ, preliminaries and markups.`)) del.mutate(cur.id)
      },
    })
  }

  return (
    <div className="space-y-4">
      {/* 25.4 — One-row revision strip: dropdown + Actions ▾. Lifecycle actions
          (was 5–6 visible buttons that wrapped to a second row) are now collapsed
          into the Actions menu; export buttons (4) are collapsed into Export ▾
          inside EstimateEditor. The Compare control moved into the Insights tab
          back in 25.3 and is no longer in the strip at all. */}
      <Card className="flex flex-wrap items-center justify-between gap-3 p-3">
        <div className="flex items-center gap-2">
          <span className="text-sm font-semibold text-slate-600">Revision</span>
          <Select className="w-auto py-1.5" value={String(currentId)} onChange={(e) => setSelectedId(Number(e.target.value))}>
            {list.map((e) => (
              <option key={e.id} value={e.id}>Rev {e.revision} · {e.status} · {money(e.bidPrice, e.currency)}</option>
            ))}
          </Select>
        </div>
        {actionItems.length > 0 && (
          <DropdownButton
            ariaLabel={t("rev.actions.aria")}
            className="h-8 text-xs"
            label={t("rev.actions")}
            items={actionItems}
          />
        )}
      </Card>

      {currentId != null && <EstimateEditor key={currentId} estimateId={currentId} projectId={projectId} canEditMeta={canEditMeta} estimatesList={list} />}

      {copyOpen && currentId != null && (
        <CopyToProjectModal projectId={projectId} estimateId={currentId}
          onClose={() => setCopyOpen(false)}
          onDone={(targetId, name, rev) => { setCopyOpen(false); toast.success(`Copied to ${name} as Rev ${rev}`); router.push(`/projects/${targetId}`) }} />
      )}

      {saveTplOpen && currentId != null && (
        <SaveTemplateModal estimateId={currentId} onClose={() => setSaveTplOpen(false)} />
      )}
      {fromTplOpen && (
        <FromTemplateModal pending={fromTemplate.isPending} onClose={() => setFromTplOpen(false)}
          onPick={(templateId, title) => fromTemplate.mutate({ templateId, title })} />
      )}
    </div>
  )
}

/** 21.3 — Save the current estimate's structure as a reusable tenant template. */
function SaveTemplateModal({ estimateId, onClose }: { estimateId: number; onClose: () => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState("")
  const [description, setDescription] = useState("")
  const [category, setCategory] = useState("")
  const [tags, setTags] = useState("")
  const save = useMutation({
    mutationFn: () => fetchApi<EstimateTemplate>("/api/estimate-templates", {
      method: "POST", body: JSON.stringify({
        name: name.trim(), description: description.trim() || null, estimateId,
        category: category.trim() || null,
        tags: tags.split(",").map((t) => t.trim()).filter(Boolean),
      }),
    }),
    onSuccess: (t) => { qc.invalidateQueries({ queryKey: ["estimate-templates"] }); toast.success(`Template “${t.name}” saved`); onClose() },
    onError: (e) => toast.error((e as Error).message),
  })
  return (
    <Modal open onClose={onClose} title="Save as template">
      <div className="space-y-3">
        <Field label="Template name"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Warehouse shell — standard" /></Field>
        <Field label="Description (optional)"><Textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={2} placeholder="What this template is for" /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Category (optional)"><Input value={category} onChange={(e) => setCategory(e.target.value)} placeholder="Warehouse" maxLength={40} /></Field>
          <Field label="Tags (comma-separated)"><Input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="concrete, framing" /></Field>
        </div>
        <p className="text-xs text-slate-400">Captures the BOQ sections/items + cost build-ups, preliminaries, markups and risks. Area tags and the project are not included.</p>
      </div>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button disabled={!name.trim() || save.isPending} onClick={() => save.mutate()}>{save.isPending ? "Saving…" : "Save template"}</Button>
      </div>
    </Modal>
  )
}

/** 21.3 — Pick a saved template to spin up a new Draft revision from. */
function FromTemplateModal({ pending, onClose, onPick }: { pending: boolean; onClose: () => void; onPick: (templateId: number, title: string | null) => void }) {
  const templates = useQuery({ queryKey: ["estimate-templates"], queryFn: () => fetchApi<EstimateTemplate[]>("/api/estimate-templates") })
  const [picked, setPicked] = useState("")
  const [title, setTitle] = useState("")
  const [search, setSearch] = useState("")
  const all = templates.data ?? []
  // The API already returns featured-first, newest-next; filter client-side by name/category/tag.
  const q = search.trim().toLowerCase()
  const list = !q ? all : all.filter((t) =>
    t.name.toLowerCase().includes(q)
    || (t.category?.toLowerCase().includes(q) ?? false)
    || t.tags.some((g) => g.toLowerCase().includes(q)))
  return (
    <Modal open onClose={onClose} title="New estimate from template">
      {templates.isLoading ? <p className="text-sm text-slate-400">Loading templates…</p>
        : all.length === 0 ? <p className="text-sm text-slate-500">No templates yet. Save one from an existing estimate first.</p>
        : (
          <div className="space-y-3">
            <Field label="Find a template"><Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search name, category or tag…" /></Field>
            <Field label="Template">
              <Select value={picked} onChange={(e) => setPicked(e.target.value)}>
                <option value="">— choose a template —</option>
                {list.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.isFeatured ? "★ " : ""}{t.name}{t.category ? ` · ${t.category}` : ""} ({t.sectionCount} sec · {t.itemCount} items)
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Title (optional)"><Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="defaults to the template name" /></Field>
            <p className="text-xs text-slate-400">Creates a new Draft revision in this project from the template&apos;s structure. ★ = featured for your org.</p>
          </div>
        )}
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button disabled={!picked || pending} onClick={() => onPick(Number(picked), title.trim() || null)}>{pending ? "Creating…" : "Create"}</Button>
      </div>
    </Modal>
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
