"use client"
import { useEffect, useMemo, useRef, useState } from "react"
import { Layers, Plus, Trash2 } from "lucide-react"
import type { Area, AssemblyRow, CostComponentType, ItemBreakdown, SectionBreakdown } from "@/lib/types"
import { Button, Input } from "@/components/ui"
import { Field, Select } from "@/components/form"
import { money, cn } from "@/lib/utils"
import { Money } from "@/components/money"
import { CollapseToggle, ITEM_KINDS, kindLabel, type CompInput } from "./shared"
import { BuildUpModal } from "./build-up-modal"

/** Threshold above which the section uses the virtualized row list. Anything
 *  smaller doesn't justify the windowing overhead — a 50-row section is fine
 *  as a plain <table>. 150 is comfortably below the point where React DevTools
 *  starts noticing the cost on a typical office laptop. */
const VIRTUALIZE_THRESHOLD = 150
/** Pixel height we estimate per BOQ row. The actual row is taller when the
 *  cost build-up is shown inline, but the virtualizer only needs an
 *  approximation — it positions windows of rows, not pixel-perfect layout. */
const ROW_ESTIMATED_PX = 56
/** How many rows past the viewport to render on each side. A buffer is
 *  cheap (rows are cheap) and avoids visible "tearing" on fast scrolls. */
const OVERSCAN = 12

/** One BOQ section: collapsible header + table of items + add-item form.
 *  For very long sections (> VIRTUALIZE_THRESHOLD rows) we switch to a
 *  windowed row renderer so a 5k-row section doesn't blow up the page. */
export function SectionBlock({ section, currency, assemblies, costTypes, areas, open, onToggle, canAdd, canEdit, canDelete, onAddItem, onUpdItem, onDelItem, onDelSection }: {
  section: SectionBreakdown; currency: string; assemblies: AssemblyRow[]; costTypes: CostComponentType[]; areas: Area[]
  open: boolean; onToggle: () => void
  canAdd: boolean; canEdit: boolean; canDelete: boolean
  onAddItem: (v: unknown) => void; onUpdItem: (v: unknown) => void; onDelItem: (iid: number) => void; onDelSection: () => void
}) {
  const longList = section.items.length > VIRTUALIZE_THRESHOLD
  return (
    <div className="border-t border-[var(--border)]">
      <div className="flex items-center justify-between bg-slate-50/60 px-4 py-2">
        <span className="flex items-center gap-1.5 font-semibold text-slate-700">
          <CollapseToggle open={open} hasChildren={section.items.length > 0} onToggle={onToggle} />
          {section.code} {section.title}
          {!open && section.items.length > 0 && <span className="text-xs font-normal text-muted">· {section.items.length} item(s)</span>}
        </span>
        <div className="flex items-center gap-3">
          <Money className="font-semibold" value={section.sectionTotal} currency={currency} />
          {canDelete && <button onClick={onDelSection} className="rounded p-1 text-muted hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
        </div>
      </div>
      {open && (
        <>
          {longList
            ? <VirtualizedItemList items={section.items} currency={currency} costTypes={costTypes} areas={areas} canEdit={canEdit} canDelete={canDelete} onUpd={onUpdItem} onDel={onDelItem} />
            : (
              <table className="w-full text-sm">
                <tbody>
                  {section.items.map((i) => (
                    <ItemRow key={i.id} item={i} currency={currency} costTypes={costTypes} areas={areas} canEdit={canEdit} canDelete={canDelete} onUpd={onUpdItem} onDel={() => onDelItem(i.id)} />
                  ))}
                </tbody>
              </table>
            )}
          {canAdd && <AddItemForm assemblies={assemblies} costTypes={costTypes} areas={areas} onAdd={onAddItem} />}
        </>
      )}
    </div>
  )
}

/** Custom windowed list — 19.6 perf win for large BOQs. Renders ONLY the
 *  rows currently in (or near) the viewport. We measure scroll/viewport on
 *  the scroll container, compute first/last visible row indices from the
 *  estimated row height, then spacer-pad the top + bottom so the scrollbar
 *  matches the full list height. Pure DOM math, no extra dependency.
 *
 *  Why not @tanstack/react-virtual? For a single fixed-height row table this
 *  is ~40 lines and zero install — fewer moving parts. If we later need
 *  variable heights or window-of-windows behavior, swap this for the lib. */
function VirtualizedItemList({ items, currency, costTypes, areas, canEdit, canDelete, onUpd, onDel }: {
  items: ItemBreakdown[]; currency: string; costTypes: CostComponentType[]; areas: Area[]
  canEdit: boolean; canDelete: boolean; onUpd: (v: unknown) => void; onDel: (iid: number) => void
}) {
  const scrollRef = useRef<HTMLDivElement>(null)
  const [scrollTop, setScrollTop] = useState(0)
  const [viewportH, setViewportH] = useState(600)   // sane default until layout settles
  useEffect(() => {
    const el = scrollRef.current
    if (!el) return
    const onScroll = () => setScrollTop(el.scrollTop)
    const onResize = () => setViewportH(el.clientHeight)
    onResize()
    el.addEventListener("scroll", onScroll, { passive: true })
    const ro = typeof ResizeObserver !== "undefined" ? new ResizeObserver(onResize) : null
    ro?.observe(el)
    return () => { el.removeEventListener("scroll", onScroll); ro?.disconnect() }
  }, [])
  const { startIdx, endIdx, padTop, padBottom } = useMemo(() => {
    const first = Math.max(0, Math.floor(scrollTop / ROW_ESTIMATED_PX) - OVERSCAN)
    const visibleCount = Math.ceil(viewportH / ROW_ESTIMATED_PX) + OVERSCAN * 2
    const last = Math.min(items.length, first + visibleCount)
    return {
      startIdx: first,
      endIdx: last,
      padTop: first * ROW_ESTIMATED_PX,
      padBottom: (items.length - last) * ROW_ESTIMATED_PX,
    }
  }, [scrollTop, viewportH, items.length])
  const slice = items.slice(startIdx, endIdx)
  return (
    <div className="relative">
      <div className="border-b border-dashed border-amber-200 bg-amber-50/60 px-4 py-1 text-[11px] text-amber-800">
        Showing {slice.length} of {items.length} rows (virtualized)
      </div>
      <div ref={scrollRef} className="max-h-[60vh] overflow-y-auto" data-testid="boq-virtual-scroll">
        <div style={{ paddingTop: padTop, paddingBottom: padBottom }}>
          <table className="w-full text-sm">
            <tbody>
              {slice.map((i) => (
                <ItemRow key={i.id} item={i} currency={currency} costTypes={costTypes} areas={areas} canEdit={canEdit} canDelete={canDelete} onUpd={onUpd} onDel={() => onDel(i.id)} />
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  )
}

export function ItemRow({ item, currency, costTypes, areas, canEdit, canDelete, onUpd, onDel }: { item: ItemBreakdown; currency: string; costTypes: CostComponentType[]; areas: Area[]; canEdit: boolean; canDelete: boolean; onUpd: (v: unknown) => void; onDel: () => void }) {
  const adHoc = item.assemblyId == null
  const hasComps = item.components.length > 0
  const [buildup, setBuildup] = useState(false)
  // IMPORTANT: carry the current components so quantity/area edits don't wipe the build-up
  // (the PUT replaces components wholesale).
  const base = {
    id: item.id, itemCode: item.itemCode, description: item.description, unit: item.unit,
    assemblyId: item.assemblyId, unitRate: item.unitRate, sortOrder: item.sortOrder, areaId: item.areaId, kind: item.kind,
    components: hasComps ? item.components.map((c) => ({ typeId: c.typeId, value: c.value, quantity: c.quantity ?? undefined, rate: c.rate ?? undefined })) : undefined,
  }
  const areaName = areas.find((a) => a.id === item.areaId)?.name
  return (
    <tr className="border-t border-[var(--border)]">
      <td className="px-4 py-1.5">
        {item.description}
        {item.kind !== "Normal" && <span className="ml-2 rounded bg-amber-100 px-1.5 py-0.5 text-[10px] font-medium text-amber-700">{kindLabel(item.kind)}</span>}
        {hasComps && (
          <div className="text-xs text-muted">
            {item.components.map((c) => `${c.code} ${c.calcKind === "Percent" ? c.value + "%" : (c.quantity != null && c.rate != null ? `${c.quantity}×${c.rate}` : c.value)}`).join(" + ")}
          </div>
        )}
        <div className="mt-1 flex items-center gap-1">
          {canEdit && areas.length > 0
            ? <select value={item.areaId ?? ""} onChange={(ev) => onUpd({ ...base, quantity: item.quantity, areaId: ev.target.value === "" ? null : Number(ev.target.value) })}
                      className="rounded border border-[var(--border)] bg-white px-1 py-0.5 text-xs text-slate-500">
                <option value="">— no area —</option>
                {areas.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
              </select>
            : areaName && <span className="text-xs text-muted">📍 {areaName}</span>}
          {canEdit && (
            <select value={item.kind} onChange={(ev) => onUpd({ ...base, quantity: item.quantity, kind: ev.target.value })}
                    title="Line kind" className="rounded border border-[var(--border)] bg-white px-1 py-0.5 text-xs text-slate-500">
              {ITEM_KINDS.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
            </select>
          )}
        </div>
      </td>
      <td className="px-2 py-1.5 text-slate-500">{item.unit}</td>
      <td className="px-2 py-1.5 text-right">
        {canEdit
          ? <Input className="w-20 py-1 text-right" type="number" step="0.0001" defaultValue={item.quantity}
              onBlur={(ev) => { const q = Number(ev.target.value); if (q !== item.quantity) onUpd({ ...base, quantity: q }) }} />
          : <span className="text-slate-600">{item.quantity}</span>}
      </td>
      <td className="px-2 py-1.5 text-right">
        {adHoc && canEdit && !hasComps
          ? <Input className="w-24 py-1 text-right" type="number" step="0.0001" defaultValue={item.unitRate}
              onBlur={(ev) => { const r = Number(ev.target.value); if (r !== item.unitRate) onUpd({ ...base, quantity: item.quantity, unitRate: r }) }} />
          : <Money className="text-slate-600" value={item.unitRate} currency={currency} />}
      </td>
      <td className="px-4 py-1.5 text-right font-medium"><Money value={item.lineTotal} currency={currency} /></td>
      <td className="px-2 py-1.5 text-right">
        <div className="flex items-center justify-end gap-1">
          {adHoc && canEdit && (
            <button onClick={() => setBuildup(true)} title="Unit-rate build-up"
                    className={cn("rounded p-1 hover:bg-slate-100", hasComps ? "text-[var(--brand)]" : "text-muted")}>
              <Layers className="h-3.5 w-3.5" />
            </button>
          )}
          {canDelete && <button onClick={onDel} className="rounded p-1 text-muted hover:bg-rose-50 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
        </div>
      </td>
      {buildup && (
        <BuildUpModal open={buildup} onClose={() => setBuildup(false)} costTypes={costTypes} currency={currency}
          initial={item.components.map((c) => ({ typeId: c.typeId, value: c.value, quantity: c.quantity ?? undefined, rate: c.rate ?? undefined }))}
          onSave={(comps) => onUpd({ ...base, quantity: item.quantity, components: comps })} />
      )}
    </tr>
  )
}

export function AddItemForm({ assemblies, costTypes, areas, onAdd }: { assemblies: AssemblyRow[]; costTypes: CostComponentType[]; areas: Area[]; onAdd: (v: unknown) => void }) {
  const BUILDUP = "__buildup__"
  const [f, setF] = useState({ description: "", unit: "", quantity: "1", assemblyId: "", unitRate: "0", areaId: "", kind: "Normal" })
  const [comps, setComps] = useState<CompInput[]>([])
  const [modal, setModal] = useState(false)
  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setF({ ...f, [k]: e.target.value })
  const mode = f.assemblyId   // "" ad-hoc | BUILDUP | assembly id
  const useAsm = mode !== "" && mode !== BUILDUP
  const useBuildup = mode === BUILDUP
  function submit() {
    if (!f.description) return
    onAdd({
      description: f.description, unit: f.unit, quantity: Number(f.quantity || 0),
      assemblyId: useAsm ? Number(f.assemblyId) : null,
      unitRate: useAsm || useBuildup ? 0 : Number(f.unitRate || 0),
      components: useBuildup ? comps : undefined,
      areaId: f.areaId === "" ? null : Number(f.areaId), kind: f.kind, sortOrder: 0,
    })
    setF({ description: "", unit: "", quantity: "1", assemblyId: "", unitRate: "0", areaId: f.areaId, kind: "Normal" }); setComps([])
  }
  return (
    <div className="grid grid-cols-[1fr_60px_60px_1fr_90px_110px_110px_auto] items-end gap-2 bg-slate-50 px-4 py-3 text-sm">
      <Field label="Description"><Input value={f.description} onChange={set("description")} /></Field>
      <Field label="Unit"><Input value={f.unit} onChange={set("unit")} /></Field>
      <Field label="Qty"><Input type="number" step="0.0001" value={f.quantity} onChange={set("quantity")} /></Field>
      <Field label="Pricing">
        <Select value={f.assemblyId} onChange={set("assemblyId")}>
          <option value="">— ad-hoc rate —</option>
          <option value={BUILDUP}>— cost build-up —</option>
          {assemblies.map((a) => <option key={a.id} value={a.id}>{a.code} ({money(a.computedRate)})</option>)}
        </Select>
      </Field>
      {useBuildup
        ? <Field label="Build-up"><Button variant="outline" className="h-9 w-full text-xs" onClick={() => setModal(true)}><Layers className="h-3.5 w-3.5" /> {comps.length ? `${comps.length} parts` : "Define"}</Button></Field>
        : <Field label="Rate"><Input type="number" step="0.0001" value={f.unitRate} onChange={set("unitRate")} disabled={useAsm} /></Field>}
      <Field label="Area">
        <Select value={f.areaId} onChange={set("areaId")}>
          <option value="">— none —</option>
          {areas.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
        </Select>
      </Field>
      <Field label="Kind">
        <Select value={f.kind} onChange={set("kind")}>
          {ITEM_KINDS.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
        </Select>
      </Field>
      <Button variant="outline" onClick={submit}><Plus className="h-4 w-4" /></Button>
      {modal && (
        <BuildUpModal open={modal} onClose={() => setModal(false)} costTypes={costTypes} currency=""
          initial={comps} onSave={(c) => setComps(c)} />
      )}
    </div>
  )
}

/** "+ Section" inline form that opens to a tiny code/title pair. */
export function AddSection({ onAdd }: { onAdd: (v: { code: string; title: string }) => void }) {
  const [open, setOpen] = useState(false)
  const [code, setCode] = useState("")
  const [title, setTitle] = useState("")
  if (!open) return <Button variant="ghost" className="h-7 px-2 text-xs" onClick={() => setOpen(true)}><Plus className="h-3.5 w-3.5" /> Section</Button>
  return (
    <div className="flex items-center gap-2">
      <Input className="w-20 py-1" placeholder="Code" value={code} onChange={(e) => setCode(e.target.value)} />
      <Input className="w-48 py-1" placeholder="Title" value={title} onChange={(e) => setTitle(e.target.value)} />
      <Button className="h-8 px-3 text-xs" onClick={() => { if (title) { onAdd({ code, title }); setCode(""); setTitle(""); setOpen(false) } }}>Add</Button>
    </div>
  )
}
