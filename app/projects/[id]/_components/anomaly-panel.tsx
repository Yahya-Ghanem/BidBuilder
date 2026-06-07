"use client"
import { useState } from "react"
import { AlertTriangle, ShieldCheck } from "lucide-react"
import { useQuery } from "@tanstack/react-query"
import { fetchApi } from "@/lib/api"
import type { AnomalyReport } from "@/lib/types"
import { Badge, Button } from "@/components/ui"
import { money } from "@/lib/utils"

/**
 * 23.5 — Cost-anomaly panel.
 *
 * Runs a fan-out scan: GET `/api/estimates/{id}/anomalies`. The endpoint compares
 * each BOQ line's unit rate against the tenant's historical priced lines (same
 * unit, optional description-token narrowing). Lines >= 2.5 stddevs from the
 * mean, or whose rate is >= 3× / <= 1/3 the historical median, are flagged.
 *
 * Lazy by design: the query only runs after the user clicks "Scan" — most of
 * the time an estimator opens an estimate to keep working, not to audit it, and
 * we'd rather not pay the cross-estimate scan cost on every open.
 */
export function AnomalyPanel({ estimateId, currency }: { estimateId: number; currency: string }) {
  const [scanned, setScanned] = useState(false)
  const q = useQuery({
    queryKey: ["anomalies", estimateId],
    queryFn:  () => fetchApi<AnomalyReport>(`/api/estimates/${estimateId}/anomalies`),
    enabled:  scanned,
    staleTime: 60_000,
  })

  return (
    <div className="rounded-lg border border-[var(--border)] bg-white p-4">
      <div className="mb-2 flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-semibold">
          <AlertTriangle className="h-4 w-4 text-amber-500" aria-hidden />
          Cost anomalies
        </div>
        <Button
          variant={scanned ? "outline" : "primary"}
          className="px-3 py-1 text-xs"
          onClick={() => { setScanned(true); q.refetch() }}
          disabled={q.isFetching}
        >
          {q.isFetching ? "Scanning…" : scanned ? "Re-scan" : "Scan"}
        </Button>
      </div>

      {!scanned && (
        <div className="text-xs text-[var(--muted-foreground)]">
          Compares every priced BOQ line in this estimate against your tenant&apos;s
          historical lines (same unit) and flags outliers — overpriced and under-priced
          alike. Lines without enough history are silently passed.
        </div>
      )}

      {q.error && <div className="text-xs text-red-600">Failed to scan: {(q.error as Error).message}</div>}

      {q.data && (
        q.data.itemsFlagged === 0 ? (
          <div className="flex items-center gap-2 text-xs text-emerald-700">
            <ShieldCheck className="h-4 w-4" aria-hidden />
            No anomalies — scanned {q.data.itemsScanned} priced line{q.data.itemsScanned === 1 ? "" : "s"}.
          </div>
        ) : (
          <>
            <div className="mb-2 text-xs text-[var(--muted-foreground)]">
              {q.data.itemsFlagged} of {q.data.itemsScanned} priced lines flagged.
            </div>
            <div className="overflow-x-auto">
              <table className="min-w-full text-xs">
                <thead className="text-start text-[var(--muted-foreground)]">
                  <tr>
                    <th className="px-2 py-1 text-start">Line</th>
                    <th className="px-2 py-1 text-end">Rate</th>
                    <th className="px-2 py-1 text-end">Median</th>
                    <th className="px-2 py-1 text-end">×</th>
                    <th className="px-2 py-1 text-end">z</th>
                    <th className="px-2 py-1 text-start">Why</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-[var(--border)]">
                  {q.data.items.map((it) => (
                    <tr key={it.itemId}>
                      <td className="px-2 py-1">
                        <div className="flex items-center gap-1">
                          <Badge className={it.severity === "high" ? "bg-rose-100 text-rose-700" : "bg-amber-100 text-amber-800"}>
                            {it.severity}
                          </Badge>
                          <span className="truncate" title={it.description}>{it.itemCode || it.description}</span>
                        </div>
                        <div className="text-[10px] text-[var(--muted-foreground)]">
                          {it.unit} · n={it.sampleSize}
                        </div>
                      </td>
                      <td className="px-2 py-1 text-end tabular-nums">{money(it.unitRate, currency)}</td>
                      <td className="px-2 py-1 text-end tabular-nums">{money(it.historicalMedian, currency)}</td>
                      <td className="px-2 py-1 text-end tabular-nums">{it.medianMultiple.toFixed(2)}×</td>
                      <td className="px-2 py-1 text-end tabular-nums">{it.zScore.toFixed(1)}</td>
                      <td className="px-2 py-1 text-[var(--muted-foreground)]">{it.reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )
      )}
    </div>
  )
}
