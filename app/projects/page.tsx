"use client"

import { useState, type ReactNode } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { useRouter } from "next/navigation"
import { Plus, ChevronRight, ChevronDown, Building2, Folder, FolderOpen, FileText, ExternalLink } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import { useAuth } from "@/lib/auth"
import type { Project, EstimateSummary } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { Card, Badge, Button, Input, statusColor } from "@/components/ui"
import { Modal, Field } from "@/components/form"

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

/* ── Level 0: root ─────────────────────────────────────────────────────────── */

function RootNode() {
  const [open, setOpen] = useState(false)
  return (
    <div>
      <TreeRow
        depth={0}
        open={open}
        hasChildren
        icon={open ? <FolderOpen className="h-4 w-4" /> : <Building2 className="h-4 w-4" />}
        label="BidBuilder"
        hint="(root)"
        onToggle={() => setOpen((o) => !o)}
      />
      {/* Mounted only once expanded → projects fetched on double-click, not before. */}
      {open && <ProjectsLevel depth={1} />}
    </div>
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
  const [open, setOpen] = useState(false)
  const router = useRouter()
  const hasEstimates = project.estimateCount > 0
  return (
    <div>
      <TreeRow
        depth={depth}
        open={open}
        hasChildren={hasEstimates}
        icon={open && hasEstimates ? <FolderOpen className="h-4 w-4" /> : <Folder className="h-4 w-4" />}
        label={project.name}
        badge={<Badge className={statusColor(project.status)}>{project.status}</Badge>}
        hint={
          <>
            <span className="font-mono">{project.code}</span>
            {" · "}{project.estimateCount} estimate(s){project.clientName ? ` · ${project.clientName}` : ""}
          </>
        }
        actions={
          <Button
            variant="outline"
            className="h-7 px-2 py-0 text-xs"
            onClick={(e) => { e.stopPropagation(); router.push(`/projects/${project.id}`) }}
          >
            <ExternalLink className="h-3.5 w-3.5" /> Open
          </Button>
        }
        onToggle={hasEstimates ? () => setOpen((o) => !o) : undefined}
      />
      {open && hasEstimates && <EstimatesLevel projectId={project.id} depth={depth + 1} />}
    </div>
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
    <TreeRow
      depth={depth}
      open={false}
      hasChildren={false}
      icon={<FileText className="h-4 w-4" />}
      label={`Rev ${estimate.revision} · ${estimate.title}`}
      badge={<Badge className={statusColor(estimate.status)}>{estimate.status}</Badge>}
      hint={`${estimate.currency} ${estimate.bidPrice.toLocaleString()}`}
      actions={
        <Button
          variant="outline"
          className="h-7 px-2 py-0 text-xs"
          onClick={(e) => { e.stopPropagation(); router.push(`/projects/${projectId}`) }}
        >
          <ExternalLink className="h-3.5 w-3.5" /> Open
        </Button>
      }
      onToggle={() => router.push(`/projects/${projectId}`)}
    />
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
