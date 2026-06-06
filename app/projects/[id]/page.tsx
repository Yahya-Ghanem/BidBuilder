"use client"
/**
 * Project detail page (19.6).
 *
 * This page used to be a single ~1,800-line file. It now composes focused
 * components from `_components/` — each living in its own file and importable
 * for testing in isolation. The route stays a thin shell whose only job is
 * to read the route param, fetch the project header, and render the
 * Teams / Areas / Estimates panels.
 *
 * The leading-underscore directory keeps Next.js from treating those modules
 * as route segments.
 */
import { use, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { Pencil } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { Project } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { useAuth } from "@/lib/auth"
import { Card, Badge, Button, statusColor } from "@/components/ui"
import { EditProjectModal } from "./_components/edit-project-modal"
import { TeamsPanel } from "./_components/teams-panel"
import { AreasPanel } from "./_components/areas-panel"
import { EstimatesSection } from "./_components/estimates-section"

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
            <span className="font-mono text-xs text-slate-400">{p.code}{p.projectTypeName ? ` · ${p.projectTypeName}` : ""}</span>
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
