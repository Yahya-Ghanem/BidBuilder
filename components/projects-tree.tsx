"use client"

import { createContext, useContext, useState, type ReactNode } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import Link from "next/link"
import { useRouter } from "next/navigation"
import {
  Plus, ChevronRight, ChevronDown, Building2, Folder, FolderOpen, FileText, ExternalLink, Maximize2, LayoutGrid, Tag,
} from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import { usePermissions } from "@/lib/permissions"
import type { Project, EstimateSummary, EstimateBreakdown, Area, AreaRollup, ProjectType } from "@/lib/types"
import { Button, Input, Card } from "@/components/ui"
import { Modal, Field, Select } from "@/components/form"

/* ── Selection context (shared between the sidebar tree and the detail pane) ── */

export type SectionKey = "areas" | "boq" | "activities" | "cost-by-area" | "preliminaries" | "markups"

export interface TreeSelection {
  projectId: number
  projectName: string
  estimateId: number
  estimateLabel: string
  section: SectionKey
  sectionLabel: string
}

interface TreeCtx { selected: TreeSelection | null; select: (s: TreeSelection) => void }
const Ctx = createContext<TreeCtx | null>(null)

export function ProjectsTreeProvider({ children }: { children: ReactNode }) {
  const [selected, setSelected] = useState<TreeSelection | null>(null)
  return <Ctx.Provider value={{ selected, select: setSelected }}>{children}</Ctx.Provider>
}

export function useProjectsTree(): TreeCtx {
  const ctx = useContext(Ctx)
  if (!ctx) throw new Error("useProjectsTree must be used within ProjectsTreeProvider")
  return ctx
}

export const money = (n: number, c: string) => `${c} ${n.toLocaleString(undefined, { maximumFractionDigits: 2 })}`

/* ── Sidebar tree (master) ─────────────────────────────────────────────────── */

function Row({
  depth, open, hasChildren, icon, label, hint, selected, onToggle, onDoubleClick, title,
}: {
  depth: number; open?: boolean; hasChildren: boolean; icon: ReactNode
  label: ReactNode; hint?: ReactNode; selected?: boolean
  onToggle?: () => void; onDoubleClick?: () => void; title?: string
}) {
  const interactive = !!(onToggle || onDoubleClick)
  return (
    <div
      className={`flex items-center gap-1.5 rounded px-1 py-1 text-sm select-none ${interactive ? "cursor-pointer" : ""} ${selected ? "bg-[var(--brand)]/10 text-[var(--brand)]" : "hover:bg-slate-100 text-slate-700"}`}
      style={{ paddingLeft: depth * 14 + 4 }}
      onClick={onToggle}
      onDoubleClick={onDoubleClick}
      title={title}
    >
      {hasChildren
        ? <span className="shrink-0 text-slate-400">{open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}</span>
        : <span className="inline-block w-3.5 shrink-0" />}
      <span className="shrink-0 text-slate-400">{icon}</span>
      <span className="truncate font-medium">{label}</span>
      {hint && <span className="truncate text-xs text-slate-400">{hint}</span>}
    </div>
  )
}

function Message({ depth, children, tone }: { depth: number; children: ReactNode; tone?: "error" }) {
  return <div className={`py-1 text-xs ${tone === "error" ? "text-rose-600" : "text-slate-400"}`} style={{ paddingLeft: depth * 14 + 22 }}>{children}</div>
}

export function ProjectsSidebarTree() {
  const [open, setOpen] = useState(true)
  return (
    <div className="mt-1">
      <Row depth={1} open={open} hasChildren icon={<Building2 className="h-3.5 w-3.5" />} label="Projects" hint="(root)"
        onToggle={() => setOpen((o) => !o)} />
      {open && <ProjectsBranch depth={2} />}
    </div>
  )
}

const UNTYPED = "Untyped"

function ProjectsBranch({ depth }: { depth: number }) {
  const { data, isLoading, error } = useQuery({ queryKey: ["projects"], queryFn: () => fetchApi<Project[]>("/api/projects") })
  if (isLoading) return <Message depth={depth}>Loading…</Message>
  if (error) return <Message depth={depth} tone="error">{(error as Error).message}</Message>
  if (!data?.length) return <Message depth={depth}>No projects.</Message>
  // Group projects by their type; "Untyped" sorts last, the rest alphabetically.
  const groups = new Map<string, Project[]>()
  for (const p of data) {
    const key = p.projectTypeName ?? UNTYPED
    ;(groups.get(key) ?? groups.set(key, []).get(key)!).push(p)
  }
  const entries = [...groups.entries()].sort((a, b) =>
    a[0] === UNTYPED ? 1 : b[0] === UNTYPED ? -1 : a[0].localeCompare(b[0]))
  return <>{entries.map(([type, projects]) => <ProjectTypeGroup key={type} type={type} projects={projects} depth={depth} />)}</>
}

function ProjectTypeGroup({ type, projects, depth }: { type: string; projects: Project[]; depth: number }) {
  const [open, setOpen] = useState(false)
  return (
    <div>
      <Row depth={depth} open={open} hasChildren icon={<Tag className="h-3.5 w-3.5" />}
        label={type} hint={`${projects.length} project${projects.length > 1 ? "s" : ""}`}
        onToggle={() => setOpen((o) => !o)} />
      {open && projects.map((p) => <ProjectBranch key={p.id} project={p} depth={depth + 1} />)}
    </div>
  )
}

function ProjectBranch({ project, depth }: { project: Project; depth: number }) {
  const [open, setOpen] = useState(false)
  const has = project.estimateCount > 0
  return (
    <div>
      <Row depth={depth} open={open} hasChildren={has}
        icon={open && has ? <FolderOpen className="h-3.5 w-3.5" /> : <Folder className="h-3.5 w-3.5" />}
        label={project.name} hint={project.code}
        onToggle={has ? () => setOpen((o) => !o) : undefined} />
      {open && has && <EstimatesBranch projectId={project.id} projectName={project.name} depth={depth + 1} />}
    </div>
  )
}

function EstimatesBranch({ projectId, projectName, depth }: { projectId: number; projectName: string; depth: number }) {
  const { data, isLoading, error } = useQuery({ queryKey: ["estimates", projectId], queryFn: () => fetchApi<EstimateSummary[]>(`/api/projects/${projectId}/estimates`) })
  if (isLoading) return <Message depth={depth}>Loading…</Message>
  if (error) return <Message depth={depth} tone="error">{(error as Error).message}</Message>
  if (!data?.length) return <Message depth={depth}>No estimates.</Message>
  return <>{data.map((e) => <EstimateBranch key={e.id} estimate={e} projectId={projectId} projectName={projectName} depth={depth} />)}</>
}

function EstimateBranch({ estimate, projectId, depth }: { estimate: EstimateSummary; projectId: number; projectName: string; depth: number }) {
  const [open, setOpen] = useState(false)
  const router = useRouter()
  const estimateLabel = `Rev ${estimate.revision} · ${estimate.title}`
  return (
    <div>
      <Row depth={depth} open={open} hasChildren icon={<FileText className="h-3.5 w-3.5" />}
        label={estimateLabel} onToggle={() => setOpen((o) => !o)} />
      {open && (
        <Row depth={depth + 1} hasChildren={false} icon={<Maximize2 className="h-3.5 w-3.5" />}
          label="Full display" hint="open editor" title="Double-click to open the full editor"
          onDoubleClick={() => router.push(`/projects/${projectId}`)} />
      )}
    </div>
  )
}

/* ── Detail pane (renders the selected section's data) ─────────────────────── */

export function ProjectsDetailPane() {
  const { selected } = useProjectsTree()
  const { isAdmin } = usePermissions()
  const [newOpen, setNewOpen] = useState(false)

  return (
    <div className="space-y-4">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          {selected ? (
            <nav className="flex flex-wrap items-center gap-1 text-sm text-slate-500">
              <span className="font-medium text-slate-700">{selected.projectName}</span>
              <ChevronRight className="h-3.5 w-3.5" />
              <span>{selected.estimateLabel}</span>
              <ChevronRight className="h-3.5 w-3.5" />
              <span className="font-semibold text-slate-800">{selected.sectionLabel}</span>
            </nav>
          ) : (
            <p className="text-sm text-slate-500">Open a project → estimate in the sidebar, then double-click “Full display” to open the editor.</p>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {selected && (
            <Link href={`/projects/${selected.projectId}`}>
              <Button variant="outline"><ExternalLink className="h-4 w-4" /> Open editor</Button>
            </Link>
          )}
          {isAdmin && <Button onClick={() => setNewOpen(true)}><Plus className="h-4 w-4" /> New project</Button>}
        </div>
      </div>

      {selected ? <SectionView key={`${selected.estimateId}:${selected.section}`} sel={selected} /> : <EmptyState />}

      <NewProjectModal open={newOpen} onClose={() => setNewOpen(false)} />
    </div>
  )
}

function EmptyState() {
  return (
    <Card className="grid place-items-center p-16 text-center text-slate-400">
      <div>
        <LayoutGrid className="mx-auto mb-3 h-10 w-10 opacity-40" />
        <p className="text-sm">Pick a project → estimate from the tree on the left.</p>
        <p className="text-xs">Double-click “Full display” to open the full editor.</p>
      </div>
    </Card>
  )
}

function SectionView({ sel }: { sel: TreeSelection }) {
  switch (sel.section) {
    case "areas": return <AreasView projectId={sel.projectId} />
    case "boq": return <BoqView estimateId={sel.estimateId} />
    case "activities": return <ActivitiesView estimateId={sel.estimateId} projectId={sel.projectId} />
    case "cost-by-area": return <CostByAreaView estimateId={sel.estimateId} />
    case "preliminaries": return <PreliminariesView estimateId={sel.estimateId} />
    case "markups": return <MarkupsView estimateId={sel.estimateId} />
  }
}

function Preparing() { return <Card className="p-8 text-sm text-slate-400">Preparing data…</Card> }
function ErrorCard({ e }: { e: unknown }) { return <Card className="p-8 text-sm text-rose-600">{(e as Error).message}</Card> }
function EmptyCard({ children }: { children: ReactNode }) { return <Card className="p-8 text-sm text-slate-400">{children}</Card> }

function useBreakdown(estimateId: number) {
  return useQuery({ queryKey: ["estimate", estimateId], queryFn: () => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}`) })
}
function useAreas(projectId: number) {
  return useQuery({ queryKey: ["areas", projectId], queryFn: () => fetchApi<Area[]>(`/api/projects/${projectId}/areas`) })
}

/* Areas — nested Area → Sub-area → Unit. */
function AreasView({ projectId }: { projectId: number }) {
  const { data, isLoading, error } = useAreas(projectId)
  if (isLoading) return <Preparing />
  if (error) return <ErrorCard e={error} />
  if (!data?.length) return <EmptyCard>No areas yet.</EmptyCard>
  const render = (parentId: number | null, depth: number): ReactNode =>
    data.filter((a) => a.parentAreaId === parentId).map((a) => {
      const measure = a.quantity > 0 ? `${a.quantity}${a.unit ? ` ${a.unit}` : ""}` : "—"
      return (
        <div key={a.id}>
          <div className="flex items-center gap-2 border-t border-[var(--border)] py-1.5 text-sm" style={{ paddingLeft: depth * 20 + 8 }}>
            <Folder className="h-4 w-4 text-slate-400" />
            {a.code && <span className="font-mono text-xs text-slate-400">{a.code}</span>}
            <span className="text-slate-800">{a.name}</span>
            <span className="text-xs text-slate-400">{a.kind}</span>
            <span className="ml-auto text-xs text-slate-500">{measure}</span>
          </div>
          {render(a.id, depth + 1)}
        </div>
      )
    })
  return (
    <Card className="p-4">
      <h3 className="mb-2 text-sm font-semibold text-slate-600">Areas</h3>
      {render(null, 0)}
    </Card>
  )
}

/* Bill Of Quantities — sections → items. */
function BoqView({ estimateId }: { estimateId: number }) {
  const { data, isLoading, error } = useBreakdown(estimateId)
  if (isLoading) return <Preparing />
  if (error) return <ErrorCard e={error} />
  if (!data?.sections.length) return <EmptyCard>No sections yet.</EmptyCard>
  const c = data.currency
  return (
    <div className="space-y-3">
      {data.sections.map((s) => (
        <Card key={s.id} className="overflow-hidden">
          <div className="flex items-center justify-between border-b border-[var(--border)] bg-slate-50 px-4 py-2">
            <h3 className="text-sm font-semibold text-slate-700">{s.code && <span className="mr-1 font-mono text-xs text-slate-400">{s.code}</span>}{s.title}</h3>
            <span className="text-sm text-slate-500">{s.items.length} item(s) · {money(s.sectionTotal, c)}</span>
          </div>
          {s.items.length ? (
            <div className="overflow-x-auto">
            <table className="w-full min-w-[32rem] text-sm">
              <thead className="text-xs text-slate-400">
                <tr className="border-b border-[var(--border)]">
                  <th className="px-4 py-1.5 text-left font-medium">Item</th>
                  <th className="px-2 py-1.5 text-right font-medium">Qty</th>
                  <th className="px-2 py-1.5 text-left font-medium">Unit</th>
                  <th className="px-2 py-1.5 text-right font-medium">Rate</th>
                  <th className="px-4 py-1.5 text-right font-medium">Total</th>
                </tr>
              </thead>
              <tbody>
                {s.items.map((it) => (
                  <tr key={it.id} className="border-b border-[var(--border)] last:border-0">
                    <td className="px-4 py-1.5">{it.itemCode && <span className="mr-1 font-mono text-xs text-slate-400">{it.itemCode}</span>}{it.description}</td>
                    <td className="px-2 py-1.5 text-right">{it.quantity}</td>
                    <td className="px-2 py-1.5">{it.unit}</td>
                    <td className="px-2 py-1.5 text-right">{money(it.unitRate, c)}</td>
                    <td className="px-4 py-1.5 text-right font-medium">{money(it.lineTotal, c)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            </div>
          ) : <p className="px-4 py-2 text-sm text-slate-400">No items.</p>}
        </Card>
      ))}
    </div>
  )
}

/* Activities by unit — area tree with each unit's activities (M material, L manpower). */
function ActivitiesView({ estimateId, projectId }: { estimateId: number; projectId: number }) {
  const bd = useBreakdown(estimateId)
  const ar = useAreas(projectId)
  if (bd.isLoading || ar.isLoading) return <Preparing />
  if (bd.error) return <ErrorCard e={bd.error} />
  if (ar.error) return <ErrorCard e={ar.error} />
  const areas = ar.data ?? []
  const items = (bd.data?.sections ?? []).flatMap((s) => s.items)
  const c = bd.data?.currency ?? ""
  if (!areas.length) return <EmptyCard>No units yet.</EmptyCard>
  const comp = (it: typeof items[number], code: string) => it.components.find((x) => x.code === code)?.amount ?? 0
  const render = (parentId: number | null, depth: number): ReactNode =>
    areas.filter((a) => a.parentAreaId === parentId).map((a) => {
      const acts = items.filter((it) => it.areaId === a.id)
      return (
        <div key={a.id}>
          <div className="flex items-center gap-2 border-t border-[var(--border)] py-1.5 text-sm" style={{ paddingLeft: depth * 18 + 8 }}>
            <Folder className="h-4 w-4 text-slate-400" />
            <span className="text-slate-800">{a.name}</span>
            <span className="text-xs text-slate-400">{a.kind}{acts.length ? ` · ${acts.length} activit${acts.length > 1 ? "ies" : "y"}` : ""}</span>
          </div>
          {acts.map((it) => (
            <div key={it.id} className="grid grid-cols-[1fr_120px_120px_120px] items-center gap-2 py-1 text-sm" style={{ paddingLeft: depth * 18 + 30 }}>
              <span className="text-slate-700">{it.description}</span>
              <span className="text-right text-xs text-slate-500" title="Material">M {money(comp(it, "MAT"), c)}</span>
              <span className="text-right text-xs text-slate-500" title="Manpower">L {money(comp(it, "LAB"), c)}</span>
              <span className="text-right font-medium">{money(it.lineTotal, c)}</span>
            </div>
          ))}
          {render(a.id, depth + 1)}
        </div>
      )
    })
  return (
    <Card className="p-4">
      <h3 className="mb-1 text-sm font-semibold text-slate-600">Activities by unit</h3>
      <p className="mb-2 text-xs text-slate-400">M = material (qty × price), L = manpower (hours × rate); total includes any other components.</p>
      {render(null, 0)}
    </Card>
  )
}

/* Cost by area — the area roll-up (direct + rolled-up totals, cost per unit). */
function CostByAreaView({ estimateId }: { estimateId: number }) {
  const { data, isLoading, error } = useQuery({ queryKey: ["areas-rollup", estimateId], queryFn: () => fetchApi<AreaRollup>(`/api/estimates/${estimateId}/areas-rollup`) })
  const [collapsed, setCollapsed] = useState<Set<number>>(new Set())
  const toggle = (id: number) => setCollapsed((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n })
  if (isLoading) return <Preparing />
  if (error) return <ErrorCard e={error} />
  if (!data?.areas.length) return <EmptyCard>No costed areas yet.</EmptyCard>
  const c = data.currency
  const childrenOf = (pid: number | null) => data.areas.filter((a) => a.parentAreaId === pid)
  const parentIds = data.areas.filter((a) => childrenOf(a.id).length > 0).map((a) => a.id)

  const render = (parentId: number | null, depth: number): ReactNode =>
    childrenOf(parentId).map((a) => {
      const kids = childrenOf(a.id)
      const has = kids.length > 0
      const open = !collapsed.has(a.id)
      return (
        <div key={a.id}>
          <div className="grid grid-cols-[1fr_110px_130px_120px] items-center gap-2 border-t border-[var(--border)] py-1.5 text-sm" style={{ paddingLeft: depth * 18 + 8 }}>
            <span className="flex items-center gap-1.5">
              {has
                ? <button onClick={() => toggle(a.id)} className="text-slate-400 hover:text-slate-600">{open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}</button>
                : <span className="inline-block w-3.5" />}
              <Folder className="h-4 w-4 text-slate-400" />
              <span className="text-slate-800">{a.name}</span>
              <span className="text-xs text-slate-400">{a.kind}{!open && has ? ` · ${kids.length} sub-area(s)` : ""}</span>
            </span>
            <span className="text-right text-xs text-slate-500">{a.itemCount} item(s)</span>
            <span className="text-right font-medium">{money(a.rollupTotal, c)}</span>
            <span className="text-right text-xs text-slate-500">{a.costPerUnit != null ? `${money(a.costPerUnit, c)}/${a.unit || "unit"}` : "—"}</span>
          </div>
          {open && render(a.id, depth + 1)}
        </div>
      )
    })
  return (
    <Card className="p-4">
      <div className="mb-2 flex items-center justify-between">
        <h3 className="text-sm font-semibold text-slate-600">Cost by area</h3>
        <div className="flex items-center gap-3">
          {parentIds.length > 0 && (
            <div className="flex items-center gap-1 text-xs">
              <button onClick={() => setCollapsed(new Set())} className="rounded px-1.5 py-0.5 text-slate-500 hover:bg-slate-100">Expand all</button>
              <span className="text-slate-300">·</span>
              <button onClick={() => setCollapsed(new Set(parentIds))} className="rounded px-1.5 py-0.5 text-slate-500 hover:bg-slate-100">Collapse all</button>
            </div>
          )}
          <span className="text-xs text-slate-500">Assigned {money(data.assignedTotal, c)} · Unassigned {money(data.unassignedTotal, c)}</span>
        </div>
      </div>
      <div className="grid grid-cols-[1fr_110px_130px_120px] gap-2 pb-1 text-xs text-slate-400" style={{ paddingLeft: 8 }}>
        <span>Area</span><span className="text-right">Items</span><span className="text-right">Roll-up total</span><span className="text-right">Cost / unit</span>
      </div>
      {render(null, 0)}
    </Card>
  )
}

/* Preliminaries — flat list. */
function PreliminariesView({ estimateId }: { estimateId: number }) {
  const { data, isLoading, error } = useBreakdown(estimateId)
  if (isLoading) return <Preparing />
  if (error) return <ErrorCard e={error} />
  if (!data?.preliminaries.length) return <EmptyCard>No preliminaries yet.</EmptyCard>
  const c = data.currency
  return (
    <Card className="overflow-hidden">
      <h3 className="border-b border-[var(--border)] bg-slate-50 px-4 py-2 text-sm font-semibold text-slate-700">Preliminaries</h3>
      <table className="w-full text-sm">
        <thead className="text-xs text-slate-400"><tr className="border-b border-[var(--border)]"><th className="px-4 py-1.5 text-left font-medium">Description</th><th className="px-2 py-1.5 text-left font-medium">Kind</th><th className="px-4 py-1.5 text-right font-medium">Amount</th></tr></thead>
        <tbody>
          {data.preliminaries.map((p) => (
            <tr key={p.id} className="border-b border-[var(--border)] last:border-0">
              <td className="px-4 py-1.5">{p.description}</td>
              <td className="px-2 py-1.5 text-slate-500">{p.kind}</td>
              <td className="px-4 py-1.5 text-right font-medium">{money(p.computedTotal, c)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  )
}

/* Markups — flat list. */
function MarkupsView({ estimateId }: { estimateId: number }) {
  const { data, isLoading, error } = useBreakdown(estimateId)
  if (isLoading) return <Preparing />
  if (error) return <ErrorCard e={error} />
  if (!data?.markups.length) return <EmptyCard>No markups yet.</EmptyCard>
  const c = data.currency
  return (
    <Card className="overflow-hidden">
      <h3 className="border-b border-[var(--border)] bg-slate-50 px-4 py-2 text-sm font-semibold text-slate-700">Markups</h3>
      <table className="w-full text-sm">
        <thead className="text-xs text-slate-400"><tr className="border-b border-[var(--border)]"><th className="px-4 py-1.5 text-left font-medium">Markup</th><th className="px-2 py-1.5 text-right font-medium">%</th><th className="px-4 py-1.5 text-right font-medium">Amount</th></tr></thead>
        <tbody>
          {data.markups.map((m) => (
            <tr key={m.id} className="border-b border-[var(--border)] last:border-0">
              <td className="px-4 py-1.5">{m.label || m.type}</td>
              <td className="px-2 py-1.5 text-right text-slate-500">{m.percentage}%</td>
              <td className="px-4 py-1.5 text-right font-medium">{money(m.computedAmount, c)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  )
}

/* ── New project ───────────────────────────────────────────────────────────── */

function NewProjectModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient()
  const { data: projectTypes } = useQuery({ queryKey: ["project-types"], queryFn: () => fetchApi<ProjectType[]>("/api/project-types") })
  const activeTypes = (projectTypes ?? []).filter((t) => t.isActive)
  const [form, setForm] = useState({ name: "", clientName: "", location: "", currency: "AED", durationMonths: "", projectTypeId: "" })
  const mut = useMutation({
    mutationFn: () => fetchApi<Project>("/api/projects", {
      method: "POST",
      body: JSON.stringify({
        name: form.name, clientName: form.clientName || null, location: form.location || null,
        currency: form.currency, durationMonths: form.durationMonths ? Number(form.durationMonths) : null,
        projectTypeId: form.projectTypeId ? Number(form.projectTypeId) : null,
      }),
    }),
    onSuccess: (p) => {
      toast.success(`Project ${p.code} created`)
      qc.invalidateQueries({ queryKey: ["projects"] })
      setForm({ name: "", clientName: "", location: "", currency: "AED", durationMonths: "", projectTypeId: "" })
      onClose()
    },
    onError: (e) => toast.error((e as Error).message),
  })
  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setForm({ ...form, [k]: e.target.value })
  return (
    <Modal open={open} onClose={onClose} title="New project">
      <form id="new-project" onSubmit={(e) => { e.preventDefault(); mut.mutate() }} className="space-y-3">
        <Field label="Name *"><Input value={form.name} onChange={set("name")} required /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Client"><Input value={form.clientName} onChange={set("clientName")} /></Field>
          <Field label="Project type">
            <Select value={form.projectTypeId} onChange={set("projectTypeId")}>
              <option value="">— none —</option>
              {activeTypes.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
            </Select>
          </Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Location"><Input value={form.location} onChange={set("location")} /></Field>
          <Field label="Currency"><Input value={form.currency} onChange={set("currency")} /></Field>
        </div>
        <Field label="Duration (months)"><Input type="number" min={0} value={form.durationMonths} onChange={set("durationMonths")} /></Field>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="new-project" disabled={mut.isPending}>{mut.isPending ? "Saving…" : "Create"}</Button>
      </div>
    </Modal>
  )
}
