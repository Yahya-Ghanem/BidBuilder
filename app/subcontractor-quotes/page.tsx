"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { Plus, Link2, Check, X, Trash2 } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { SubcontractorQuote, Project } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Button, Input, TableScroll } from "@/components/ui"
import { Modal, Field, ModalActions, Select, Textarea } from "@/components/form"
import { Money } from "@/components/money"

/**
 * 20.6 — Subcontractor quote portal (estimator side). Create a request-for-quote
 * against a project, copy the public portal link to send to the subcontractor,
 * then track, accept or decline the priced responses as they come back. Gated on
 * the "projects" module.
 */
export default function SubcontractorQuotesPage() {
  return <AppShell title="Subcontractor Quotes"><Register /></AppShell>
}

const STATUS_STYLE: Record<string, string> = {
  Pending:   "bg-slate-100 text-slate-600",
  Submitted: "bg-amber-100 text-amber-700",
  Accepted:  "bg-emerald-100 text-emerald-700",
  Declined:  "bg-rose-100 text-rose-700",
  Revoked:   "bg-slate-100 text-slate-400 line-through",
}

function StatusBadge({ status, expired }: { status: string; expired: boolean }) {
  const cls = STATUS_STYLE[status] ?? "bg-slate-100 text-slate-600"
  return (
    <span className="inline-flex items-center gap-1">
      <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${cls}`}>{status}</span>
      {expired && status === "Pending" && (
        <span className="rounded bg-rose-50 px-1.5 py-0.5 text-[10px] font-medium uppercase text-rose-700">expired</span>
      )}
    </span>
  )
}

function Register() {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canAdd = can("projects", "add")
  const canEdit = can("projects", "edit")
  const canDelete = can("projects", "delete")

  const [adding, setAdding] = useState(false)

  const { data, isLoading, error } = useQuery({
    queryKey: ["subcontractor-quotes"],
    queryFn: () => fetchApi<SubcontractorQuote[]>("/api/subcontractor-quotes"),
  })

  const decide = useMutation({
    mutationFn: ({ id, accept }: { id: number; accept: boolean }) =>
      fetchApi(`/api/subcontractor-quotes/${id}/decision`, { method: "POST", body: JSON.stringify({ accept }) }),
    onSuccess: (_d, v) => { toast.success(v.accept ? "Quote accepted" : "Quote declined"); qc.invalidateQueries({ queryKey: ["subcontractor-quotes"] }) },
    onError: (e) => toast.error((e as Error).message),
  })

  const revoke = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/subcontractor-quotes/${id}`, { method: "DELETE" }),
    onSuccess: () => { toast.success("Request withdrawn"); qc.invalidateQueries({ queryKey: ["subcontractor-quotes"] }) },
    onError: (e) => toast.error((e as Error).message),
  })

  // 23.3 — Fetch a freshly signed portal URL (HMAC+exp) from the backend and copy it
  // to the clipboard. The default expiry is 7 days; the dropdown next to each row
  // (24h / 7d / 30d) is plumbed through validDays.
  async function copySignedLink(q: SubcontractorQuote, validDays = 7) {
    try {
      const r = await fetchApi<{ portalPath: string; expiresAt: string }>(
        `/api/subcontractor-quotes/${q.id}/signed-link?validDays=${validDays}`)
      const url = `${window.location.origin}${r.portalPath}`
      await navigator.clipboard.writeText(url)
      toast.success(`Portal link copied — expires ${r.expiresAt.slice(0, 10)}`)
    } catch (e) {
      toast.error((e as Error).message)
    }
  }

  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
        <span className="text-sm font-semibold">Requests for quote</span>
        {canAdd && <Button variant="ghost" className="h-7 px-2 text-xs" onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" /> New request</Button>}
      </div>

      {isLoading ? <p className="p-4 text-sm text-slate-400">Loading…</p>
        : error ? <p className="p-4 text-sm text-rose-600">{(error as Error).message}</p>
        : !data?.length ? <p className="p-4 text-sm text-slate-400">No subcontractor requests yet. Create one and share the portal link.</p>
        : (
          <TableScroll>
          <table className="w-full min-w-[60rem] text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500">
              <tr>
                <th className="px-4 py-2">Contractor</th>
                <th className="px-4 py-2">Trade</th>
                <th className="px-4 py-2">Project</th>
                <th className="px-4 py-2">Status</th>
                <th className="px-4 py-2 text-right">Quote</th>
                <th className="px-4 py-2">Expires</th>
                <th className="px-2 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {data.map((q) => (
                <tr key={q.id} className="border-t border-[var(--border)] align-top">
                  <td className="px-4 py-2">
                    <div className="font-medium text-slate-700">{q.contractorName}</div>
                    {q.respondentName && <div className="text-xs text-slate-400">by {q.respondentName}</div>}
                  </td>
                  <td className="px-4 py-2 text-slate-600">{q.trade}</td>
                  <td className="px-4 py-2 text-slate-500">{q.projectCode}</td>
                  <td className="px-4 py-2"><StatusBadge status={q.status} expired={q.expired} /></td>
                  <td className="px-4 py-2 text-right tabular-nums">
                    {q.quotedAmount != null
                      // 25.1 — Was "AED 187,500.50 AED": `money(quotedAmount)` already prefixed
                      // the (defaulted) currency, and `{q.currency}` was appended next to it,
                      // producing a duplicate (or worse, conflicting) currency code. <Money>
                      // takes currency as a required prop and renders it once via Intl.
                      ? <Money value={q.quotedAmount} currency={q.currency} />
                      : <span className="text-slate-300">—</span>}
                  </td>
                  <td className="px-4 py-2 text-slate-500">{q.expiresAt.slice(0, 10)}</td>
                  <td className="px-2 py-2">
                    <div className="flex items-center justify-end gap-1">
                      {(q.status === "Pending" || q.status === "Submitted") && (
                        // 23.3 — A small <details> popover so the row stays tidy. Click the link
                        // icon to expand; pick an expiry (24h / 7d / 30d). One-click = 7 days.
                        <details className="relative">
                          <summary title="Copy signed portal link" className="list-none cursor-pointer rounded p-1 text-slate-400 hover:bg-slate-100 hover:text-[var(--brand)]">
                            <Link2 className="h-3.5 w-3.5" />
                          </summary>
                          <div className="absolute end-0 z-10 mt-1 flex flex-col gap-1 rounded-md border border-[var(--border)] bg-white p-1 shadow-md">
                            <button onClick={() => copySignedLink(q, 1)}  className="rounded px-2 py-1 text-xs text-slate-700 hover:bg-slate-100 whitespace-nowrap text-start">Copy — 24 hours</button>
                            <button onClick={() => copySignedLink(q, 7)}  className="rounded px-2 py-1 text-xs text-slate-700 hover:bg-slate-100 whitespace-nowrap text-start">Copy — 7 days</button>
                            <button onClick={() => copySignedLink(q, 30)} className="rounded px-2 py-1 text-xs text-slate-700 hover:bg-slate-100 whitespace-nowrap text-start">Copy — 30 days</button>
                          </div>
                        </details>
                      )}
                      {canEdit && q.status === "Submitted" && (
                        <>
                          <button title="Accept" onClick={() => decide.mutate({ id: q.id, accept: true })}
                            className="rounded p-1 text-slate-400 hover:bg-emerald-50 hover:text-emerald-600">
                            <Check className="h-3.5 w-3.5" />
                          </button>
                          <button title="Decline" onClick={() => decide.mutate({ id: q.id, accept: false })}
                            className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600">
                            <X className="h-3.5 w-3.5" />
                          </button>
                        </>
                      )}
                      {canDelete && (q.status === "Pending" || q.status === "Submitted") && (
                        <button title="Withdraw request" onClick={() => { if (confirm(`Withdraw the request to ${q.contractorName}?`)) revoke.mutate(q.id) }}
                          className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600">
                          <Trash2 className="h-3.5 w-3.5" />
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </TableScroll>
        )}

      {adding && <NewRequestModal onClose={() => setAdding(false)} onCreated={(q) => {
        setAdding(false)
        qc.invalidateQueries({ queryKey: ["subcontractor-quotes"] })
        // 23.3 — Auto-copy a signed 7-day link on create so the estimator can paste right away.
        void copySignedLink(q, 7)
      }} />}
    </Card>
  )
}

function NewRequestModal({ onClose, onCreated }: { onClose: () => void; onCreated: (q: SubcontractorQuote) => void }) {
  const { data: projects } = useQuery({ queryKey: ["projects"], queryFn: () => fetchApi<Project[]>("/api/projects") })
  const [projectId, setProjectId] = useState("")
  const [trade, setTrade] = useState("")
  const [scope, setScope] = useState("")
  const [contractorName, setContractorName] = useState("")
  const [contractorEmail, setContractorEmail] = useState("")
  const [currency, setCurrency] = useState("")
  const [validDays, setValidDays] = useState("30")

  const save = useMutation({
    mutationFn: () => fetchApi<SubcontractorQuote>("/api/subcontractor-quotes", {
      method: "POST",
      body: JSON.stringify({
        projectId: Number(projectId),
        trade: trade.trim(),
        scope: scope.trim(),
        contractorName: contractorName.trim(),
        contractorEmail: contractorEmail.trim() || null,
        currency: currency.trim() || null,
        validDays: Number(validDays || 30),
      }),
    }),
    onSuccess: (q) => onCreated(q),
    onError: (e) => toast.error((e as Error).message),
  })

  const valid = projectId && trade.trim() && scope.trim() && contractorName.trim()

  return (
    <Modal open onClose={onClose} title="New subcontractor request">
      <form onSubmit={(e) => { e.preventDefault(); if (valid) save.mutate() }} className="grid gap-3">
        <Field label="Project">
          <Select value={projectId} onChange={(e) => setProjectId(e.target.value)} required>
            <option value="">Select a project…</option>
            {projects?.map((p) => <option key={p.id} value={p.id}>{p.code} — {p.name}</option>)}
          </Select>
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Trade / package"><Input value={trade} onChange={(e) => setTrade(e.target.value)} placeholder="Concrete works" required /></Field>
          <Field label="Contractor name"><Input value={contractorName} onChange={(e) => setContractorName(e.target.value)} placeholder="Acme Concrete LLC" required /></Field>
          <Field label="Contractor email (optional)"><Input type="email" value={contractorEmail} onChange={(e) => setContractorEmail(e.target.value)} placeholder="estimating@acme.com" /></Field>
          <Field label="Currency (optional)"><Input value={currency} onChange={(e) => setCurrency(e.target.value.toUpperCase())} maxLength={3} placeholder="project default" /></Field>
        </div>
        <Field label="Scope to price"><Textarea value={scope} onChange={(e) => setScope(e.target.value)} rows={4} placeholder="Supply, place and finish 500 m³ of C40 concrete to raft foundation…" required /></Field>
        <Field label="Link valid for (days)"><Input type="number" min={1} max={365} value={validDays} onChange={(e) => setValidDays(e.target.value)} /></Field>
        <p className="text-xs text-slate-400">A unique, no-login portal link is generated. Share it with the subcontractor — it expires after the window above.</p>
        <ModalActions onCancel={onClose} busy={save.isPending} submitLabel="Create & copy link" />
      </form>
    </Modal>
  )
}
