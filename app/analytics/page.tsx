"use client"

import { useMemo, useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import type { LucideIcon } from "lucide-react"
import { Trophy, TrendingUp, TrendingDown, Pencil } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { BidAnalyticsResult, BidAnalyticsBucket, BidRegisterRow } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { Card, Input } from "@/components/ui"
import { Modal, Field, ModalActions } from "@/components/form"
import { usePermissions } from "@/lib/permissions"
import { money } from "@/lib/utils"

/**
 * Bid Analytics dashboard (19.1). Read-only roll-ups + the editable bid register
 * underneath. Scope is server-enforced (ProjectAccessService) — a user only sees
 * the bid data for projects they're allowed into.
 *
 * Hit-rate = Won / (Won + Lost). Bid-vs-Award = (award − bid) / bid × 100; a
 * weighted-average per bucket, null when no rows in that bucket have BOTH sides.
 */
export default function AnalyticsPage() {
  // Lazy initializers keep the impure "now()" call out of the render body — the
  // lint rule react-hooks/set-state-in-effect is strict about that.
  const [from, setFrom] = useState(() => new Date(Date.now() - 365 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10))
  const [to,   setTo]   = useState(() => new Date().toISOString().slice(0, 10))

  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ["analytics-bids", from, to],
    queryFn: () => fetchApi<BidAnalyticsResult>(`/api/analytics/bids?from=${from}&to=${to}`),
  })

  return (
    <AppShell title="Bid Analytics">
      <div className="space-y-6">
        {/* Date window */}
        <Card className="flex flex-wrap items-end gap-4 p-4">
          <Field label="From"><Input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
          <Field label="To"><Input type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
          <span className="text-xs text-slate-500">Window filters the hit-rate / variance buckets. The register lists every accessible project.</span>
        </Card>

        {error && <p className="text-sm text-rose-600">{(error as Error).message}</p>}
        {isLoading && <p className="text-sm text-slate-400">Loading…</p>}

        {data && (
          <>
            <OverallStrip overall={data.overall} />

            <div className="grid gap-6 lg:grid-cols-2">
              <BucketTable title="By project type" rows={data.byProjectType} />
              <BucketTable title="By client"       rows={data.byClient} />
            </div>

            <BucketTable title="By period (year-quarter)" rows={data.byPeriod} />

            <Register rows={data.register} onChanged={() => refetch()} />
          </>
        )}
      </div>
    </AppShell>
  )
}

function OverallStrip({ overall }: { overall: BidAnalyticsBucket }) {
  return (
    <Card className="grid grid-cols-2 gap-4 p-4 sm:grid-cols-4">
      <Stat label="Total decided" value={String(overall.total)} icon={Trophy} />
      <Stat label="Hit rate" value={`${overall.hitRatePct.toFixed(2)}%`} sub={`${overall.won} won / ${overall.lost} lost`} icon={Trophy} accent />
      <Stat label="Avg bid → award" value={overall.avgBidVsAwardPct === null ? "—" : `${overall.avgBidVsAwardPct.toFixed(2)}%`}
            sub={overall.avgBidVsAwardPct === null ? "no completed pairs" : "weighted-average %"}
            icon={overall.avgBidVsAwardPct !== null && overall.avgBidVsAwardPct >= 0 ? TrendingUp : TrendingDown} />
      <Stat label="Awarded value (won)" value={money(overall.awardedValueSum)} sub={overall.mixedCurrency ? "mixed currencies" : ""} icon={Trophy} />
    </Card>
  )
}

function Stat({ label, value, sub, icon: Icon, accent }: { label: string; value: string; sub?: string; icon: LucideIcon; accent?: boolean }) {
  return (
    <div className={`rounded-md border border-[var(--border)] p-3 ${accent ? "bg-[var(--brand)]/5" : ""}`}>
      <div className="flex items-center gap-2 text-xs uppercase tracking-wide text-slate-500"><Icon className="h-3.5 w-3.5" /> {label}</div>
      <div className="mt-1 text-xl font-semibold tabular-nums">{value}</div>
      {sub && <div className="text-xs text-slate-500">{sub}</div>}
    </div>
  )
}

function BucketTable({ title, rows }: { title: string; rows: BidAnalyticsBucket[] }) {
  return (
    <Card className="overflow-hidden">
      <div className="border-b border-[var(--border)] px-4 py-2 text-sm font-semibold">{title}</div>
      {!rows.length ? (
        <p className="p-4 text-sm text-slate-400">No decided projects in this window.</p>
      ) : (
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-left text-xs text-slate-500">
            <tr>
              <th className="px-4 py-2">Bucket</th>
              <th className="px-4 py-2 text-right">Total</th>
              <th className="px-4 py-2 text-right">Won</th>
              <th className="px-4 py-2 text-right">Lost</th>
              <th className="px-4 py-2 text-right">Hit rate</th>
              <th className="px-4 py-2 text-right">Avg bid→award</th>
              <th className="px-4 py-2 text-right">Awarded $</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((b) => (
              <tr key={b.key} className="border-t border-[var(--border)]">
                <td className="px-4 py-2 font-medium">{b.key}</td>
                <td className="px-4 py-2 text-right tabular-nums">{b.total}</td>
                <td className="px-4 py-2 text-right tabular-nums text-emerald-700">{b.won}</td>
                <td className="px-4 py-2 text-right tabular-nums text-rose-700">{b.lost}</td>
                <td className="px-4 py-2 text-right tabular-nums">{b.hitRatePct.toFixed(2)}%</td>
                <td className="px-4 py-2 text-right tabular-nums">
                  {b.avgBidVsAwardPct === null ? <span className="text-slate-300">—</span>
                    : <span className={b.avgBidVsAwardPct >= 0 ? "text-emerald-700" : "text-rose-700"}>
                        {b.avgBidVsAwardPct > 0 ? "+" : ""}{b.avgBidVsAwardPct.toFixed(2)}%
                      </span>}
                </td>
                <td className="px-4 py-2 text-right tabular-nums">
                  {money(b.awardedValueSum)}
                  {b.mixedCurrency && <span className="ml-1 rounded bg-amber-50 px-1.5 py-0.5 text-[10px] uppercase text-amber-800" title="Aggregated across multiple project currencies — see register below for the mix.">mixed</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  )
}

function Register({ rows, onChanged }: { rows: BidRegisterRow[]; onChanged: () => void }) {
  const { can } = usePermissions()
  const canEdit = can("projects", "edit")
  const [editing, setEditing] = useState<BidRegisterRow | null>(null)

  const grouped = useMemo(() => rows.slice(0, 250), [rows])  // cap to 250 latest for UI snappiness

  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
        <span className="text-sm font-semibold">Bid register</span>
        <span className="text-xs text-slate-500">{rows.length} project(s) accessible{rows.length > 250 ? " · showing the 250 most recent" : ""}</span>
      </div>
      {!grouped.length ? (
        <p className="p-4 text-sm text-slate-400">No projects accessible.</p>
      ) : (
        <div className="overflow-auto">
          <table className="w-full text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500">
              <tr>
                <th className="px-4 py-2">Project</th>
                <th className="px-4 py-2">Client</th>
                <th className="px-4 py-2">Type</th>
                <th className="px-4 py-2">Status</th>
                <th className="px-4 py-2">Decided</th>
                <th className="px-4 py-2 text-right">Submitted bid</th>
                <th className="px-4 py-2 text-right">Awarded</th>
                <th className="px-4 py-2 text-right">Bid→Award</th>
                <th className="px-4 py-2 text-right">Final cost</th>
                <th className="px-2 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {grouped.map((r) => (
                <tr key={r.projectId} className="border-t border-[var(--border)]">
                  <td className="px-4 py-2"><span className="font-mono text-xs">{r.code}</span> · {r.name}</td>
                  <td className="px-4 py-2 text-slate-600">{r.clientName ?? <span className="text-slate-300">—</span>}</td>
                  <td className="px-4 py-2 text-slate-600">{r.projectTypeName ?? <span className="text-slate-300">—</span>}</td>
                  <td className="px-4 py-2"><StatusPill status={r.status} /></td>
                  <td className="px-4 py-2 text-slate-500">{r.decisionAt?.slice(0,10) ?? <span className="text-slate-300">—</span>}</td>
                  <td className="px-4 py-2 text-right tabular-nums">{r.submittedBidValue === null ? <span className="text-slate-300">—</span> : money(r.submittedBidValue)}</td>
                  <td className="px-4 py-2 text-right tabular-nums">{r.awardedValue === null ? <span className="text-slate-300">—</span> : money(r.awardedValue)}</td>
                  <td className="px-4 py-2 text-right tabular-nums">
                    {r.bidVsAwardPct === null ? <span className="text-slate-300">—</span>
                      : <span className={r.bidVsAwardPct >= 0 ? "text-emerald-700" : "text-rose-700"}>
                          {r.bidVsAwardPct > 0 ? "+" : ""}{r.bidVsAwardPct.toFixed(2)}%
                        </span>}
                  </td>
                  <td className="px-4 py-2 text-right tabular-nums">{r.finalCost === null ? <span className="text-slate-300">—</span> : money(r.finalCost)}</td>
                  <td className="px-2 py-2">
                    {canEdit && (
                      <button onClick={() => setEditing(r)} className="rounded p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700" title="Record outcome">
                        <Pencil className="h-3.5 w-3.5" />
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && <OutcomeModal row={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onChanged() }} />}
    </Card>
  )
}

function StatusPill({ status }: { status: string }) {
  const tone =
    status === "Won"       ? "bg-emerald-50 text-emerald-700"
    : status === "Lost"    ? "bg-rose-50 text-rose-700"
    : status === "Submitted" ? "bg-sky-50 text-sky-700"
    : status === "Bidding" ? "bg-amber-50 text-amber-700"
    : status === "Archived" ? "bg-slate-100 text-slate-500"
    : "bg-slate-50 text-slate-600"
  return <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${tone}`}>{status}</span>
}

function OutcomeModal({ row, onClose, onSaved }: { row: BidRegisterRow; onClose: () => void; onSaved: () => void }) {
  const qc = useQueryClient()
  const [bid, setBid] = useState(row.submittedBidValue?.toString() ?? "")
  const [award, setAward] = useState(row.awardedValue?.toString() ?? "")
  const [final, setFinal] = useState(row.finalCost?.toString() ?? "")
  const [decided, setDecided] = useState(row.decisionAt?.slice(0,10) ?? "")
  const [note, setNote] = useState(row.winLossNote ?? "")

  const save = useMutation({
    mutationFn: () => fetchApi(`/api/projects/${row.projectId}/bid-outcome`, {
      method: "PATCH",
      body: JSON.stringify({
        submittedBidValue: bid === "" ? null : Number(bid),
        awardedValue:      award === "" ? null : Number(award),
        finalCost:         final === "" ? null : Number(final),
        decisionAt:        decided ? new Date(decided + "T00:00:00Z").toISOString() : null,
        winLossNote:       note.trim() === "" ? null : note,
        clearWinLossNote:  note.trim() === "" && (row.winLossNote ?? "").length > 0,
      }),
    }),
    onSuccess: () => { toast.success("Outcome recorded"); qc.invalidateQueries({ queryKey: ["analytics-bids"] }); onSaved() },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Modal open onClose={onClose} title={`Record outcome · ${row.code}`}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate() }} className="grid gap-3">
        <div className="grid grid-cols-2 gap-3">
          <Field label={`Submitted bid (${row.currency})`}><Input type="number" step="0.01" min={0} value={bid} onChange={(e) => setBid(e.target.value)} /></Field>
          <Field label={`Awarded value (${row.currency})`}><Input type="number" step="0.01" min={0} value={award} onChange={(e) => setAward(e.target.value)} /></Field>
          <Field label={`Final cost (${row.currency})`}><Input type="number" step="0.01" min={0} value={final} onChange={(e) => setFinal(e.target.value)} /></Field>
          <Field label="Decision date"><Input type="date" value={decided} onChange={(e) => setDecided(e.target.value)} /></Field>
        </div>
        <Field label="Win/loss note">
          <textarea value={note} onChange={(e) => setNote(e.target.value)} rows={3}
            placeholder="Lost on price — undercut by 4%. Client signalled they may re-tender."
            className="w-full rounded-md border border-[var(--border)] px-3 py-2 text-sm outline-none focus:border-[var(--brand)]" />
        </Field>
        <ModalActions onCancel={onClose} busy={save.isPending} submitLabel="Save outcome" />
      </form>
    </Modal>
  )
}
