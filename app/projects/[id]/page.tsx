"use client"
/**
 * Project detail page (25.3 tab restructure).
 *
 * Was: fourteen sections stacked vertically (Teams + Areas + Revisions +
 * Estimate editor with BOQ, prelims, markups, what-if, target, area-rollup,
 * activities, anomalies, risk register, cash-flow, approvals…) — a
 * five-thousand-pixel wall of scroll on a new estimator.
 *
 * Now: a sticky project header at the top, followed by `EstimatesSection`
 * which renders an always-visible "current revision" strip (revision +
 * status + 4 stat cards) and a tab strip (Overview · Estimate · Insights
 * · Risk · Activity) that groups the existing `_components/*` panels
 * without changing any of their behaviour.
 *
 * The leading-underscore `_components/` directory keeps Next.js from
 * treating those modules as route segments.
 */
import { use, useState, useEffect } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { Pencil, Star } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { Project, UserPreferences } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { useAuth } from "@/lib/auth"
import { Card, Badge, Button, statusColor } from "@/components/ui"
import { useT } from "@/lib/i18n"
import { usePreferences } from "@/components/projects-tree"
import { EditProjectModal } from "./_components/edit-project-modal"
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
  const t = useT()
  const { user } = useAuth()
  const isAdmin = user?.role === "TenantAdmin" || user?.role === "SuperAdmin"
  const [editing, setEditing] = useState(false)
  const qc = useQueryClient()
  const project = useQuery({ queryKey: ["project", projectId], queryFn: () => fetchApi<Project>(`/api/projects/${projectId}`) })

  // 27.5 — favourites: read the user's pins so the header star reflects state…
  const prefs = usePreferences()
  const isPinned = !!prefs.data?.pinnedProjectIds.includes(projectId)
  const pinMut = useMutation({
    mutationFn: () => fetchApi<UserPreferences>("/api/preferences/pinned", {
      method: "PUT", body: JSON.stringify({ projectId, pinned: !isPinned }),
    }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["preferences"] }),
    onError: (e) => toast.error((e as Error).message),
  })

  // …and record this visit so the project surfaces in the sidebar "Recent" list.
  // Best-effort: a failure here must never block viewing the project.
  useEffect(() => {
    fetchApi("/api/preferences/recent", { method: "POST", body: JSON.stringify({ projectId }) })
      .then(() => qc.invalidateQueries({ queryKey: ["preferences"] }))
      .catch(() => {})
  }, [projectId, qc])

  if (project.isLoading) return <p className="text-muted">{t("common.loading")}</p>
  if (project.error) return <p className="text-rose-600">{(project.error as Error).message}</p>
  const p = project.data!

  return (
    <div className="space-y-4">
      {/* 25.3 — Sticky project header. Pins to the top of the page's scroll
          area so the project identity, status and Edit affordance never leave
          the screen while the user moves between tabs. The Edit modal still
          lives at the page level because it logically belongs to the project,
          not to any one tab. */}
      <Card className="sticky top-0 z-20 p-4 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <span className="font-mono text-xs text-muted">{p.code}{p.projectTypeName ? ` · ${p.projectTypeName}` : ""}</span>
            <h2 className="truncate text-lg font-bold text-slate-800">{p.name}</h2>
            <p className="text-xs text-slate-500">
              {p.clientName ?? "—"} · {p.location ?? "—"}
              {" · "}{t("proj.currency")} <b className="text-slate-700">{p.currency}</b>
              {" · "}{t("proj.duration")} <b className="text-slate-700">{p.durationMonths ?? "—"} {t("proj.mo")}</b>
              {" · "}{t("proj.teams")} <b className="text-slate-700">{p.teamCount}</b>
            </p>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => pinMut.mutate()}
              disabled={pinMut.isPending}
              aria-label={isPinned ? t("proj.unpin") : t("proj.pin")}
              aria-pressed={isPinned}
              title={isPinned ? t("proj.unpin") : t("proj.pin")}
              className="rounded p-1.5 text-muted hover:bg-slate-100 disabled:opacity-50"
            >
              <Star className={`h-4 w-4 ${isPinned ? "fill-amber-400 text-amber-400" : ""}`} />
            </button>
            <Badge className={statusColor(p.status)}>{p.status}</Badge>
            {isAdmin && <Button variant="outline" className="h-8 text-xs" onClick={() => setEditing(true)}><Pencil className="h-3.5 w-3.5" /> {t("common.edit")}</Button>}
          </div>
        </div>
      </Card>

      {editing && <EditProjectModal project={p} onClose={() => setEditing(false)} />}

      {/* Teams and Areas (previously standalone panels here) now live inside
          the Overview tab in EstimatesSection — see 25.3 in ROADMAP.md. */}
      <EstimatesSection projectId={projectId} />
    </div>
  )
}
