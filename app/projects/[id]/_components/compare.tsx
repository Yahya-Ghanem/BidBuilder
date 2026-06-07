"use client"
import { useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { GitCompareArrows, X } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { CompareView, EstimateSummary } from "@/lib/types"
import { Card, Badge, statusColor } from "@/components/ui"
import { Money } from "@/components/money"
import { money } from "@/lib/utils"

/**
 * 20.5 — Bid comparison. Pick 2–4 revisions of this project and see their
 * roll-up totals and per-section figures side by side, with deltas measured
 * against the first (baseline) column.
 *
 * The first selected revision is the baseline; every other column shows its
 * delta versus that baseline. When the selected revisions aren't all in one
 * currency the deltas are hidden (you can't subtract AED from USD) and a
 * warning is shown instead — each column is still formatted in its own currency.
 */
// 25.3 — `onClose` is now optional. The Insights tab embeds this panel inline
// with no close affordance; the original revision-bar toggle in EstimatesSection
// kept the X button for parity with that flow.
export function CompareRevisions({ estimates, onClose }: { estimates: EstimateSummary[]; onClose?: () => void }) {
  // Selection order matters: the first picked revision is the baseline. A Set
  // preserves insertion order, so iterating it yields the column order.
  const [selected, setSelected] = useState<number[]>(() => estimates.slice(0, 2).map((e) => e.id))

  function toggle(id: number) {
    setSelected((prev) =>
      prev.includes(id) ? prev.filter((x) => x !== id)
      : prev.length >= 4 ? prev   // cap at four
      : [...prev, id])
  }

  const ids = selected
  const enough = ids.length >= 2 && ids.length <= 4
  const qs = ids.map((id) => `ids=${id}`).join("&")
  const { data, isLoading, error } = useQuery<CompareView>({
    queryKey: ["estimate-compare", ids],
    queryFn: () => fetchApi<CompareView>(`/api/estimates/compare?${qs}`),
    enabled: enough,
  })

  return (
    <Card className="space-y-4 p-4">
      <div className="flex items-center justify-between gap-3">
        <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-700">
          <GitCompareArrows className="h-4 w-4 text-[var(--brand)]" /> Compare revisions
        </h3>
        {onClose && (
          <button onClick={onClose} className="rounded p-1 text-muted hover:bg-slate-100 hover:text-slate-600" title="Close comparison">
            <X className="h-4 w-4" />
          </button>
        )}
      </div>

      {/* Revision picker — tick 2 to 4. The order you tick sets the baseline (first). */}
      <div className="flex flex-wrap gap-2">
        {estimates.map((e) => {
          const idx = ids.indexOf(e.id)
          const on = idx >= 0
          const atCap = !on && ids.length >= 4
          return (
            <button key={e.id} onClick={() => toggle(e.id)} disabled={atCap}
              className={`flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition
                ${on ? "border-[var(--brand)] bg-[var(--brand)]/10 text-slate-800"
                     : atCap ? "cursor-not-allowed border-[var(--border)] text-slate-300"
                             : "border-[var(--border)] text-slate-600 hover:border-slate-400"}`}>
              {on && <span className="grid h-4 w-4 place-items-center rounded-full bg-[var(--brand)] text-[10px] font-bold text-white">{idx + 1}</span>}
              Rev {e.revision}
              <span className="text-muted">· {e.status}</span>
            </button>
          )
        })}
      </div>

      {!enough && <p className="text-xs text-muted">Select 2–4 revisions to compare.</p>}
      {enough && isLoading && <p className="text-sm text-muted">Comparing…</p>}
      {enough && error && <p className="text-sm text-rose-600">{(error as Error).message}</p>}
      {enough && data && <CompareTable view={data} />}
    </Card>
  )
}

function CompareTable({ view }: { view: CompareView }) {
  const { columns, sections, mixedCurrency } = view
  const baseline = columns[0]

  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[640px] border-collapse text-sm">
        <thead>
          <tr className="border-b border-[var(--border)]">
            <th className="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted">Metric</th>
            {columns.map((c, i) => (
              <th key={c.estimateId} className="px-3 py-2 text-right align-bottom">
                <div className="flex flex-col items-end gap-1">
                  <span className="font-semibold text-slate-700">Rev {c.revision}</span>
                  <Badge className={statusColor(c.status)}>{c.status}</Badge>
                  {i === 0 && <span className="text-[10px] uppercase tracking-wide text-muted">baseline</span>}
                  <span className="max-w-[12rem] truncate text-[11px] font-normal text-muted" title={c.title}>{c.title}</span>
                </div>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          <MetricRow label="Direct cost"        columns={columns} pick={(c) => c.directCost}      mixed={mixedCurrency} />
          <MetricRow label="Indirect (prelims)" columns={columns} pick={(c) => c.indirectCost}    mixed={mixedCurrency} />
          <MetricRow label="Markups"            columns={columns} pick={(c) => c.markupCost}      mixed={mixedCurrency} />
          <MetricRow label="Bid (excl. tax)"    columns={columns} pick={(c) => c.bidPrice}        mixed={mixedCurrency} strong />
          <MetricRow label="Tax / VAT"          columns={columns} pick={(c) => c.taxAmount}       mixed={mixedCurrency} />
          <MetricRow label="Bid (incl. tax)"    columns={columns} pick={(c) => c.bidPriceInclTax} mixed={mixedCurrency} strong />
          <MarginRow columns={columns} />

          {sections.length > 0 && (
            <tr className="border-t border-[var(--border)] bg-slate-50/60">
              <td colSpan={columns.length + 1} className="px-3 py-1.5 text-xs font-semibold uppercase tracking-wide text-muted">Sections</td>
            </tr>
          )}
          {sections.map((s) => (
            <tr key={s.key} className="border-t border-[var(--border)]">
              <td className="px-3 py-1.5 text-slate-600">
                {s.code && <span className="mr-1 text-muted">{s.code}</span>}{s.title}
              </td>
              {columns.map((c, i) => (
                <Cell key={c.estimateId} value={s.totals[i]} baseline={s.totals[0]} currency={c.currency} isBaseline={i === 0} mixed={mixedCurrency} />
              ))}
            </tr>
          ))}
        </tbody>
      </table>

      {mixedCurrency && (
        <p className="mt-3 rounded-md bg-amber-50 px-3 py-2 text-xs text-amber-700">
          These revisions use different currencies — figures are shown in each revision&apos;s own currency and deltas are hidden (they aren&apos;t directly comparable).
        </p>
      )}
      <p className="mt-2 text-[11px] text-muted">Deltas are measured against Rev {baseline.revision} (the baseline column).</p>
    </div>
  )
}

/** A headline metric row: one value per column with a delta vs the first column. */
function MetricRow({ label, columns, pick, mixed, strong }: {
  label: string; columns: CompareView["columns"]; pick: (c: CompareView["columns"][number]) => number; mixed: boolean; strong?: boolean
}) {
  const base = pick(columns[0])
  return (
    <tr className={`border-t border-[var(--border)] ${strong ? "font-semibold" : ""}`}>
      <td className={`px-3 py-1.5 ${strong ? "text-slate-800" : "text-slate-600"}`}>{label}</td>
      {columns.map((c, i) => (
        <Cell key={c.estimateId} value={pick(c)} baseline={base} currency={c.currency} isBaseline={i === 0} mixed={mixed} />
      ))}
    </tr>
  )
}

/** Margin-on-price is a percentage, not money — show points delta, not currency. */
function MarginRow({ columns }: { columns: CompareView["columns"] }) {
  const base = columns[0].marginOnPricePct
  return (
    <tr className="border-t border-[var(--border)]">
      <td className="px-3 py-1.5 text-slate-600">Margin on price</td>
      {columns.map((c, i) => {
        const d = c.marginOnPricePct - base
        return (
          <td key={c.estimateId} className="px-3 py-1.5 text-right tabular-nums">
            <span className="text-slate-700">{c.marginOnPricePct.toFixed(2)}%</span>
            {i > 0 && Math.abs(d) >= 0.005 && (
              <span className={`ml-1.5 text-[11px] ${d > 0 ? "text-emerald-600" : "text-rose-600"}`}>
                {d > 0 ? "+" : ""}{d.toFixed(2)} pts
              </span>
            )}
          </td>
        )
      })}
    </tr>
  )
}

/** A money cell with an optional signed delta + percentage vs the baseline. */
function Cell({ value, baseline, currency, isBaseline, mixed }: {
  value: number | null; baseline: number | null; currency: string; isBaseline: boolean; mixed: boolean
}) {
  if (value == null) return <td className="px-3 py-1.5 text-right tabular-nums text-slate-300">—</td>
  return (
    <td className="px-3 py-1.5 text-right tabular-nums">
      <Money className="text-slate-700" value={value} currency={currency} />
      {!isBaseline && !mixed && baseline != null && <Delta value={value} baseline={baseline} />}
    </td>
  )
}

/** Signed delta with a percentage, color-coded (higher = red, lower = green —
 *  a lower bid is usually the “better” option to highlight). */
function Delta({ value, baseline }: { value: number; baseline: number }) {
  const d = value - baseline
  if (Math.abs(d) < 0.005) return <span className="ml-1.5 text-[11px] text-muted">=</span>
  const pct = baseline !== 0 ? (d / Math.abs(baseline)) * 100 : null
  return (
    <span className={`ml-1.5 block text-[11px] ${d > 0 ? "text-rose-600" : "text-emerald-600"}`}>
      {d > 0 ? "+" : "−"}{money(Math.abs(d)).replace(/^[^\d-]*/, "")}{pct != null && ` (${d > 0 ? "+" : "−"}${Math.abs(pct).toFixed(1)}%)`}
    </span>
  )
}
