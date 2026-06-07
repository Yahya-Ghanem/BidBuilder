"use client"
import { useState } from "react"
import { AlertTriangle, ShieldCheck } from "lucide-react"
import { useQuery } from "@tanstack/react-query"
import { fetchApi } from "@/lib/api"
import type { AnomalyReport } from "@/lib/types"
import { Badge, Button } from "@/components/ui"
import { money } from "@/lib/utils"
import { useT } from "@/lib/i18n"

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
 *
 * 24.1 — i18n: text is fully translated; numeric/medical formatting stays neutral.
 */
export function AnomalyPanel({ estimateId, currency }: { estimateId: number; currency: string }) {
  const t = useT()
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
          {t("ed.anom.title")}
        </div>
        <Button
          variant={scanned ? "outline" : "primary"}
          className="px-3 py-1 text-xs"
          onClick={() => { setScanned(true); q.refetch() }}
          disabled={q.isFetching}
        >
          {q.isFetching ? t("ed.anom.scanning") : scanned ? t("ed.anom.rescan") : t("ed.anom.scan")}
        </Button>
      </div>

      {!scanned && (
        <div className="text-xs text-[var(--muted-foreground)]">{t("ed.anom.intro")}</div>
      )}

      {q.error && (
        <div className="text-xs text-red-600">
          {t("ed.anom.scanFailed", { error: (q.error as Error).message })}
        </div>
      )}

      {q.data && (
        q.data.itemsFlagged === 0 ? (
          <div className="flex items-center gap-2 text-xs text-emerald-700">
            <ShieldCheck className="h-4 w-4" aria-hidden />
            {t("ed.anom.clean", { n: q.data.itemsScanned, plural: q.data.itemsScanned === 1 ? "" : "s" })}
          </div>
        ) : (
          <>
            <div className="mb-2 text-xs text-[var(--muted-foreground)]">
              {t("ed.anom.summary", { flagged: q.data.itemsFlagged, scanned: q.data.itemsScanned })}
            </div>
            <div className="overflow-x-auto">
              <table className="min-w-full text-xs">
                <thead className="text-start text-[var(--muted-foreground)]">
                  <tr>
                    <th className="px-2 py-1 text-start">{t("ed.anom.colLine")}</th>
                    <th className="px-2 py-1 text-end">{t("ed.anom.colRate")}</th>
                    <th className="px-2 py-1 text-end">{t("ed.anom.colMedian")}</th>
                    <th className="px-2 py-1 text-end">{t("ed.anom.colMultiple")}</th>
                    <th className="px-2 py-1 text-end">{t("ed.anom.colZ")}</th>
                    <th className="px-2 py-1 text-start">{t("ed.anom.colReason")}</th>
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
