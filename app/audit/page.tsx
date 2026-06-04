"use client"

import { useState } from "react"
import { useQuery, keepPreviousData } from "@tanstack/react-query"
import { ChevronLeft, ChevronRight, X } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { AuditPage } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { Card, Badge, Button, Input } from "@/components/ui"
import { Field, Select } from "@/components/form"

const PAGE_SIZE = 50

interface Filters { action: string; actor: string; from: string; to: string }
const EMPTY: Filters = { action: "", actor: "", from: "", to: "" }

export default function AuditPage() {
  return (
    <AppShell title="Audit log">
      <AuditView />
    </AppShell>
  )
}

function AuditView() {
  const [draft, setDraft] = useState<Filters>(EMPTY)
  const [applied, setApplied] = useState<Filters>(EMPTY)
  const [page, setPage] = useState(0)

  const { data: actions } = useQuery({
    queryKey: ["audit-actions"],
    queryFn: () => fetchApi<string[]>("/api/audit/actions"),
  })

  const { data, isLoading, error, isFetching } = useQuery({
    queryKey: ["audit", applied, page],
    queryFn: () => {
      const p = new URLSearchParams({ take: String(PAGE_SIZE), skip: String(page * PAGE_SIZE) })
      if (applied.action) p.set("action", applied.action)
      if (applied.actor.trim()) p.set("actor", applied.actor.trim())
      if (applied.from) p.set("from", applied.from)
      if (applied.to) p.set("to", applied.to)
      return fetchApi<AuditPage>(`/api/audit?${p.toString()}`)
    },
    placeholderData: keepPreviousData,
  })

  const set = (k: keyof Filters) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    setDraft({ ...draft, [k]: e.target.value })

  function apply() { setApplied(draft); setPage(0) }
  function clear() { setDraft(EMPTY); setApplied(EMPTY); setPage(0) }

  const hasFilters = applied.action || applied.actor || applied.from || applied.to
  const total = data?.total ?? 0
  const items = data?.items ?? []
  const skip = data?.skip ?? 0
  const rangeFrom = total === 0 ? 0 : skip + 1
  const rangeTo = skip + items.length
  const canPrev = page > 0
  const canNext = (page + 1) * PAGE_SIZE < total

  return (
    <div className="space-y-4">
      {/* Filter bar */}
      <Card className="grid items-end gap-3 p-4 sm:grid-cols-[1fr_1fr_auto_auto_auto]">
        <Field label="Action">
          <Select value={draft.action} onChange={set("action")}>
            <option value="">All actions</option>
            {actions?.map((a) => <option key={a} value={a}>{a}</option>)}
          </Select>
        </Field>
        <Field label="Actor (name or email)">
          <Input value={draft.actor} onChange={set("actor")} placeholder="e.g. admin"
                 onKeyDown={(e) => { if (e.key === "Enter") apply() }} />
        </Field>
        <Field label="From"><Input type="date" value={draft.from} onChange={set("from")} /></Field>
        <Field label="To"><Input type="date" value={draft.to} onChange={set("to")} /></Field>
        <div className="flex gap-2">
          <Button onClick={apply} disabled={isFetching} className="h-9">Apply</Button>
          {hasFilters && <Button variant="outline" onClick={clear} className="h-9"><X className="h-4 w-4" /> Clear</Button>}
        </div>
      </Card>

      {isLoading ? <p className="text-slate-400">Loading…</p>
        : error ? <p className="text-rose-600">{(error as Error).message}</p>
        : items.length === 0 ? <p className="text-slate-500">{hasFilters ? "No events match these filters." : "No audit events recorded yet."}</p>
        : (
          <Card className="overflow-hidden">
            <table className="w-full text-sm">
              <thead className="bg-slate-50 text-left text-xs text-slate-500">
                <tr>
                  <th className="px-4 py-2">When (UTC)</th>
                  <th className="px-4 py-2">Actor</th>
                  <th className="px-4 py-2">Action</th>
                  <th className="px-4 py-2">Entity</th>
                  <th className="px-4 py-2">Details</th>
                </tr>
              </thead>
              <tbody>
                {items.map((e) => (
                  <tr key={e.id} className="border-t border-[var(--border)]">
                    <td className="whitespace-nowrap px-4 py-2 text-slate-500">{e.at.replace("T", " ").slice(0, 19)}</td>
                    <td className="px-4 py-2">
                      {e.actorName ?? e.actorEmail ?? "—"}
                      {e.actorRole && <span className="ml-1 text-xs text-slate-400">{e.actorRole}</span>}
                    </td>
                    <td className="px-4 py-2"><Badge className="bg-slate-100 font-mono text-xs text-slate-700">{e.action}</Badge></td>
                    <td className="px-4 py-2 text-slate-500">{e.entity}{e.entityKey ? ` #${e.entityKey}` : ""}</td>
                    <td className="px-4 py-2 text-slate-600">{e.summary ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        )}

      {/* Pagination footer */}
      {total > 0 && (
        <div className="flex items-center justify-between text-sm text-slate-500">
          <span>Showing <b>{rangeFrom}</b>–<b>{rangeTo}</b> of <b>{total}</b></span>
          <div className="flex gap-2">
            <Button variant="outline" className="h-8 text-xs" disabled={!canPrev || isFetching} onClick={() => setPage((p) => p - 1)}>
              <ChevronLeft className="h-4 w-4" /> Prev
            </Button>
            <Button variant="outline" className="h-8 text-xs" disabled={!canNext || isFetching} onClick={() => setPage((p) => p + 1)}>
              Next <ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}
