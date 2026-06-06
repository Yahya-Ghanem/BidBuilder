"use client"
import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { Project, ProjectType } from "@/lib/types"
import { Modal, Field, Select } from "@/components/form"
import { Input, Button } from "@/components/ui"

/** Project meta editor (name, client, currency, duration, status, project type).
 *  Changing the duration re-prices time-related preliminaries on the project's
 *  draft estimates, so we invalidate estimate caches alongside the project ones. */
export function EditProjectModal({ project, onClose }: { project: Project; onClose: () => void }) {
  const qc = useQueryClient()
  const { data: projectTypes } = useQuery({ queryKey: ["project-types"], queryFn: () => fetchApi<ProjectType[]>("/api/project-types") })
  const typeOptions = (projectTypes ?? []).filter((t) => t.isActive || t.id === project.projectTypeId)
  const [f, setF] = useState({
    name: project.name,
    clientName: project.clientName ?? "",
    location: project.location ?? "",
    currency: project.currency,
    durationMonths: project.durationMonths != null ? String(project.durationMonths) : "",
    status: project.status,
    tenderDueAt: project.tenderDueAt ? project.tenderDueAt.slice(0, 10) : "",
    projectTypeId: project.projectTypeId != null ? String(project.projectTypeId) : "",
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
        projectTypeId: f.projectTypeId ? Number(f.projectTypeId) : null,
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
        <div className="grid grid-cols-2 gap-3">
          <Field label="Client"><Input value={f.clientName} onChange={set("clientName")} /></Field>
          <Field label="Project type">
            <Select value={f.projectTypeId} onChange={set("projectTypeId")}>
              <option value="">— none —</option>
              {typeOptions.map((t) => <option key={t.id} value={t.id}>{t.name}{t.isActive ? "" : " (inactive)"}</option>)}
            </Select>
          </Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Location"><Input value={f.location} onChange={set("location")} /></Field>
          <Field label="Currency"><Input value={f.currency} onChange={set("currency")} maxLength={3} /></Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Duration (months)"><Input type="number" min={0} value={f.durationMonths} onChange={set("durationMonths")} /></Field>
          <Field label="Tender due"><Input type="date" value={f.tenderDueAt} onChange={set("tenderDueAt")} /></Field>
          <Field label="Status"><Select value={f.status} onChange={set("status")}>{STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}</Select></Field>
        </div>
        <p className="text-xs text-slate-400">Changing the duration re-prices time-related preliminaries on this project&apos;s draft estimates.</p>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="edit-project" disabled={save.isPending}>{save.isPending ? "Saving…" : "Save"}</Button>
      </div>
    </Modal>
  )
}
