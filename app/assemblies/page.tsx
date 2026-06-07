"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { useRouter } from "next/navigation"
import { Plus } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { AssemblyRow } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Button, Input, Badge, TableScroll } from "@/components/ui"
import { Modal, Field } from "@/components/form"
import { Money } from "@/components/money"
import { cn } from "@/lib/utils"

type StatusFilter = "active" | "all" | "inactive"

export default function AssembliesPage() {
  const router = useRouter()
  const { can } = usePermissions()
  const canAdd = can("assemblies", "add")
  const [open, setOpen] = useState(false)
  const [filter, setFilter] = useState<StatusFilter>("active")
  const { data, isLoading, error } = useQuery({
    queryKey: ["assemblies", filter],
    queryFn: () => fetchApi<AssemblyRow[]>(`/api/assemblies${filter === "all" ? "" : `?active=${filter === "active"}`}`),
  })

  return (
    <AppShell title="Assemblies">
      <div className="mb-4 flex items-center justify-between">
        <div className="flex rounded-md border border-[var(--border)] bg-white p-0.5 text-xs">
          {(["active", "all", "inactive"] as const).map((k) => (
            <button
              key={k}
              onClick={() => setFilter(k)}
              className={cn(
                "rounded px-3 py-1 font-medium capitalize transition",
                filter === k ? "bg-[var(--brand)] text-white" : "text-slate-600 hover:bg-slate-100",
              )}
            >
              {k}
            </button>
          ))}
        </div>
        {canAdd && (
          <Button onClick={() => setOpen(true)}><Plus className="h-4 w-4" /> New assembly</Button>
        )}
      </div>
      <Card className="overflow-hidden">
        {isLoading ? <p className="p-4 text-sm text-slate-400">Loading…</p>
          : error ? <p className="p-4 text-sm text-rose-600">{(error as Error).message}</p>
          : !data?.length ? <p className="p-4 text-sm text-slate-400">{filter === "inactive" ? "No inactive assemblies." : filter === "active" ? "No active assemblies." : "No assemblies yet."}</p>
          : (
            <TableScroll>
            <table className="w-full min-w-[34rem] text-sm">
              <thead className="bg-slate-50 text-left text-xs text-slate-500">
                <tr><th className="px-4 py-2">Code</th><th className="px-4 py-2">Name</th><th className="px-4 py-2">Unit</th><th className="px-4 py-2 text-right">Components</th><th className="px-4 py-2 text-right">Unit rate</th></tr>
              </thead>
              <tbody>
                {data.map((a) => (
                  <tr key={a.id} className="cursor-pointer border-t border-[var(--border)] hover:bg-slate-50" onClick={() => router.push(`/assemblies/${a.id}`)}>
                    <td className="px-4 py-2 font-mono text-xs">{a.code}</td>
                    <td className="px-4 py-2">
                      {a.name}
                      {!a.isActive && <Badge className="ml-2 bg-slate-100 text-xs text-slate-500">Inactive</Badge>}
                    </td>
                    <td className="px-4 py-2 text-slate-500">{a.unit}</td>
                    <td className="px-4 py-2 text-right">{a.componentCount}</td>
                    {/* 25.1 — TODO: confirm currency source */}
                    <td className="px-4 py-2 text-right"><Money className="font-medium" value={a.computedRate} currency="AED" /></td>
                  </tr>
                ))}
              </tbody>
            </table>
            </TableScroll>
          )}
      </Card>
      <NewAssemblyModal open={open} onClose={() => setOpen(false)} />
    </AppShell>
  )
}

function NewAssemblyModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient()
  const router = useRouter()
  const [f, setF] = useState({ code: "", name: "", unit: "" })
  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value })
  const mut = useMutation({
    mutationFn: () => fetchApi<AssemblyRow>("/api/assemblies", { method: "POST", body: JSON.stringify({ ...f, isActive: true }) }),
    onSuccess: (a) => { toast.success(`Assembly ${a.code} created`); qc.invalidateQueries({ queryKey: ["assemblies"] }); onClose(); router.push(`/assemblies/${a.id}`) },
    onError: (e) => toast.error((e as Error).message),
  })
  return (
    <Modal open={open} onClose={onClose} title="New assembly">
      <form id="new-asm" onSubmit={(e) => { e.preventDefault(); mut.mutate() }} className="space-y-3">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Code *"><Input value={f.code} onChange={set("code")} required /></Field>
          <Field label="Unit"><Input value={f.unit} onChange={set("unit")} placeholder="m3, m2, no…" /></Field>
        </div>
        <Field label="Name *"><Input value={f.name} onChange={set("name")} required /></Field>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="new-asm" disabled={mut.isPending}>{mut.isPending ? "Saving…" : "Create"}</Button>
      </div>
    </Modal>
  )
}
