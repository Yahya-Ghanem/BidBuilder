"use client"
import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Plus, Trash2, Users } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { useAuth } from "@/lib/auth"
import type { GroupOption, ProjectTeam } from "@/lib/types"
import { Card, Badge, Button } from "@/components/ui"
import { Field, Select } from "@/components/form"

/** Which teams (Groups) may access this project. Admins can assign/remove. */
export function TeamsPanel({ projectId }: { projectId: number }) {
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

      {assigned.length === 0 && <p className="text-sm text-muted">No teams assigned yet.</p>}
      <div className="space-y-1">
        {assigned.map((t) => (
          <div key={t.groupId} className="flex items-center justify-between rounded-md border border-[var(--border)] px-3 py-2 text-sm">
            <div className="flex items-center gap-2">
              <span className="font-mono text-xs text-muted">{t.groupCode}</span>
              <span className="font-medium text-slate-700">{t.groupName}</span>
              {t.isLead && <Badge className="bg-[var(--brand)]/10 text-[var(--brand)]">Lead</Badge>}
            </div>
            {isAdmin && (
              <button
                onClick={() => { if (confirm(`Remove "${t.groupName}" from this project?`)) remove.mutate(t.groupId) }}
                aria-label={`Remove team ${t.groupName}`}
                className="rounded p-1 text-slate-500 hover:bg-rose-50 hover:text-rose-600"
              >
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            )}
          </div>
        ))}
      </div>

      {isAdmin && <AssignTeam available={available} busy={assign.isPending} onAssign={(v) => assign.mutate(v)} />}
      {isAdmin && available.length === 0 && groups.data && (
        <p className="mt-2 text-xs text-muted">All teams are already assigned.</p>
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
