"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { Plus, Pencil, Trash2, Power, PowerOff, Sparkles, Check } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { ResourceRow, BulkResourceResult, RateSuggestion } from "@/lib/types"
import { RateTrendCell } from "@/components/rate-trend-cell"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Button, Input, TableScroll } from "@/components/ui"
import { Modal, Field } from "@/components/form"
import { money } from "@/lib/utils"
import { Money } from "@/components/money"
import { useI18n } from "@/lib/i18n"

type Kind = "labor" | "materials" | "equipment" | "subcontractors"
/** API enum value sent in the rate-suggestion query (matches C# ResourceType). */
type ResourceApiType = "Labor" | "Material" | "Equipment" | "Subcontractor"
interface TabCfg { key: Kind; labelKey: string; path: string; rateField: "ratePerHour" | "unitPrice" | "unitRate"; rateLabelKey: string; material?: boolean; apiType: ResourceApiType }

const TABS: TabCfg[] = [
  { key: "labor",          labelKey: "resources.labor",          path: "/api/resources/labor",          rateField: "ratePerHour", rateLabelKey: "resources.ratePerHour", apiType: "Labor"         },
  { key: "materials",      labelKey: "resources.materials",      path: "/api/resources/materials",      rateField: "unitPrice",   rateLabelKey: "resources.unitPrice",   material: true, apiType: "Material"  },
  { key: "equipment",      labelKey: "resources.equipment",      path: "/api/resources/equipment",      rateField: "ratePerHour", rateLabelKey: "resources.ratePerHour", apiType: "Equipment"     },
  { key: "subcontractors", labelKey: "resources.subcontractors", path: "/api/resources/subcontractors", rateField: "unitRate",    rateLabelKey: "resources.unitRate",    apiType: "Subcontractor" },
]

export default function ResourcesPage() {
  const { t } = useI18n()
  return (
    <AppShell title={t("nav.resources")}>
      <div className="grid gap-6 lg:grid-cols-2">
        {TABS.map((tab) => <ResourceTable key={tab.key} cfg={tab} />)}
      </div>
    </AppShell>
  )
}

type BulkAction = "activate" | "deactivate" | "delete"

function ResourceTable({ cfg }: { cfg: TabCfg }) {
  const qc = useQueryClient()
  const { t, locale } = useI18n()
  const { can } = usePermissions()
  const canAdd = can("resource-library", "add")
  const canEdit = can("resource-library", "edit")
  const canDelete = can("resource-library", "delete")
  const [editing, setEditing] = useState<ResourceRow | null>(null)
  const [adding, setAdding] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const { data, isLoading, error } = useQuery({ queryKey: ["res", cfg.path], queryFn: () => fetchApi<ResourceRow[]>(cfg.path) })

  // Selecting rows enables bulk actions; only meaningful when the user can edit
  // or delete. Keep selection valid as the list changes.
  const canSelect = canEdit || canDelete
  const rows = data ?? []
  const allChecked = rows.length > 0 && rows.every((r) => selected.has(r.id))
  const toggle = (id: number) => setSelected((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })
  const toggleAll = () => setSelected((s) => s.size === rows.length ? new Set() : new Set(rows.map((r) => r.id)))
  const clear = () => setSelected(new Set())

  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`${cfg.path}/${id}`, { method: "DELETE" }),
    onSuccess: () => { toast.success(t("resources.deleted")); qc.invalidateQueries({ queryKey: ["res", cfg.path] }) },
    onError: (e) => toast.error((e as Error).message),
  })

  const bulk = useMutation({
    mutationFn: (action: BulkAction) => fetchApi<BulkResourceResult>(`${cfg.path}/bulk`, {
      method: "POST", body: JSON.stringify({ ids: [...selected], action }),
    }),
    onSuccess: (r) => {
      qc.invalidateQueries({ queryKey: ["res", cfg.path] })
      const done = r.action === "delete" ? `${r.deleted} ${t("resources.deleted").toLowerCase()}` : `${r.updated} ${r.action}d`
      if (r.skipped.length) toast.warning(`${done} · ${r.skipped.length} skipped (in use by an assembly)`)
      else toast.success(done)
      clear()
    },
    onError: (e) => toast.error((e as Error).message),
  })

  const runBulk = (action: BulkAction) => {
    if (action === "delete" && !confirm(t("resources.bulkDeleteConfirm", { n: selected.size, kind: t(cfg.labelKey).toLowerCase() }))) return
    bulk.mutate(action)
  }

  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
        <span className="text-sm font-semibold">{t(cfg.labelKey)}</span>
        {canAdd && <Button variant="ghost" className="h-7 px-2 text-xs" onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" /> {t("common.add")}</Button>}
      </div>

      {/* Bulk action bar — appears once rows are selected. */}
      {selected.size > 0 && (
        <div className="flex flex-wrap items-center gap-2 border-b border-[var(--border)] bg-slate-50 px-4 py-2 text-xs">
          <span className="font-medium text-slate-600">{t("resources.selected", { n: selected.size })}</span>
          {canEdit && <Button variant="outline" className="h-7 text-xs" disabled={bulk.isPending} onClick={() => runBulk("activate")}><Power className="h-3.5 w-3.5" /> {t("common.activate")}</Button>}
          {canEdit && <Button variant="outline" className="h-7 text-xs" disabled={bulk.isPending} onClick={() => runBulk("deactivate")}><PowerOff className="h-3.5 w-3.5" /> {t("common.deactivate")}</Button>}
          {canDelete && <Button variant="outline" className="h-7 text-xs text-rose-600 hover:bg-rose-50" disabled={bulk.isPending} onClick={() => runBulk("delete")}><Trash2 className="h-3.5 w-3.5" /> {t("common.delete")}</Button>}
          <button onClick={clear} className="ms-auto text-slate-400 hover:text-slate-600">{t("common.clear")}</button>
        </div>
      )}

      {isLoading ? <p className="p-4 text-sm text-slate-400">{t("common.loading")}</p>
        : error ? <p className="p-4 text-sm text-rose-600">{(error as Error).message}</p>
        : !rows.length ? <p className="p-4 text-sm text-slate-400">{t("resources.none")}</p>
        : (
          <TableScroll>
          <table className="w-full min-w-[34rem] text-sm">
            <thead className="bg-slate-50 text-start text-xs text-slate-500">
              <tr>
                {canSelect && <th className="px-3 py-2"><input type="checkbox" aria-label={t("common.add")} checked={allChecked} onChange={toggleAll} /></th>}
                <th className="px-4 py-2">{t("resources.colCode")}</th><th className="px-4 py-2">{t("resources.colName")}</th><th className="px-4 py-2">{t("resources.colUnit")}</th><th className="px-4 py-2 text-end">{t(cfg.rateLabelKey)}</th><th className="px-2 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id} className={`border-t border-[var(--border)] ${selected.has(r.id) ? "bg-[var(--brand)]/5" : ""} ${r.isActive ? "" : "text-slate-400"}`}>
                  {canSelect && <td className="px-3 py-2"><input type="checkbox" aria-label={`Select ${r.code}`} checked={selected.has(r.id)} onChange={() => toggle(r.id)} /></td>}
                  <td className="px-4 py-2 font-mono text-xs">{r.code}{!r.isActive && <span className="ms-1 rounded bg-slate-100 px-1 text-[10px] text-slate-500">{t("resources.inactive")}</span>}</td>
                  <td className="px-4 py-2">{r.name}</td>
                  <td className="px-4 py-2 text-slate-500">{r.unit}</td>
                  <td className="px-4 py-2 text-end">
                    <span className="inline-flex items-center justify-end gap-1">
                      <Money value={(r[cfg.rateField] as number) ?? 0} currency="AED" />
                      <RateTrendCell apiType={cfg.apiType} id={r.id} />
                    </span>
                  </td>
                  <td className="px-2 py-2">
                    <div className="flex justify-end gap-1">
                      {canEdit && <button onClick={() => setEditing(r)} className="rounded p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700"><Pencil className="h-3.5 w-3.5" /></button>}
                      {canDelete && <button onClick={() => { if (confirm(t("resources.deleteConfirm", { code: r.code }))) del.mutate(r.id) }} className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
                      {!canEdit && !canDelete && <span className="text-xs text-slate-300">—</span>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </TableScroll>
        )}
      {(adding || editing) && (
        <ResourceEditor cfg={cfg} row={editing} onClose={() => { setAdding(false); setEditing(null) }} />
      )}
    </Card>
  )
}

function ResourceEditor({ cfg, row, onClose }: { cfg: TabCfg; row: ResourceRow | null; onClose: () => void }) {
  const qc = useQueryClient()
  const { t, locale } = useI18n()
  const editing = row !== null
  const [f, setF] = useState({
    code: row?.code ?? "",
    name: row?.name ?? "",
    unit: row?.unit ?? (cfg.rateField === "ratePerHour" ? "hr" : ""),
    rate: String((row?.[cfg.rateField] as number | undefined) ?? ""),
    wastagePct: String(row?.wastagePct ?? "0"),
    supplier: row?.supplier ?? "",
    isActive: row?.isActive ?? true,
  })
  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value })

  // ── AI rate suggestion state ──────────────────────────────────────────────
  const [suggestion, setSuggestion] = useState<{ loading: boolean; data: RateSuggestion | null; error: string | null } | null>(null)

  async function fetchSuggestion() {
    const name = f.name.trim()
    const unit = (f.unit || cfg.rateField === "ratePerHour" ? "hr" : "unit").trim()
    if (!name) { toast.error(t("resources.aiEnterName")); return }
    setSuggestion({ loading: true, data: null, error: null })
    try {
      const params = new URLSearchParams({ name, unit, type: cfg.apiType })
      const data = await fetchApi<RateSuggestion>(`/api/ai/rate-suggestion?${params}`)
      setSuggestion({ loading: false, data, error: null })
    } catch (e) {
      setSuggestion({ loading: false, data: null, error: (e as Error).message })
    }
  }

  function applyRate(rate: number) {
    setF((prev) => ({ ...prev, rate: String(rate) }))
    setSuggestion(null)
    toast.success(t("resources.rateApplied"))
  }

  const mut = useMutation({
    mutationFn: () => {
      const body: Record<string, unknown> = { code: f.code, name: f.name, unit: f.unit, isActive: f.isActive }
      body[cfg.rateField] = Number(f.rate || 0)
      if (cfg.material) { body.wastagePct = Number(f.wastagePct || 0); body.supplier = f.supplier || null }
      return fetchApi(editing ? `${cfg.path}/${row!.id}` : cfg.path, {
        method: editing ? "PUT" : "POST",
        body: JSON.stringify(body),
      })
    },
    onSuccess: () => { toast.success(editing ? t("resources.updated") : t("resources.created")); qc.invalidateQueries({ queryKey: ["res", cfg.path] }); onClose() },
    onError: (e) => toast.error((e as Error).message),
  })

  const confidenceChip = (c: string) => {
    const cls = c === "high" ? "bg-emerald-100 text-emerald-700" : c === "medium" ? "bg-amber-100 text-amber-700" : "bg-slate-100 text-slate-500"
    return <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${cls}`}>{c}</span>
  }

  return (
    <Modal open onClose={onClose} title={t(editing ? "resources.editTitle" : "resources.addTitle", { kind: t(cfg.labelKey) })}>
      <form id="res-form" onSubmit={(e) => { e.preventDefault(); mut.mutate() }} className="space-y-3">
        <div className="grid grid-cols-2 gap-3">
          <Field label={`${t("resources.fCode")} *`}><Input value={f.code} onChange={set("code")} disabled={editing} required /></Field>
          <Field label={t("resources.fUnit")}><Input value={f.unit} onChange={set("unit")} /></Field>
        </div>
        <Field label={`${t("resources.fName")} *`}><Input value={f.name} onChange={set("name")} required /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={`${t(cfg.rateLabelKey)} *`}>
            {/* Rate input row with AI suggest button */}
            <div className="flex items-center gap-2">
              <Input type="number" step="0.0001" min={0} value={f.rate} onChange={set("rate")} required className="flex-1" />
              <button
                type="button"
                title={t("resources.aiTitle")}
                disabled={suggestion?.loading}
                onClick={fetchSuggestion}
                className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md border border-[var(--border)] text-slate-400 transition hover:border-[var(--brand)] hover:text-[var(--brand)] disabled:opacity-40"
              >
                <Sparkles className="h-4 w-4" />
              </button>
            </div>
          </Field>
          {cfg.material && <Field label={t("resources.fWastage")}><Input type="number" step="0.01" min={0} value={f.wastagePct} onChange={set("wastagePct")} /></Field>}
        </div>

        {/* AI suggestion panel — shown after the button is clicked */}
        {suggestion !== null && (
          <div className="rounded-md border border-[var(--border)] bg-slate-50 p-3 text-xs">
            {suggestion.loading && (
              <p className="flex items-center gap-2 text-slate-500">
                <Sparkles className="h-3.5 w-3.5 animate-pulse text-[var(--brand)]" /> {t("resources.aiFetching")}
              </p>
            )}
            {suggestion.error && <p className="text-rose-600">{suggestion.error}</p>}
            {suggestion.data && (() => {
              const s = suggestion.data
              return (
                <>
                  <div className="mb-2 flex items-center justify-between">
                    <span className="font-medium text-slate-700">
                      {t("resources.aiSuggested")} <Money className="font-mono" value={s.suggestedRate} currency="AED" />
                    </span>
                    <div className="flex items-center gap-2">
                      {confidenceChip(s.confidence)}
                      <span className="text-slate-400">{s.basis}</span>
                    </div>
                  </div>
                  {s.comparables.length > 0 && (
                    <ul className="mb-2 space-y-0.5">
                      {s.comparables.slice(0, 5).map((c, i) => (
                        <li key={i} className="flex items-center justify-between text-slate-500">
                          <span className="truncate max-w-[55%]">{c.name}</span>
                          <span className="font-mono text-slate-700"><Money value={c.rate} currency="AED" />/{c.unit}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                  {s.suggestedRate > 0 && (
                    <button
                      type="button"
                      onClick={() => applyRate(s.suggestedRate)}
                      className="flex items-center gap-1 rounded-md bg-[var(--brand)] px-2.5 py-1 text-white hover:opacity-90"
                    >
                      <Check className="h-3 w-3" /> {t("resources.aiApply", { amount: money(s.suggestedRate, "AED", locale) })}
                    </button>
                  )}
                  {s.suggestedRate === 0 && (
                    <p className="text-slate-400">{t("resources.aiNone")}</p>
                  )}
                </>
              )
            })()}
          </div>
        )}

        {cfg.material && <Field label={t("resources.fSupplier")}><Input value={f.supplier} onChange={set("supplier")} /></Field>}
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>{t("common.cancel")}</Button>
        <Button type="submit" form="res-form" disabled={mut.isPending}>{mut.isPending ? t("common.saving") : t("common.save")}</Button>
      </div>
    </Modal>
  )
}
