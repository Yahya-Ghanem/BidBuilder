"use client"
import { useRef, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { FileDown, FileSpreadsheet, FileText, Table, Upload } from "lucide-react"
import { ApiError, downloadFile, fetchApi, uploadFile } from "@/lib/api"
import type { Area, AssemblyRow, CostComponentType, CurrencyRates, EstimateBreakdown, EstimateSummary, ImportResult } from "@/lib/types"
import { usePermissions } from "@/lib/permissions"
import { Badge, Button, DropdownButton, statusColor, type DropdownItem } from "@/components/ui"
import { Select } from "@/components/form"
import { Money } from "@/components/money"
import { money } from "@/lib/utils"
import { useT } from "@/lib/i18n"
import { Tabs } from "@/components/tabs"
import { ExpandCollapseAll, Row, useCollapse, type CompInput } from "./shared"
import { StatCards } from "./stat-cards"
import { AddItemForm as _UnusedAddItemForm, AddSection, SectionBlock } from "./boq"
import { WhatIfPanel } from "./what-if"
import { TargetPanel } from "./target"
import { ApprovalPanel } from "./approval-panel"
import { AddMarkup, AddPrelim } from "./prelim-markup-controls"
import { RiskAndCashFlowPanel } from "./risk-cashflow"
import { AreaRollupPanel } from "./area-rollup"
import { AnomalyPanel } from "./anomaly-panel"
import { ActivitiesPanel } from "./activities"
import { BidLetterModal } from "./bid-letter-modal"
import { TeamsPanel } from "./teams-panel"
import { AreasPanel } from "./areas-panel"
import { CompareRevisions } from "./compare"

// Re-import suppression — boq.tsx re-exports AddItemForm via SectionBlock; ESLint
// would flag an unused import otherwise. The "_Unused" alias is a no-op.
void _UnusedAddItemForm

/** The estimate editor — the heart of the project page. Owns the breakdown
 *  query, every BOQ / preliminary / markup mutation, the optimistic-concurrency
 *  If-Match header on each write, and the lock state for Published revisions.
 *
 *  25.3 — Restructured into five tabs (Overview · Estimate · Insights · Risk ·
 *  Activity) so the project page stops being a five-thousand-pixel scroll. The
 *  meta header (title/status/fx/vat/pricing-date + exports), the locked banner
 *  and the 4 stat cards stay ABOVE the tab strip so they remain visible no
 *  matter which tab the user is on — the bid figure is the thing an estimator
 *  is constantly checking against, so it has to stay on screen.
 *
 *  The earlier 19.6 doc-comment claimed "sibling panels, not visual tabs"; user
 *  research (the 25.3 D-series) overturned that — newcomers hit the wall of
 *  scroll first and never find half the panels. Tabs win.
 *
 *  The Overview tab takes the Teams + Areas panels that used to render above
 *  the editor; the Insights tab absorbs the Compare-revisions panel that the
 *  revision-bar used to toggle in EstimatesSection. */
export function EstimateEditor({ estimateId, projectId, canEditMeta, estimatesList }: {
  estimateId: number
  projectId: number
  canEditMeta: boolean
  estimatesList: EstimateSummary[]
}) {
  const t = useT()
  const qc = useQueryClient()
  const { can } = usePermissions()
  const boqAdd = can("boq", "add"), boqEdit = can("boq", "edit"), boqDelete = can("boq", "delete")
  const plmView = can("prelims-markups", "view"), plmAdd = can("prelims-markups", "add")
  const plmEdit = can("prelims-markups", "edit"), plmDelete = can("prelims-markups", "delete")
  const canReports = can("reports", "view")
  const key = ["estimate", estimateId]
  const { data, isLoading, error } = useQuery({ queryKey: key, queryFn: () => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}`) })
  const assemblies = useQuery({ queryKey: ["assemblies"], queryFn: () => fetchApi<AssemblyRow[]>("/api/assemblies") })
  const costTypes = useQuery({ queryKey: ["cost-types"], queryFn: () => fetchApi<CostComponentType[]>("/api/cost-components") })
  const areas = useQuery({ queryKey: ["areas", data?.projectId], queryFn: () => fetchApi<Area[]>(`/api/projects/${data!.projectId}/areas`), enabled: data?.projectId != null })
  const boqCollapse = useCollapse(data?.sections.map((s) => s.id) ?? [], true)
  const [bidLetter, setBidLetter] = useState(false)

  // Every estimate mutation returns the recomputed breakdown — push it into cache.
  const apply = (d: EstimateBreakdown) => qc.setQueryData(key, d)
  // Send the row version we last saw so the API can detect a concurrent edit (409).
  const ifMatch = (): Record<string, string> => {
    const v = qc.getQueryData<EstimateBreakdown>(key)?.rowVersion
    return v ? { "If-Match": v } : {}
  }
  function useEstimateMut<V>(fn: (vars: V) => Promise<EstimateBreakdown>) {
    return useMutation({
      mutationFn: fn,
      onSuccess: (d) => { apply(d); qc.invalidateQueries({ queryKey: ["areas-rollup", estimateId] }) },
      onError: (e) => {
        if (e instanceof ApiError && e.status === 409) {
          toast.error("This estimate was changed by someone else — reloading the latest.")
          qc.invalidateQueries({ queryKey: key })
        } else {
          toast.error((e as Error).message)
        }
      },
    })
  }

  const addSection = useEstimateMut((v: { code: string; title: string }) =>
    fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/sections`, { method: "POST", headers: ifMatch(), body: JSON.stringify({ ...v, sortOrder: 0 }) }))
  const delSection = useEstimateMut((sid: number) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/sections/${sid}`, { method: "DELETE", headers: ifMatch() }))
  const addItem = useEstimateMut((v: { sectionId: number } & Record<string, unknown>) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/sections/${v.sectionId}/items`, { method: "POST", headers: ifMatch(), body: JSON.stringify(v) }))
  const updItem = useEstimateMut((v: { id: number } & Record<string, unknown>) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/items/${v.id}`, { method: "PUT", headers: ifMatch(), body: JSON.stringify(v) }))
  const delItem = useEstimateMut((iid: number) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/items/${iid}`, { method: "DELETE", headers: ifMatch() }))
  const addPrelim = useEstimateMut((v: unknown) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/preliminaries`, { method: "POST", headers: ifMatch(), body: JSON.stringify(v) }))
  const delPrelim = useEstimateMut((pid: number) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/preliminaries/${pid}`, { method: "DELETE", headers: ifMatch() }))
  const addMarkup = useEstimateMut((v: unknown) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/markups`, { method: "POST", headers: ifMatch(), body: JSON.stringify(v) }))
  const delMarkup = useEstimateMut((mid: number) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/markups/${mid}`, { method: "DELETE", headers: ifMatch() }))
  const updateMeta = useEstimateMut((v: { title: string; status?: string; secondaryCurrency?: string; taxRatePct?: number; pricingDate?: string; clearPricingDate?: boolean }) => fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}`, { method: "PUT", headers: ifMatch(), body: JSON.stringify(v) }))
  // Clone a room: new unit + its activities. Also refreshes the project areas list.
  const cloneRoom = useMutation({
    mutationFn: (v: { areaId: number; name: string }) =>
      fetchApi<EstimateBreakdown>(`/api/estimates/${estimateId}/areas/${v.areaId}/clone`, { method: "POST", headers: ifMatch(), body: JSON.stringify({ name: v.name }) }),
    onSuccess: (d) => { apply(d); qc.invalidateQueries({ queryKey: ["areas", d.projectId] }); qc.invalidateQueries({ queryKey: ["areas-rollup", estimateId] }); toast.success("Room cloned") },
    onError: (e) => { if (e instanceof ApiError && e.status === 409) { toast.error("This estimate was changed or is locked — reloading."); qc.invalidateQueries({ queryKey: key }) } else toast.error((e as Error).message) },
  })
  const { data: fxRates } = useQuery({ queryKey: ["currencies"], queryFn: () => fetchApi<CurrencyRates>("/api/settings/currencies") })

  // Excel BOQ import (multipart upload → returns counts + recomputed breakdown).
  const fileRef = useRef<HTMLInputElement>(null)
  const importMut = useMutation({
    mutationFn: (f: File) => uploadFile<ImportResult>(`/api/estimates/${estimateId}/import`, f, ifMatch()),
    onSuccess: (r) => { apply(r.estimate); toast.success(`Imported ${r.sectionsAdded} section(s), ${r.itemsAdded} item(s)`) },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) { toast.error("This estimate was changed by someone else — reloading the latest."); qc.invalidateQueries({ queryKey: key }) }
      else toast.error((e as Error).message)
    },
  })
  async function downloadTemplate() {
    try { await downloadFile("/api/estimates/import-template.xlsx", "boq-import-template.xlsx") }
    catch (err) { toast.error((err as Error).message) }
  }

  if (isLoading) return <p className="text-slate-400">Loading estimate…</p>
  if (error) return <p className="text-danger">{(error as Error).message}</p>
  const e = data!
  const c = e.currency
  // A Published/Superseded revision is locked: content edits are blocked server-side,
  // so suppress the affordances here too (the status dropdown stays usable to revert).
  const locked = e.status === "Published" || e.status === "Superseded"
  const editAdd = boqAdd && !locked, editEdit = boqEdit && !locked, editDelete = boqDelete && !locked
  const editPlmAdd = plmAdd && !locked, editPlmDelete = plmDelete && !locked

  async function dl(kind: "xlsx" | "pdf" | "csv") {
    try {
      await downloadFile(`/api/estimates/${estimateId}/export.${kind}`, `estimate.${kind}`)
    } catch (err) {
      toast.error((err as Error).message)
    }
  }
  async function dlActivities(kind: "xlsx" | "pdf" | "csv", level: "detail" | "area" | "subarea" | "unit" = "detail") {
    const q = level === "detail" ? "" : `?level=${level}`
    const suffix = level === "area" ? "ByArea" : level === "subarea" ? "BySubArea" : level === "unit" ? "ByUnitSummary" : "ByUnit"
    try { await downloadFile(`/api/estimates/${estimateId}/activities.${kind}${q}`, `Activities${suffix}.${kind}`) }
    catch (err) { toast.error((err as Error).message) }
  }
  async function dlCostByArea(kind: "xlsx" | "pdf" | "csv", level: "detail" | "area" | "subarea" | "unit" = "detail") {
    const q = level === "detail" ? "" : `?level=${level}`
    const suffix = level === "area" ? "ByArea" : level === "subarea" ? "BySubArea" : level === "unit" ? "ByUnit" : "ByArea"
    try { await downloadFile(`/api/estimates/${estimateId}/cost-by-area.${kind}${q}`, `Cost${suffix}.${kind}`) }
    catch (err) { toast.error((err as Error).message) }
  }

  // Add an activity under a unit: ensure the auto "Activities" section exists, then
  // create a BOQ item tagged to that unit with the material/manpower build-up.
  async function addActivity(areaId: number, v: { description: string; unit: string; components: CompInput[] }) {
    try {
      let sectionId = e.sections.find((s) => s.code === "ACT")?.id
      if (sectionId == null) {
        const bd = await addSection.mutateAsync({ code: "ACT", title: "Activities" })
        sectionId = bd.sections.find((s) => s.code === "ACT")?.id
      }
      if (sectionId == null) return
      await addItem.mutateAsync({
        sectionId, description: v.description, unit: v.unit || "", quantity: 1,
        assemblyId: null, unitRate: 0, components: v.components, areaId, sortOrder: 0,
      })
    } catch { /* useEstimateMut already toasts errors / handles 409 */ }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <h3 className="text-sm font-semibold text-slate-600">{e.title} — {t("ed.rev")} {e.revision}</h3>
          {canEditMeta
            ? <Select className="w-auto py-1 text-xs" value={e.status} onChange={(ev) => updateMeta.mutate({ title: e.title, status: ev.target.value })}>
                <option value="Draft">{t("ed.status.draft")}</option>
                <option value="UnderReview">{t("ed.status.review")}</option>
                <option value="Published">{t("ed.status.published")}</option>
                <option value="Superseded">{t("ed.status.superseded")}</option>
              </Select>
            : <Badge className={statusColor(e.status)}>{e.status}</Badge>}
          {canEditMeta && fxRates && (() => {
            const codes = [fxRates.baseCurrency, ...fxRates.rates.map((r) => r.code)]
              .filter((x, i, arr) => arr.indexOf(x) === i && x.toUpperCase() !== e.currency.toUpperCase())
            return (
              <label className="flex items-center gap-1 text-xs text-slate-500">
                {t("ed.showIn")}
                <Select className="w-auto py-1 text-xs" value={e.fx?.secondaryCurrency ?? ""}
                        onChange={(ev) => updateMeta.mutate({ title: e.title, secondaryCurrency: ev.target.value })}>
                  <option value="">{t("ed.none")}</option>
                  {codes.map((x) => <option key={x} value={x}>{x}</option>)}
                </Select>
              </label>
            )
          })()}
          {canEditMeta && (
            <label className="flex items-center gap-1 text-xs text-slate-500">
              {t("ed.vatPct")}
              <input type="number" step="0.01" min={0} max={100} defaultValue={e.taxRatePct ?? 0}
                     className="w-16 rounded-md border border-[var(--border)] px-2 py-1 text-xs outline-none focus:border-[var(--brand)]"
                     onBlur={(ev) => { const v = Number(ev.target.value); if (v !== (e.taxRatePct ?? 0)) updateMeta.mutate({ title: e.title, taxRatePct: v }) }} />
            </label>
          )}
          {canEditMeta && (
            <label className="flex items-center gap-1 text-xs text-slate-500" title="Price as-of: resolves resource rates from ResourceRateHistory (most-recent snapshot ≤ date). Empty = live rate.">
              {t("ed.pricedAsOf")}
              <input type="date" defaultValue={e.pricingDate ?? ""}
                     className="rounded-md border border-[var(--border)] px-2 py-1 text-xs outline-none focus:border-[var(--brand)]"
                     onBlur={(ev) => {
                       const v = ev.target.value
                       if ((v || null) === (e.pricingDate ?? null)) return
                       if (!v) updateMeta.mutate({ title: e.title, clearPricingDate: true })
                       else updateMeta.mutate({ title: e.title, pricingDate: v })
                     }} />
              {e.pricingDate && (
                <span className="rounded bg-warning-soft px-1.5 py-0.5 text-[10px] font-medium uppercase text-warning">{t("ed.historicalPricing")}</span>
              )}
            </label>
          )}
        </div>
        {canReports && (
          /* 25.4 — Export buttons (Excel + CSV + PDF + Bid Letter) collapsed
             into a single Export ▾ dropdown so the meta header stays one row.
             Same destinations, same handlers — only the affordance changes. */
          <DropdownButton
            ariaLabel={t("ed.export.aria")}
            className="h-8 text-xs"
            label={<><FileDown className="h-4 w-4" /> {t("ed.export")}</>}
            items={[
              { key: "xlsx", label: <><FileSpreadsheet className="h-4 w-4" /> {t("ed.export.excel")}</>, onSelect: () => dl("xlsx") },
              { key: "csv", label: <><Table className="h-4 w-4" /> {t("ed.export.csv")}</>, onSelect: () => dl("csv") },
              { key: "pdf", label: <><FileText className="h-4 w-4" /> {t("ed.export.pdf")}</>, onSelect: () => dl("pdf") },
              { key: "bidLetter", label: <><FileText className="h-4 w-4" /> {t("ed.export.bidLetter")}</>, onSelect: () => setBidLetter(true) },
            ] satisfies DropdownItem[]}
          />
        )}
      </div>
      {locked && (
        <div className="rounded-md border border-warning/30 bg-warning-soft px-3 py-2 text-sm text-warning">
          {t("ed.lockedBanner", { status: e.status })}
        </div>
      )}
      {/* 25.3 + 25.5 — Stat cards: ALWAYS visible above the tab strip (sticky
          just below the project header so the bid total never leaves the screen
          while the user scrolls deep into a tab). 25.5 extracted the grid into
          <StatCards> which adds per-card trend deltas vs. the prior revision
          and a "Per m²/room/unit" toggle when the project has areas with a
          positive top-level quantity. The top offset `top-[100px]` still
          clears the sticky project header — re-tune if header padding changes. */}
      {/* key on e.id forces remount when the user switches revisions so the
          per-estimate per-unit-toggle preference is freshly read from localStorage. */}
      <StatCards key={e.id} e={e} areas={areas.data} />
      {e.markupCost > 0 && (
        <p className="text-sm text-slate-500">{t("ed.grossMargin")} <b className="text-slate-700">{e.marginOnPricePct}%</b> <span className="text-slate-400">{t("ed.markupsOnCost")}</span></p>
      )}
      {e.fx && (
        <p className="text-sm text-slate-500">
          ≈ <Money className="text-slate-700 font-bold" as="span" value={e.fx.convertedBidPrice} currency={e.fx.secondaryCurrency} />
          {" "}· 1 {c} = {e.fx.rate.toLocaleString(undefined, { maximumFractionDigits: 6 })} {e.fx.secondaryCurrency}
          {" "}· {e.fx.frozen ? `${t("ed.fx.frozen")}${e.fx.frozenAt ? " " + e.fx.frozenAt.slice(0, 10) : ""}` : t("ed.fx.live")}
        </p>
      )}

      {/* 25.3 — Bid letter modal is rendered at the editor level (not inside any
          tab) so its open state survives a tab switch. */}
      {bidLetter && <BidLetterModal estimateId={estimateId} bidPrice={e.bidPriceInclTax} currency={c} onClose={() => setBidLetter(false)} />}

      {/* 25.3 — Five tabs grouping the existing panels with no behaviour change.
          Inactive panels stay mounted (display:none) so per-tab state like a
          half-typed BOQ row or a what-if % isn't lost when the user flips
          tabs to check an insight. */}
      <Tabs
        ariaLabel={t("ptab.aria")}
        defaultId="estimate"
        tabs={[
          {
            id: "overview",
            label: t("ptab.overview"),
            content: (
              <div className="space-y-4">
                <TeamsPanel projectId={projectId} />
                <AreasPanel projectId={projectId} />
              </div>
            ),
          },
          {
            id: "estimate",
            label: t("ptab.estimate"),
            content: (
              <div className="space-y-4">
                {/* BOQ */}
                <div className="overflow-hidden rounded-lg border border-[var(--border)] bg-white">
                  <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
                    <span className="text-sm font-semibold">{t("ed.boq.heading")}</span>
                    <div className="flex items-center gap-2">
                      {e.sections.length > 0 && (
                        <ExpandCollapseAll onExpand={boqCollapse.expandAll} onCollapse={() => boqCollapse.collapseAll(e.sections.map((s) => s.id))} />
                      )}
                      {editAdd && (
                        <>
                          <input ref={fileRef} type="file" accept=".xlsx" className="hidden"
                            onChange={(ev) => { const f = ev.target.files?.[0]; if (f) importMut.mutate(f); ev.target.value = "" }} />
                          <Button variant="ghost" className="h-7 px-2 text-xs" onClick={downloadTemplate}><FileDown className="h-3.5 w-3.5" /> {t("ed.boq.template")}</Button>
                          <Button variant="ghost" className="h-7 px-2 text-xs" disabled={importMut.isPending} onClick={() => fileRef.current?.click()}>
                            <Upload className="h-3.5 w-3.5" /> {importMut.isPending ? t("ed.boq.importing") : t("ed.boq.import")}
                          </Button>
                          <AddSection onAdd={(v) => addSection.mutate(v)} />
                        </>
                      )}
                    </div>
                  </div>
                  {e.sections.map((s) => (
                    <SectionBlock key={s.id} section={s} currency={c} assemblies={assemblies.data ?? []} costTypes={costTypes.data ?? []} areas={areas.data ?? []}
                      open={boqCollapse.isOpen(s.id)} onToggle={() => boqCollapse.toggle(s.id)}
                      canAdd={editAdd} canEdit={editEdit} canDelete={editDelete}
                      onAddItem={(v) => addItem.mutate({ ...(v as Record<string, unknown>), sectionId: s.id } as { sectionId: number } & Record<string, unknown>)}
                      onUpdItem={(v) => updItem.mutate(v as { id: number } & Record<string, unknown>)}
                      onDelItem={(iid) => delItem.mutate(iid)}
                      onDelSection={() => { if (confirm(t("ed.boq.deleteSectionConfirm", { title: s.title }))) delSection.mutate(s.id) }} />
                  ))}
                  {!e.sections.length && <p className="px-4 py-3 text-sm text-slate-400">{t("ed.boq.empty")}</p>}
                </div>

                {/* Prelims + markups */}
                <div className="grid gap-4 sm:grid-cols-2">
                  <div className="rounded-lg border border-[var(--border)] bg-white p-4">
                    <div className="mb-2 text-sm font-semibold">{t("ed.prelims")}</div>
                    {e.preliminaries.map((p) => (
                      <Row key={p.id} left={`${p.description} (${p.kind})`} right={money(p.computedTotal, c)} onDelete={editPlmDelete ? () => delPrelim.mutate(p.id) : undefined} />
                    ))}
                    {editPlmAdd && <AddPrelim onAdd={(v) => addPrelim.mutate(v)} />}
                  </div>
                  <div className="rounded-lg border border-[var(--border)] bg-white p-4">
                    <div className="mb-2 text-sm font-semibold">{t("ed.stat.markups")}</div>
                    {e.markups.map((m) => (
                      <Row key={m.id} left={`${m.type} (${m.percentage}%)`} right={money(m.computedAmount, c)} onDelete={editPlmDelete ? () => delMarkup.mutate(m.id) : undefined} />
                    ))}
                    {editPlmAdd && <AddMarkup onAdd={(v) => addMarkup.mutate(v)} />}
                  </div>
                </div>

                {plmView && e.markups.length > 0 && (
                  <WhatIfPanel key={e.markups.map((m) => m.id).join(",")}
                    estimateId={estimateId} markups={e.markups} currency={c} canEdit={plmEdit && !locked} />
                )}

                {plmView && (
                  <TargetPanel estimateId={estimateId} currency={c} currentBid={e.bidPrice}
                    currentAdjustment={e.commercialAdjustment} canApply={plmEdit && !locked} />
                )}
              </div>
            ),
          },
          {
            id: "insights",
            label: t("ptab.insights"),
            content: (
              <div className="space-y-4">
                {(areas.data?.length ?? 0) > 0 && (
                  <ActivitiesPanel breakdown={e} areas={areas.data ?? []} costTypes={costTypes.data ?? []} currency={c}
                    canAdd={editAdd} canEdit={editEdit} canDelete={editDelete} canExport={canReports} onExport={dlActivities}
                    onAddActivity={addActivity} onUpdItem={(v) => updItem.mutate(v as { id: number } & Record<string, unknown>)} onDelItem={(iid) => delItem.mutate(iid)}
                    onCloneRoom={(areaId, name) => cloneRoom.mutate({ areaId, name })} />
                )}
                <AreaRollupPanel estimateId={estimateId} currency={c} canExport={canReports} onExport={dlCostByArea} />
                {/* 23.5 — Cost-anomaly scan. Lazy: only fetches after the user clicks Scan. */}
                <AnomalyPanel estimateId={estimateId} currency={c} />
                {/* 25.3 — Compare-revisions moved here from the revision strip. */}
                {estimatesList.length >= 2 && <CompareRevisions estimates={estimatesList} />}
              </div>
            ),
          },
          {
            id: "risk",
            label: t("ptab.risk"),
            content: (
              <div className="space-y-4">
                {plmView ? (
                  <RiskAndCashFlowPanel estimateId={estimateId} currency={c}
                    risks={e.risks ?? []}
                    suggestedAmount={e.suggestedContingencyAmount ?? 0}
                    suggestedPct={e.suggestedContingencyPct ?? 0}
                    cashFlow={e.cashFlow}
                    canEdit={plmEdit && !locked} />
                ) : (
                  <p className="text-sm text-slate-500">{t("common.loading")}</p>
                )}
              </div>
            ),
          },
          {
            id: "activity",
            label: t("ptab.activity"),
            content: (
              <div className="space-y-4">
                {/* 20.2 — sign-off gate. Renders nothing when the tenant has
                    RequiredApprovalsToPublish=0 (the panel self-hides). */}
                <ApprovalPanel estimateId={estimateId} status={e.status} />
              </div>
            ),
          },
        ]}
      />
    </div>
  )
}
