"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import Link from "next/link"
import { Plus } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import { useAuth } from "@/lib/auth"
import type { Project } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { Card, Badge, Button, Input, statusColor } from "@/components/ui"
import { Modal, Field, ModalActions } from "@/components/form"

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
  const { data, isLoading, error } = useQuery({ queryKey: ["projects"], queryFn: () => fetchApi<Project[]>("/api/projects") })

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        {isAdmin && <Button onClick={() => setOpen(true)}><Plus className="h-4 w-4" /> New project</Button>}
      </div>

      {isLoading ? <p className="text-slate-400">Loading projects…</p>
        : error ? <p className="text-rose-600">{(error as Error).message}</p>
        : !data?.length ? <p className="text-slate-500">No projects yet.</p>
        : (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {data.map((p) => (
              <Link key={p.id} href={`/projects/${p.id}`}>
                <Card className="p-4 transition hover:shadow-md">
                  <div className="mb-2 flex items-center justify-between">
                    <span className="font-mono text-xs text-slate-400">{p.code}</span>
                    <Badge className={statusColor(p.status)}>{p.status}</Badge>
                  </div>
                  <h3 className="font-semibold text-slate-800">{p.name}</h3>
                  <p className="text-sm text-slate-500">{p.clientName ?? "—"}</p>
                  <div className="mt-3 flex items-center justify-between text-xs text-slate-500">
                    <span>{p.location ?? "—"}</span>
                    <span>{p.teamCount} team(s) · {p.estimateCount} estimate(s)</span>
                  </div>
                </Card>
              </Link>
            ))}
          </div>
        )}

      <NewProjectModal open={open} onClose={() => setOpen(false)} />
    </div>
  )
}

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
