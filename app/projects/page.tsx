"use client"

import { useState, type ReactNode } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { useRouter } from "next/navigation"
import {
  Plus, ChevronRight, ChevronDown, Building2, Folder, FolderOpen, FileText, ExternalLink,
  LayoutGrid, Table, Hammer, ClipboardList, Percent,
} from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import { useAuth } from "@/lib/auth"
import type { Project, EstimateSummary, EstimateBreakdown, Area } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { Card, Badge, Button, Input, statusColor } from "@/components/ui"
import { Modal, Field } from "@/components/form"

const money = (n: number, c: string) => `${c} ${n.toLocaleString(undefined, { maximumFractionDigits: 2 })}`

export default function ProjectsPage() {
  return (
    <AppShell title="Projects">
      <ProjectsContent />
    </AppShell>
  )
}

function ProjectsContent() {
  const { user } = useAuth()
  const isAdmin = user?.role === "TenantAdmin" || user?.role === "SuperAdmin"
  const [open, setOpen] = useState(false)

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500">
          Double-click a node to expand it — children load on demand.
        </p>
        {isAdmin && <Button onClick={() => setOpen(true)}><Plus className="h-4 w-4" /> New project</Button>}
      </div>

      <Card className="p-2">
        <RootNode />
      </Card>

      <NewProjectModal open={open} onClose={() => setOpen(false)} />
    </div>
  )
}

/* ── Tree presentation ─────────────────────────────────────────────────────── */

function TreeRow({
  depth, open, hasChildren, icon, label, badge, hint, actions, onToggle, muted,
}: {
  depth: number
  open: boolean
  hasChildren: boolean
  icon: ReactNode
  label: ReactNode
  badge?: ReactNode
  hint?: ReactNode
  actions?: ReactNode
  onToggle?: () => void
  muted?: boolean
}) {
  return (
    <div
      className={`group flex items-center gap-2 rounded py-1.5 pr-2 select-none ${onToggle ? "cursor-pointer hover:bg-slate-50" : ""}`}
      style={{ paddingLeft: depth * 22 + 4 }}
      onDoubleClick={onToggle}
      title={onToggle ? "Double-click to expand" : undefined}
    >
      {hasChildren
        ? <span className="text-slate-400">{open ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}</span>
        : <span className="inline-block w-4" />}
      <span className="text-slate-400">{icon}</span>
      <span className={`font-medium ${muted ? "text-slate-400" : "text-slate-800"}`}>{label}</span>
      {badge}
      {hint && <span className="text-xs text-slate-400">{hint}</span>}
      {actions && <span className="ml-auto opacity-0 transition group-hover:opacity-100">{actions}</span>}
    </div>
  )
}

function MessageRow({ depth, children, tone }: { depth: number; children: ReactNode; tone?: "error" }) {
  return (
    <div
      className={`py-1.5 text-sm ${tone === "error" ? "text-rose-600" : "text-slate-400"}`}
      style={{ paddingLeft: depth * 22 + 24 }}
    >
      {children}
    </div>
  )
}

/** A non-leaf node that lazily mounts its content (which does the fetching) on expand. */
function ExpandableNode({
  depth, icon, label, badge, hint, actions, defaultOpen, children: renderContent,
}: {
  depth: number
  icon: ReactNode
  label: ReactNode
  badge?: ReactNode
  hint?: ReactNode
  actions?: ReactNode
  defaultOpen?: boolean
  children: () => ReactNode
}) {
  const [open, setOpen] = useState(!!defaultOpen)
  return (
    <div>
      <TreeRow
        depth={depth} open={open} hasChildren icon={icon} label={label} badge={badge} hint={hint} actions={actions}
        onToggle={() => setOpen((o) => !o)}
      />
      {open && renderContent()}
    </div>
  )
}

/* ── Level 0: root ─────────────────────────────────────────────────────────── */

function RootNode() {
  return (
    <ExpandableNode
      depth={0}
      icon={<Building2 className="h-4 w-4" />}
      label="BidBuilder"
      hint="(root)"
    >
      {/* Mounted only once expanded → projects fetched on double-click, not before. */}
      {() => <ProjectsLevel depth={1} />}
    </ExpandableNode>
  )
}

/* ── Level 1: projects (lazy) ──────────────────────────────────────────────── */

function ProjectsLevel({ depth }: { depth: number }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["projects"],
    queryFn: () => fetchApi<Project[]>("/api/projects"),
  })

  if (isLoading) return <MessageRow depth={depth}>Loading projects…</MessageRow>
  if (error) return <MessageRow depth={depth} tone="error">{(error as Error).message}</MessageRow>
  if (!data?.length) return <MessageRow depth={depth}>No projects yet.</MessageRow>
  return <>{data.map((p) => <ProjectNode key={p.id} project={p} depth={depth} />)}</>
}

function ProjectNode({ project, depth }: { project: Project; depth: number }) {
  const router = useRouter()
  const hasEstimates = project.estimateCount > 0
  const openBtn = (
    <Button
      variant="outline" className="h-7 px-2 py-0 text-xs"
      onClick={(e) => { e.stopPropagation(); router.push(`/projects/${project.id}`) }}
    >
      <ExternalLink className="h-3.5 w-3.5" /> Open
    </Button>
  )
  const badge = <Badge className={statusColor(project.status)}>{project.status}</Badge>
  const hint = (
    <>
      <span className="font-mono">{project.code}</span>
      {" · "}{project.estimateCount} estimate(s){project.clientName ? ` · ${project.clientName}` : ""}
    </>
  )

  if (!hasEstimates) {
    return (
      <TreeRow depth={depth} open={false} hasChildren={false}
        icon={<Folder className="h-4 w-4" />} label={project.name} badge={badge} hint={hint} actions={openBtn} />
    )
  }
  return (
    <ExpandableNode depth={depth} icon={<Folder className="h-4 w-4" />} label={project.name} badge={badge} hint={hint} actions={openBtn}>
      {() => <EstimatesLevel projectId={project.id} depth={depth + 1} />}
    </ExpandableNode>
  )
}

/* ── Level 2: estimates (lazy) ─────────────────────────────────────────────── */

function EstimatesLevel({ projectId, depth }: { projectId: number; depth: number }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["estimates", projectId],
    queryFn: () => fetchApi<EstimateSummary[]>(`/api/projects/${projectId}/estimates`),
  })

  if (isLoading) return <MessageRow depth={depth}>Loading estimates…</MessageRow>
  if (error) return <MessageRow depth={depth} tone="error">{(error as Error).message}</MessageRow>
  if (!data?.length) return <MessageRow depth={depth}>No estimates yet.</MessageRow>
  return (
    <>
      {data.map((e) => (
        <EstimateNode key={e.id} estimate={e} projectId={projectId} depth={depth} />
      ))}
    </>
  )
}

function EstimateNode({ estimate, projectId, depth }: { estimate: EstimateSummary; projectId: number; depth: number }) {
  const router = useRouter()
  return (
    <ExpandableNode
      depth={depth}
      icon={<FileText className="h-4 w-4" />}
      label={`Rev ${estimate.revision} · ${estimate.title}`}
      badge={<Badge className={statusColor(estimate.status)}>{estimate.status}</Badge>}
      hint={money(estimate.bidPrice, estimate.currency)}
      actions={
        <Button variant="outline" className="h-7 px-2 py-0 text-xs"
          onClick={(e) => { e.stopPropagation(); router.push(`/projects/${projectId}`) }}>
          <ExternalLink className="h-3.5 w-3.5" /> Open
        </Button>
      }
    >
      {() => <EstimateSections estimateId={estimate.id} projectId={projectId} depth={depth + 1} />}
    </ExpandableNode>
  )
}

/* ── Level 3: the five estimate sections (each lazy) ───────────────────────── */

function EstimateSections({ estimateId, projectId, depth }: { estimateId: number; projectId: number; depth: number }) {
  return (
    <>
      <ExpandableNode depth={depth} icon={<LayoutGrid className="h-4 w-4" />} label="Areas">
        {() => <AreasBranch projectId={projectId} depth={depth + 1} />}
      </ExpandableNode>
      <ExpandableNode depth={depth} icon={<Table className="h-4 w-4" />} label="Bill Of Quantities">
        {() => <BoqBranch estimateId={estimateId} depth={depth + 1} />}
      </ExpandableNode>
      <ExpandableNode depth={depth} icon={<Hammer className="h-4 w-4" />} label="Activities" hint="by unit">
        {() => <ActivitiesBranch estimateId={estimateId} projectId={projectId} depth={depth + 1} />}
      </ExpandableNode>
      <ExpandableNode depth={depth} icon={<ClipboardList className="h-4 w-4" />} label="Preliminaries">
        {() => <PreliminariesBranch estimateId={estimateId} depth={depth + 1} />}
      </ExpandableNode>
      <ExpandableNode depth={depth} icon={<Percent className="h-4 w-4" />} label="Markups">
        {() => <MarkupsBranch estimateId={estimateId} depth={depth + 1} />}
      </ExpandableNode>
    </>
  )
}

/** Shared lazy fetch of the estimate breakdown (cached → only first opener fetches). */
function useBreakdown(estimateId: number) {
  return useQuery({ queryKey: ["estimate", estimateId], queryFn: () => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}`) })
}
function useAreas(projectId: number) {
  return useQuery({ queryKey: ["areas", projectId], queryFn: () => fetchApi<Area[]>(`/api/projects/${projectId}/areas`) })
}

/* Areas branch — nested Area → Sub-area → Unit tree. */
function AreasBranch({ projectId, depth }: { projectId: number; depth: number }) {
  const { data, isLoading, error } = useAreas(projectId)
  if (isLoading) return <MessageRow depth={depth}>Loading areas…</MessageRow>
  if (error) return <MessageRow depth={depth} tone="error">{(error as Error).message}</MessageRow>
  if (!data?.length) return <MessageRow depth={depth}>No areas yet.</MessageRow>
  const roots = data.filter((a) => a.parentAreaId == null)
  return <>{roots.map((a) => <AreaNode key={a.id} area={a} all={data} depth={depth} />)}</>
}

function AreaNode({ area, all, depth }: { area: Area; all: Area[]; depth: number }) {
  const kids = all.filter((a) => a.parentAreaId === area.id)
  const measure = area.quantity > 0 ? `${area.quantity}${area.unit ? ` ${area.unit}` : ""}` : null
  const hint = <>{area.kind}{measure ? ` · ${measure}` : ""}</>
  if (!kids.length) {
    return <TreeRow depth={depth} open={false} hasChildren={false}
      icon={<Folder className="h-4 w-4" />} label={area.name} hint={hint} />
  }
  return (
    <ExpandableNode depth={depth} icon={<Folder className="h-4 w-4" />} label={area.name} hint={<>{hint} · {kids.length} child(ren)</>}>
      {() => <>{kids.map((k) => <AreaNode key={k.id} area={k} all={all} depth={depth + 1} />)}</>}
    </ExpandableNode>
  )
}

/* Bill Of Quantities branch — sections → items. */
function BoqBranch({ estimateId, depth }: { estimateId: number; depth: number }) {
  const { data, isLoading, error } = useBreakdown(estimateId)
  if (isLoading) return <MessageRow depth={depth}>Loading BOQ…</MessageRow>
  if (error) return <MessageRow depth={depth} tone="error">{(error as Error).message}</MessageRow>
  if (!data?.sections.length) return <MessageRow depth={depth}>No sections yet.</MessageRow>
  return (
    <>
      {data.sections.map((s) => (
        <ExpandableNode key={s.id} depth={depth} icon={<Folder className="h-4 w-4" />}
          label={<>{s.code && <span className="font-mono text-xs text-slate-400">{s.code} </span>}{s.title}</>}
          hint={`${s.items.length} item(s) · ${money(s.sectionTotal, data.currency)}`}>
          {() => s.items.length
            ? <>{s.items.map((it) => (
                <TreeRow key={it.id} depth={depth + 1} open={false} hasChildren={false}
                  icon={<FileText className="h-4 w-4" />}
                  label={it.description}
                  hint={`${it.quantity} ${it.unit} × ${money(it.unitRate, data.currency)} = ${money(it.lineTotal, data.currency)}`} />
              ))}</>
            : <MessageRow depth={depth + 1}>No items.</MessageRow>}
        </ExpandableNode>
      ))}
    </>
  )
}

/* Activities branch — area tree with each area's activities (BOQ items tagged to it). */
function ActivitiesBranch({ estimateId, projectId, depth }: { estimateId: number; projectId: number; depth: number }) {
  const bd = useBreakdown(estimateId)
  const ar = useAreas(projectId)
  if (bd.isLoading || ar.isLoading) return <MessageRow depth={depth}>Loading activities…</MessageRow>
  if (bd.error) return <MessageRow depth={depth} tone="error">{(bd.error as Error).message}</MessageRow>
  if (ar.error) return <MessageRow depth={depth} tone="error">{(ar.error as Error).message}</MessageRow>
  const areas = ar.data ?? []
  const items = (bd.data?.sections ?? []).flatMap((s) => s.items)
  const currency = bd.data?.currency ?? ""
  if (!areas.length) return <MessageRow depth={depth}>No units yet.</MessageRow>
  const roots = areas.filter((a) => a.parentAreaId == null)
  return <>{roots.map((a) => <ActivityAreaNode key={a.id} area={a} all={areas} items={items} currency={currency} depth={depth} />)}</>
}

function ActivityAreaNode({ area, all, items, currency, depth }: {
  area: Area; all: Area[]; items: EstimateBreakdown["sections"][number]["items"]; currency: string; depth: number
}) {
  const kids = all.filter((a) => a.parentAreaId === area.id)
  const acts = items.filter((it) => it.areaId === area.id)
  const hasContent = kids.length > 0 || acts.length > 0
  const hint = <>{area.kind}{acts.length ? ` · ${acts.length} activit${acts.length > 1 ? "ies" : "y"}` : ""}</>
  const comp = (it: typeof acts[number], code: string) => it.components.find((c) => c.code === code)?.amount ?? 0
  if (!hasContent) {
    return <TreeRow depth={depth} open={false} hasChildren={false} icon={<Folder className="h-4 w-4" />} label={area.name} hint={hint} />
  }
  return (
    <ExpandableNode depth={depth} icon={<Folder className="h-4 w-4" />} label={area.name} hint={hint}>
      {() => (
        <>
          {acts.map((it) => (
            <TreeRow key={it.id} depth={depth + 1} open={false} hasChildren={false} icon={<Hammer className="h-4 w-4" />}
              label={it.description}
              hint={`M ${money(comp(it, "MAT"), currency)} · L ${money(comp(it, "LAB"), currency)} · ${money(it.lineTotal, currency)}`} />
          ))}
          {kids.map((k) => <ActivityAreaNode key={k.id} area={k} all={all} items={items} currency={currency} depth={depth + 1} />)}
        </>
      )}
    </ExpandableNode>
  )
}

/* Preliminaries branch — flat list of prelim lines. */
function PreliminariesBranch({ estimateId, depth }: { estimateId: number; depth: number }) {
  const { data, isLoading, error } = useBreakdown(estimateId)
  if (isLoading) return <MessageRow depth={depth}>Loading preliminaries…</MessageRow>
  if (error) return <MessageRow depth={depth} tone="error">{(error as Error).message}</MessageRow>
  if (!data?.preliminaries.length) return <MessageRow depth={depth}>No preliminaries yet.</MessageRow>
  return (
    <>
      {data.preliminaries.map((p) => (
        <TreeRow key={p.id} depth={depth} open={false} hasChildren={false} icon={<ClipboardList className="h-4 w-4" />}
          label={p.description} hint={`${p.kind} · ${money(p.computedTotal, data.currency)}`} />
      ))}
    </>
  )
}

/* Markups branch — flat list of markup lines. */
function MarkupsBranch({ estimateId, depth }: { estimateId: number; depth: number }) {
  const { data, isLoading, error } = useBreakdown(estimateId)
  if (isLoading) return <MessageRow depth={depth}>Loading markups…</MessageRow>
  if (error) return <MessageRow depth={depth} tone="error">{(error as Error).message}</MessageRow>
  if (!data?.markups.length) return <MessageRow depth={depth}>No markups yet.</MessageRow>
  return (
    <>
      {data.markups.map((m) => (
        <TreeRow key={m.id} depth={depth} open={false} hasChildren={false} icon={<Percent className="h-4 w-4" />}
          label={m.label || m.type} hint={`${m.percentage}% · ${money(m.computedAmount, data.currency)}`} />
      ))}
    </>
  )
}

/* ── New project ───────────────────────────────────────────────────────────── */

function NewProjectModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient()
  const [form, setForm] = useState({ name: "", clientName: "", location: "", currency: "AED", durationMonths: "" })
  const mut = useMutation({
    mutationFn: () => fetchApi<Project>("/api/projects", {
      method: "POST",
      body: JSON.stringify({
        name: form.name, clientName: form.clientName || null, location: form.location || null,
        currency: form.currency, durationMonths: form.durationMonths ? Number(form.durationMonths) : null,
      }),
    }),
    onSuccess: (p) => {
      toast.success(`Project ${p.code} created`)
      qc.invalidateQueries({ queryKey: ["projects"] })
      setForm({ name: "", clientName: "", location: "", currency: "AED", durationMonths: "" })
      onClose()
    },
    onError: (e) => toast.error((e as Error).message),
  })

  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value })

  return (
    <Modal open={open} onClose={onClose} title="New project">
      <form id="new-project" onSubmit={(e) => { e.preventDefault(); mut.mutate() }} className="space-y-3">
        <Field label="Name *"><Input value={form.name} onChange={set("name")} required /></Field>
        <Field label="Client"><Input value={form.clientName} onChange={set("clientName")} /></Field>
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
