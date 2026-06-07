"use client"
import { useMemo, useState } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Copy, FileSpreadsheet, FileText, FolderTree, Layers, Plus, Table, Trash2 } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { usePermissions } from "@/lib/permissions"
import type { ActivityType, Area, CostComponentType, EstimateBreakdown, ItemBreakdown } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"
import { Field, Modal, Select } from "@/components/form"
import { Money } from "@/components/money"
import { CollapseToggle, ExpandCollapseAll, useCollapse, type CompInput } from "./shared"
import { BuildUpModal } from "./build-up-modal"

/** Unit-centric activities: the area tree with each unit's activities (BOQ items
 *  tagged to it) showing Material (M) + Manpower (L) + total, with add/edit/delete.
 *  Reuses BOQ items + the cost build-up, so everything flows into the bid. */
export function ActivitiesPanel({ breakdown, areas, costTypes, currency, canAdd, canEdit, canDelete, canExport, onExport, onAddActivity, onUpdItem, onDelItem, onCloneRoom }: {
  breakdown: EstimateBreakdown; areas: Area[]; costTypes: CostComponentType[]; currency: string
  canAdd: boolean; canEdit: boolean; canDelete: boolean
  canExport: boolean; onExport: (kind: "xlsx" | "csv" | "pdf", level: "detail" | "area" | "subarea" | "unit") => void
  onAddActivity: (areaId: number, v: { description: string; unit: string; components: CompInput[] }) => void
  onUpdItem: (v: unknown) => void; onDelItem: (iid: number) => void
  onCloneRoom: (areaId: number, name: string) => void
}) {
  const items = breakdown.sections.flatMap((s) => s.items)
  const byArea = (aid: number) => items.filter((it) => it.areaId === aid)
  const childrenOf = (id: number | null) => areas.filter((a) => a.parentAreaId === id)
  const [adding, setAdding] = useState<Area | null>(null)
  const [editing, setEditing] = useState<ItemBreakdown | null>(null)
  const [cloning, setCloning] = useState<Area | null>(null)
  // Export grouping: Detail = every activity; Area / Sub-Area / Unit = rolled-up summary at that level.
  const [level, setLevel] = useState<"detail" | "area" | "subarea" | "unit">("detail")
  const compAmount = (it: ItemBreakdown, code: string) => it.components.find((c) => c.code === code)?.amount ?? 0
  const editBase = (it: ItemBreakdown) => ({
    id: it.id, itemCode: it.itemCode, description: it.description, unit: it.unit, assemblyId: it.assemblyId,
    unitRate: it.unitRate, sortOrder: it.sortOrder, areaId: it.areaId, quantity: it.quantity,
  })
  // Collapsible = any area with children OR with activities (collapsing hides both).
  const collapsibleIds = useMemo(() => {
    const s = new Set<number>()
    for (const a of areas) if (areas.some((x) => x.parentAreaId === a.id) || items.some((it) => it.areaId === a.id)) s.add(a.id)
    return s
  }, [areas, items])
  const { toggle, isOpen, collapseAll, expandAll } = useCollapse(collapsibleIds, true)

  function Node({ area, depth }: { area: Area; depth: number }) {
    const acts = byArea(area.id)
    const kids = childrenOf(area.id)
    const hasContent = acts.length > 0 || kids.length > 0
    const open = isOpen(area.id)
    return (
      <>
        <div className="flex items-center justify-between border-t border-[var(--border)] py-1.5" style={{ paddingLeft: depth * 16 + 4 }}>
          <span className="flex items-center text-sm">
            <CollapseToggle open={open} hasChildren={hasContent} onToggle={() => toggle(area.id)} />
            {area.code && <span className="mr-1 font-mono text-xs text-slate-400">{area.code}</span>}
            {area.name}<span className="ml-2 text-xs text-slate-400">{area.kind}{!open && acts.length > 0 ? ` · ${acts.length} activit${acts.length > 1 ? "ies" : "y"}` : ""}</span>
          </span>
          <div className="flex items-center gap-1">
            {canAdd && <Button variant="ghost" className="h-6 px-2 text-xs" onClick={() => setCloning(area)}><Copy className="h-3.5 w-3.5" /> Clone</Button>}
            {canAdd && <Button variant="ghost" className="h-6 px-2 text-xs" onClick={() => setAdding(area)}><Plus className="h-3.5 w-3.5" /> Activity</Button>}
          </div>
        </div>
        {open && acts.map((it) => (
          <div key={it.id} className="grid grid-cols-[1fr_96px_96px_100px_auto] items-center gap-2 py-1 text-sm" style={{ paddingLeft: depth * 16 + 22 }}>
            <span className="text-slate-700">{it.description}</span>
            <span className="text-right text-xs text-slate-500" title="Material">M <Money value={compAmount(it, "MAT")} currency={currency} /></span>
            <span className="text-right text-xs text-slate-500" title="Manpower">L <Money value={compAmount(it, "LAB")} currency={currency} /></span>
            <Money className="text-right font-medium" value={it.lineTotal} currency={currency} />
            <span className="flex justify-end gap-1">
              {canEdit && <button onClick={() => setEditing(it)} className="rounded p-1 text-slate-400 hover:text-[var(--brand)]" title="Material & manpower"><Layers className="h-3.5 w-3.5" /></button>}
              {canDelete && <button onClick={() => { if (confirm(`Delete activity "${it.description}"?`)) onDelItem(it.id) }} className="rounded p-1 text-slate-400 hover:text-rose-600"><Trash2 className="h-3.5 w-3.5" /></button>}
            </span>
          </div>
        ))}
        {open && kids.map((k) => <Node key={k.id} area={k} depth={depth + 1} />)}
      </>
    )
  }

  return (
    <Card className="p-4">
      <div className="mb-1 flex items-center justify-between">
        <h4 className="flex items-center gap-2 text-sm font-semibold text-slate-600"><FolderTree className="h-4 w-4" /> Activities by unit</h4>
        <div className="flex items-center gap-2">
          {canExport && (
            <>
              <div className="flex items-center rounded-md border border-[var(--border)] p-0.5 text-xs" title="Choose how the export is grouped">
                {([["detail", "Detail"], ["area", "Area"], ["subarea", "Sub-Area"], ["unit", "Unit"]] as const).map(([v, lbl]) => (
                  <button
                    key={v}
                    onClick={() => setLevel(v)}
                    className={`rounded px-2 py-1 ${level === v ? "bg-[var(--brand)] text-white" : "text-slate-600 hover:bg-slate-100"}`}
                  >
                    {lbl}
                  </button>
                ))}
              </div>
              <Button variant="outline" className="h-8 text-xs" onClick={() => onExport("xlsx", level)}><FileSpreadsheet className="h-4 w-4" /> Excel</Button>
              <Button variant="outline" className="h-8 text-xs" onClick={() => onExport("csv", level)}><Table className="h-4 w-4" /> CSV</Button>
              <Button variant="outline" className="h-8 text-xs" onClick={() => onExport("pdf", level)}><FileText className="h-4 w-4" /> PDF</Button>
            </>
          )}
          {collapsibleIds.size > 0 && <ExpandCollapseAll onExpand={expandAll} onCollapse={() => collapseAll(collapsibleIds)} />}
        </div>
      </div>
      <p className="mb-2 text-xs text-slate-400">Add work activities under each unit; each carries Material (qty × price) and Manpower (hours × rate). M = material, L = manpower; total includes any other components. The Detail / Area / Sub-Area toggle sets how the export is grouped.</p>
      {childrenOf(null).map((r) => <Node key={r.id} area={r} depth={0} />)}
      {adding && (
        <ActivityModal area={adding} costTypes={costTypes} currency={currency}
          onClose={() => setAdding(null)}
          onSave={(v) => { onAddActivity(adding.id, v); setAdding(null) }} />
      )}
      {editing && (
        <BuildUpModal open onClose={() => setEditing(null)} costTypes={costTypes} currency={currency}
          initial={editing.components.map((c) => ({ typeId: c.typeId, value: c.value, quantity: c.quantity ?? undefined, rate: c.rate ?? undefined }))}
          onSave={(comps) => { onUpdItem({ ...editBase(editing), components: comps }); setEditing(null) }} />
      )}
      {cloning && (
        <CloneRoomModal area={cloning} onClose={() => setCloning(null)}
          onSave={(name) => { onCloneRoom(cloning.id, name); setCloning(null) }} />
      )}
    </Card>
  )
}

/** Clone an area: duplicates the node, its whole subtree (sub-areas + units) and all
 *  their activities under a new name. */
function CloneRoomModal({ area, onClose, onSave }: { area: Area; onClose: () => void; onSave: (name: string) => void }) {
  const [name, setName] = useState(`${area.name} (copy)`)
  const hasChildren = area.kind !== "Unit"
  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { toast.error("Name is required"); return }
    onSave(name.trim())
  }
  return (
    <Modal open onClose={onClose} title={`Clone ${area.kind} — ${area.name}`}>
      <form id="clone-room-form" onSubmit={submit} className="space-y-3">
        <p className="text-xs text-slate-500">
          Creates a copy of this {area.kind}{hasChildren ? ", including its sub-areas, units" : ""} and all their
          activities (material &amp; manpower). You can edit the copy independently afterwards.
        </p>
        <Field label="New name"><Input value={name} onChange={(e) => setName(e.target.value)} autoFocus /></Field>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="clone-room-form">Clone</Button>
      </div>
    </Modal>
  )
}

/** New-activity dialog: pick from the activity catalog (built-in + user-added, with
 *  an inline "+ New") then define the Material/Manpower build-up (qty × rate). */
function ActivityModal({ area, costTypes, currency, onClose, onSave }: {
  area: Area; costTypes: CostComponentType[]; currency: string
  onClose: () => void; onSave: (v: { description: string; unit: string; components: CompInput[] }) => void
}) {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canAddActivity = can("boq", "add")
  const { data: activities } = useQuery({ queryKey: ["activities"], queryFn: () => fetchApi<ActivityType[]>("/api/activities") })
  const active = (activities ?? []).filter((a) => a.isActive)

  const [name, setName] = useState("")
  const [unit, setUnit] = useState("")
  const [components, setComponents] = useState<CompInput[]>([])
  const [buildup, setBuildup] = useState(false)
  const [showNew, setShowNew] = useState(false)
  const [newName, setNewName] = useState("")
  const [adding, setAdding] = useState(false)

  async function addCatalogActivity() {
    const nm = newName.trim()
    if (!nm) { toast.error("Activity name required"); return }
    setAdding(true)
    try {
      await fetchApi("/api/activities", { method: "POST", body: JSON.stringify({ name: nm, sortOrder: 0, isActive: true }) })
      await qc.invalidateQueries({ queryKey: ["activities"] })
      setName(nm); setNewName(""); setShowNew(false)
      toast.success(`Added "${nm}"`)
    } catch (e) { toast.error((e as Error).message) } finally { setAdding(false) }
  }

  function submit(ev: React.FormEvent) {
    ev.preventDefault()
    if (!name.trim()) { toast.error("Choose an activity"); return }
    onSave({ description: name.trim(), unit, components })
  }

  return (
    <Modal open onClose={onClose} title={`New activity — ${area.name}`}>
      <form id="activity-form" onSubmit={submit} className="space-y-3">
        <div>
          <span className="mb-1 block text-xs font-medium text-slate-600">Activity</span>
          <div className="flex gap-2">
            <Select value={name} onChange={(e) => setName(e.target.value)} className="flex-1">
              <option value="">— choose activity —</option>
              {active.map((a) => <option key={a.id} value={a.name}>{a.name}</option>)}
            </Select>
            {canAddActivity && (
              <Button type="button" variant="outline" className="h-9 whitespace-nowrap text-xs" onClick={() => setShowNew((s) => !s)}>
                <Plus className="h-3.5 w-3.5" /> New
              </Button>
            )}
          </div>
          {showNew && (
            <div className="mt-2 flex gap-2">
              <Input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="New activity name" autoFocus
                     onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addCatalogActivity() } }} />
              <Button type="button" variant="outline" className="h-9 text-xs" disabled={adding} onClick={addCatalogActivity}>Add</Button>
            </div>
          )}
        </div>
        <Field label="Unit (optional)"><Input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="m², no, ls" /></Field>
        <div>
          <span className="mb-1 block text-xs font-medium text-slate-600">Material &amp; manpower</span>
          <Button type="button" variant="outline" className="w-full text-xs" onClick={() => setBuildup(true)}>
            <Layers className="h-3.5 w-3.5" /> {components.length ? `${components.length} cost line(s) — edit` : "Add material & manpower"}
          </Button>
        </div>
      </form>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        <Button type="submit" form="activity-form">Add activity</Button>
      </div>
      {buildup && (
        <BuildUpModal open onClose={() => setBuildup(false)} costTypes={costTypes} currency={currency}
          initial={components} onSave={(c) => setComponents(c)} />
      )}
    </Modal>
  )
}
