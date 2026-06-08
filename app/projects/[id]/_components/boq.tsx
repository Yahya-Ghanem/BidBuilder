"use client"
import { useCallback, useMemo, useState } from "react"
import { Layers, MessageSquare, Plus, Trash2 } from "lucide-react"
import type { Area, AssemblyRow, CostComponentType, ItemBreakdown, SectionBreakdown } from "@/lib/types"
import { Button, Input } from "@/components/ui"
import { Field, Select } from "@/components/form"
import { money, cn } from "@/lib/utils"
import { Money } from "@/components/money"
import { useT } from "@/lib/i18n"
import { DataTable, type ColumnDef } from "@/components/data-table"
import { CollapseToggle, ITEM_KINDS, kindLabel, type CompInput } from "./shared"
import { BuildUpModal } from "./build-up-modal"

/** 26.4 — Threshold above which a section virtualizes its rows. Matches the
 *  pre-26.4 custom virtualizer threshold; below this, paying the windowing
 *  cost (extra wrapper div, scroll listener, padding spacers) is net-
 *  negative. The `<DataTable>` primitive now owns the virtualization itself
 *  (via `@tanstack/react-virtual`) so this number just decides when to flip
 *  it on. */
const VIRTUALIZE_THRESHOLD = 150
/** Estimated row height in px. The actual row is taller when the cost
 *  build-up legend is shown inline; the virtualizer re-measures on mount
 *  so the displayed positions correct themselves regardless. */
const ROW_ESTIMATED_PX = 56

/** Shared edit-flag bundle for the per-cell renderers. Passed through the
 *  column factory so each cell stays a closure-free pure component. */
type CellDeps = {
  currency: string
  costTypes: CostComponentType[]
  areas: Area[]
  canEdit: boolean
  canDelete: boolean
  onUpd: (v: unknown) => void
  onDel: (iid: number) => void
  /** 27.1 — per-item open-comments count for the row-level icon badge. */
  commentCounts: Map<number, number>
  /** 27.1 — open the side-panel thread for one BOQ line. */
  onOpenComments: (item: ItemBreakdown) => void
  /** 27.1 — i18n'd aria-label for the comment-icon button. */
  commentsLabel: string
}

/** One BOQ section: collapsible header + DataTable of items + add-item form.
 *  Long sections (> VIRTUALIZE_THRESHOLD rows) automatically virtualize via
 *  the DataTable wrapper. */
export function SectionBlock({ section, currency, assemblies, costTypes, areas, open, onToggle, canAdd, canEdit, canDelete, commentCounts, onOpenComments, onAddItem, onUpdItem, onDelItem, onDelSection, onSelectionChange, selectionResetKey }: {
  section: SectionBreakdown; currency: string; assemblies: AssemblyRow[]; costTypes: CostComponentType[]; areas: Area[]
  open: boolean; onToggle: () => void
  canAdd: boolean; canEdit: boolean; canDelete: boolean
  /** 27.1 — open-comments map (itemId → count) for the per-row icon badge. */
  commentCounts: Map<number, number>
  /** 27.1 — open the comment thread for a BOQ line. */
  onOpenComments: (item: ItemBreakdown) => void
  onAddItem: (v: unknown) => void; onUpdItem: (v: unknown) => void; onDelItem: (iid: number) => void; onDelSection: () => void
  /** 28.2 — Multi-select: caller receives THIS section's id + its selected
   *  item ids on every change. Omitted → no checkbox column rendered (read-
   *  only viewers). Item ids are integers (DataTable's stringified row ids
   *  are unwrapped here so the parent doesn't have to round-trip Number()).
   *  Carries `sectionId` so the parent can pass ONE stable handler instead
   *  of N section-specific arrows — important: a new arrow per render would
   *  re-run the DataTable's effect and refire the callback every paint. */
  onSelectionChange?: (sectionId: number, selectedItemIds: number[]) => void
  /** 28.2 — Bump after a successful bulk action to clear the checkboxes. */
  selectionResetKey?: string | number
}) {
  const t = useT()
  const commentsLabel = t("ed.comments.openAria")
  // 28.2 — Stable handler so the DataTable's effect deps don't change every
  // paint. `onSelectionChange` is the parent's stable callback; `section.id`
  // is stable per section. Together they yield a per-section function whose
  // identity changes only when the parent swaps the callback or the section
  // identity changes — neither of which happens during interaction. We always
  // build the inner fn through useCallback (TS infers a clean signature) and
  // only expose it to DataTable when the parent supplied a handler.
  const sectionId = section.id
  const stableHandler = useCallback((sel: Set<string>) => {
    if (!onSelectionChange) return
    const ids: number[] = []
    for (const s of sel) { const n = Number(s); if (Number.isFinite(n)) ids.push(n) }
    onSelectionChange(sectionId, ids)
  }, [onSelectionChange, sectionId])
  const selectionHandler = onSelectionChange ? stableHandler : undefined
  // Memoize the column defs so the table doesn't rebuild on every parent
  // render — TanStack Table internals compare column identity to decide
  // whether to recompute the model.
  const columns = useMemo<ColumnDef<ItemBreakdown, unknown>[]>(
    () => boqColumns({ currency, costTypes, areas, canEdit, canDelete, onUpd: onUpdItem, onDel: onDelItem, commentCounts, onOpenComments, commentsLabel }),
    [currency, costTypes, areas, canEdit, canDelete, onUpdItem, onDelItem, commentCounts, onOpenComments, commentsLabel],
  )
  const longList = section.items.length > VIRTUALIZE_THRESHOLD
  const virtBanner = longList && (
    <div className="border-b border-dashed border-warning/30 bg-warning-soft px-4 py-1 text-[11px] text-warning">
      Virtualized — {section.items.length} rows
    </div>
  )
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
          {canDelete && <button onClick={onDelSection} className="rounded p-1 text-muted hover:bg-danger-soft hover:text-danger"><Trash2 className="h-3.5 w-3.5" /></button>}
        </div>
      </div>
      {open && (
        <>
          <DataTable
            data={section.items}
            columns={columns}
            getRowId={(row) => String(row.id)}
            // Sticky thead (26.4 acceptance) gives a visual anchor when the
            // section is long; previously BOQ had no thead at all.
            showHeader
            stickyHeader
            virtualizeThreshold={VIRTUALIZE_THRESHOLD}
            rowEstimatedPx={ROW_ESTIMATED_PX}
            // Preserve the original data-testid so the existing virtualized-
            // scroll E2E selector keeps working post-migration.
            testId="boq-virtual-scroll"
            banner={virtBanner}
            tableClassName="text-sm"
            // 28.2 — Translate the DataTable's Set<string> back to numeric ids
            // (its public contract is stringified row ids). Pass-through to
            // the parent so it can union sections' selections behind one bar.
            // The inner arrow is memoized so its identity is stable across
            // renders — otherwise the DataTable's effect (which lists
            // onSelectionChange in its deps) would refire every paint.
            onSelectionChange={selectionHandler}
            selectionResetKey={selectionResetKey}
          />
          {canAdd && <AddItemForm assemblies={assemblies} costTypes={costTypes} areas={areas} onAdd={onAddItem} />}
        </>
      )}
    </div>
  )
}

/** Column factory. Built outside the component so the deps memoization in
 *  SectionBlock has a stable function to call. Each cell is a small inline
 *  closure rather than a separate component to keep the column structure
 *  visible in one place — readability beats one-extra-component per cell.
 *
 *  Column order matches the pre-26.4 layout: Description, Unit, Qty, Rate,
 *  Total, Actions. The description column is rich (kind badge + cost build-
 *  up legend + area/kind selects) and gets a larger `size` hint to push the
 *  numeric columns into a tighter right-aligned cluster. */
function boqColumns({ currency, costTypes, areas, canEdit, canDelete, onUpd, onDel, commentCounts, onOpenComments, commentsLabel }: CellDeps): ColumnDef<ItemBreakdown, unknown>[] {
  return [
    {
      id: "description",
      header: "Description",
      cell: ({ row }) => <DescriptionCell item={row.original} {...{ areas, canEdit, onUpd }} />,
    },
    {
      id: "unit",
      header: "Unit",
      size: 80,
      cell: ({ row }) => <span className="text-slate-500">{row.original.unit}</span>,
    },
    {
      id: "qty",
      header: "Qty",
      size: 96,
      cell: ({ row }) => <QtyCell item={row.original} canEdit={canEdit} onUpd={onUpd} />,
    },
    {
      id: "rate",
      header: "Rate",
      size: 120,
      cell: ({ row }) => <RateCell item={row.original} currency={currency} canEdit={canEdit} onUpd={onUpd} />,
    },
    {
      id: "total",
      header: "Total",
      size: 120,
      cell: ({ row }) => (
        <div className="text-right">
          <Money className="font-medium" value={row.original.lineTotal} currency={currency} />
        </div>
      ),
    },
    {
      id: "actions",
      // Empty header keeps the column width but reads "no action available"
      // as the accessible label via aria-label on the cell-level buttons.
      header: "",
      size: 80,
      cell: ({ row }) => <ActionsCell item={row.original} costTypes={costTypes} currency={currency} canEdit={canEdit} canDelete={canDelete} onUpd={onUpd} onDel={() => onDel(row.original.id)} />,
    },
    {
      // 27.1 — Trailing comment column: an icon-button with an unread-style
      // badge when the row has open comments. The icon is ALWAYS visible
      // (anyone with boq.view can read a thread), independent of the can*
      // edit flags — read-only viewers can still see the discussion.
      id: "comments",
      header: "",
      size: 44,
      cell: ({ row }) => <CommentsCell item={row.original} commentCounts={commentCounts} onOpenComments={onOpenComments} commentsLabel={commentsLabel} />,
    },
  ]
}

function CommentsCell({ item, commentCounts, onOpenComments, commentsLabel }: {
  item: ItemBreakdown
  commentCounts: Map<number, number>
  onOpenComments: (item: ItemBreakdown) => void
  commentsLabel: string
}) {
  const count = commentCounts.get(item.id) ?? 0
  return (
    <div className="flex items-center justify-end">
      <button
        type="button"
        onClick={() => onOpenComments(item)}
        title={commentsLabel}
        aria-label={count > 0 ? `${commentsLabel} (${count})` : commentsLabel}
        className={cn(
          "relative rounded p-1 hover:bg-slate-100",
          count > 0 ? "text-[var(--brand)]" : "text-muted",
        )}
      >
        <MessageSquare className="h-3.5 w-3.5" />
        {count > 0 && (
          <span className="absolute -end-0.5 -top-0.5 grid min-w-[14px] place-items-center rounded-full bg-rose-600 px-0.5 text-[9px] font-bold leading-[14px] text-white">
            {count > 9 ? "9+" : count}
          </span>
        )}
      </button>
    </div>
  )
}

/** Description column — the rich one. Renders the line description, an
 *  optional kind badge (Provisional / PC / Alternate / Daywork), the cost
 *  build-up legend (`Material 50 + Labour 5×80 + Daywork 20%`), and the
 *  area+kind selects beneath. Most of the BOQ "feel" lives in this cell. */
function DescriptionCell({ item, areas, canEdit, onUpd }: { item: ItemBreakdown; areas: Area[]; canEdit: boolean; onUpd: (v: unknown) => void }) {
  const hasComps = item.components.length > 0
  const base = baseUpd(item)
  const areaName = areas.find((a) => a.id === item.areaId)?.name
  return (
    <div>
      <div>
        {item.description}
        {item.kind !== "Normal" && <span className="ml-2 rounded bg-warning-soft px-1.5 py-0.5 text-[10px] font-medium text-warning">{kindLabel(item.kind)}</span>}
      </div>
      {hasComps && (
        <div className="text-xs text-muted">
          {item.components.map((c) => `${c.code} ${c.calcKind === "Percent" ? c.value + "%" : (c.quantity != null && c.rate != null ? `${c.quantity}×${c.rate}` : c.value)}`).join(" + ")}
        </div>
      )}
      <div className="mt-1 flex items-center gap-1">
        {canEdit && areas.length > 0
          ? <select value={item.areaId ?? ""} onChange={(ev) => onUpd({ ...base, quantity: item.quantity, areaId: ev.target.value === "" ? null : Number(ev.target.value) })}
                    aria-label="Area"
                    className="rounded border border-[var(--border)] bg-white px-1 py-0.5 text-xs text-slate-500">
              <option value="">— no area —</option>
              {areas.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
            </select>
          : areaName && <span className="text-xs text-muted">📍 {areaName}</span>}
        {canEdit && (
          <select value={item.kind} onChange={(ev) => onUpd({ ...base, quantity: item.quantity, kind: ev.target.value })}
                  title="Line kind" aria-label="Line kind"
                  className="rounded border border-[var(--border)] bg-white px-1 py-0.5 text-xs text-slate-500">
            {ITEM_KINDS.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
          </select>
        )}
      </div>
    </div>
  )
}

function QtyCell({ item, canEdit, onUpd }: { item: ItemBreakdown; canEdit: boolean; onUpd: (v: unknown) => void }) {
  const base = baseUpd(item)
  return (
    <div className="text-right">
      {canEdit
        ? <Input className="w-20 py-1 text-right" type="number" step="0.0001" defaultValue={item.quantity}
            aria-label="Quantity"
            onBlur={(ev) => { const q = Number(ev.target.value); if (q !== item.quantity) onUpd({ ...base, quantity: q }) }} />
        : <span className="text-slate-600">{item.quantity}</span>}
    </div>
  )
}

function RateCell({ item, currency, canEdit, onUpd }: { item: ItemBreakdown; currency: string; canEdit: boolean; onUpd: (v: unknown) => void }) {
  const adHoc = item.assemblyId == null
  const hasComps = item.components.length > 0
  const base = baseUpd(item)
  return (
    <div className="text-right">
      {adHoc && canEdit && !hasComps
        ? <Input className="w-24 py-1 text-right" type="number" step="0.0001" defaultValue={item.unitRate}
            aria-label="Unit rate"
            onBlur={(ev) => { const r = Number(ev.target.value); if (r !== item.unitRate) onUpd({ ...base, quantity: item.quantity, unitRate: r }) }} />
        : <Money className="text-slate-600" value={item.unitRate} currency={currency} />}
    </div>
  )
}

function ActionsCell({ item, costTypes, currency, canEdit, canDelete, onUpd, onDel }: { item: ItemBreakdown; costTypes: CostComponentType[]; currency: string; canEdit: boolean; canDelete: boolean; onUpd: (v: unknown) => void; onDel: () => void }) {
  const adHoc = item.assemblyId == null
  const hasComps = item.components.length > 0
  const [buildup, setBuildup] = useState(false)
  const base = baseUpd(item)
  return (
    <div className="flex items-center justify-end gap-1">
      {adHoc && canEdit && (
        <button onClick={() => setBuildup(true)} title="Unit-rate build-up" aria-label="Edit unit-rate build-up"
                className={cn("rounded p-1 hover:bg-slate-100", hasComps ? "text-[var(--brand)]" : "text-muted")}>
          <Layers className="h-3.5 w-3.5" />
        </button>
      )}
      {canDelete && <button onClick={onDel} aria-label="Delete row" className="rounded p-1 text-muted hover:bg-danger-soft hover:text-danger"><Trash2 className="h-3.5 w-3.5" /></button>}
      {buildup && (
        <BuildUpModal open={buildup} onClose={() => setBuildup(false)} costTypes={costTypes} currency={currency}
          initial={item.components.map((c) => ({ typeId: c.typeId, value: c.value, quantity: c.quantity ?? undefined, rate: c.rate ?? undefined }))}
          onSave={(comps) => onUpd({ ...base, quantity: item.quantity, components: comps })} />
      )}
    </div>
  )
}

/** Carry the current components alongside any edited fields. Quantity / area
 *  / kind edits must NOT wipe the build-up — the PUT replaces components
 *  wholesale. */
function baseUpd(item: ItemBreakdown) {
  const hasComps = item.components.length > 0
  return {
    id: item.id, itemCode: item.itemCode, description: item.description, unit: item.unit,
    assemblyId: item.assemblyId, unitRate: item.unitRate, sortOrder: item.sortOrder, areaId: item.areaId, kind: item.kind,
    components: hasComps ? item.components.map((c) => ({ typeId: c.typeId, value: c.value, quantity: c.quantity ?? undefined, rate: c.rate ?? undefined })) : undefined,
  }
}

/** ItemRow is kept as a thin export for legacy consumers that still reference
 *  it. Inside the BOQ it's no longer used directly — the DataTable handles
 *  row composition via the column factory above. */
export function ItemRow({ item, currency, costTypes, areas, canEdit, canDelete, onUpd, onDel }: { item: ItemBreakdown; currency: string; costTypes: CostComponentType[]; areas: Area[]; canEdit: boolean; canDelete: boolean; onUpd: (v: unknown) => void; onDel: () => void }) {
  return (
    <tr className="border-t border-[var(--border)]">
      <td className="px-4 py-1.5"><DescriptionCell item={item} areas={areas} canEdit={canEdit} onUpd={onUpd} /></td>
      <td className="px-2 py-1.5 text-slate-500">{item.unit}</td>
      <td className="px-2 py-1.5"><QtyCell item={item} canEdit={canEdit} onUpd={onUpd} /></td>
      <td className="px-2 py-1.5"><RateCell item={item} currency={currency} canEdit={canEdit} onUpd={onUpd} /></td>
      <td className="px-4 py-1.5"><div className="text-right"><Money className="font-medium" value={item.lineTotal} currency={currency} /></div></td>
      <td className="px-2 py-1.5"><ActionsCell item={item} costTypes={costTypes} currency={currency} canEdit={canEdit} canDelete={canDelete} onUpd={onUpd} onDel={onDel} /></td>
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
        ? <Field label="Build-up"><Button variant="outline" className="h-9 w-full text-sm" onClick={() => setModal(true)}><Layers className="h-3.5 w-3.5" /> {comps.length ? `${comps.length} parts` : "Define"}</Button></Field>
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
  if (!open) return <Button variant="ghost" className="h-7 px-2 text-sm" onClick={() => setOpen(true)}><Plus className="h-3.5 w-3.5" /> Section</Button>
  return (
    <div className="flex items-center gap-2">
      <Input className="w-20 py-1" placeholder="Code" value={code} onChange={(e) => setCode(e.target.value)} />
      <Input className="w-48 py-1" placeholder="Title" value={title} onChange={(e) => setTitle(e.target.value)} />
      <Button className="h-8 px-3 text-sm" onClick={() => { if (title) { onAdd({ code, title }); setCode(""); setTitle(""); setOpen(false) } }}>Add</Button>
    </div>
  )
}
