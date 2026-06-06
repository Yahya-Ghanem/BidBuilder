"use client"
import { useMemo, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { FolderTree, Pencil, Plus, Trash2 } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { usePermissions } from "@/lib/permissions"
import type { Area } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"
import { Field, Modal, Select } from "@/components/form"
import { CollapseToggle, ExpandCollapseAll, useCollapse } from "./shared"

/** Project areas (Area → Sub-area → Unit tree). Add/edit/delete with permission
 *  gating. The collapse state seeds fully-collapsed on first data load so a
 *  deeply nested project doesn't blow up the initial render. */
export function AreasPanel({ projectId }: { projectId: number }) {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canAdd = can("projects", "add"), canEdit = can("projects", "edit"), canDelete = can("projects", "delete")
  const { data: areas } = useQuery({ queryKey: ["areas", projectId], queryFn: () => fetchApi<Area[]>(`/api/projects/${projectId}/areas`) })
  const [modal, setModal] = useState<{ parentAreaId: number | null; area?: Area } | null>(null)
  const inval = () => qc.invalidateQueries({ queryKey: ["areas", projectId] })
  const del = useMutation({
    mutationFn: (aid: number) => fetchApi(`/api/projects/${projectId}/areas/${aid}`, { method: "DELETE" }),
    onSuccess: () => { inval(); toast.success("Area deleted") }, onError: (e) => toast.error((e as Error).message),
  })
  const childrenOf = (id: number | null) => (areas ?? []).filter((a) => a.parentAreaId === id)
  const parentIds = useMemo(() => new Set((areas ?? []).filter((a) => a.parentAreaId != null).map((a) => a.parentAreaId as number)), [areas])
  const { toggle, isOpen, collapseAll, expandAll } = useCollapse(parentIds, true)

  function Node({ area, depth }: { area: Area; depth: number }) {
    const kids = childrenOf(area.id)
    const open = isOpen(area.id)
    return (
      <>
        <div className="flex items-center justify-between rounded py-1 pr-2 hover:bg-slate-50" style={{ paddingLeft: depth * 18 + 4 }}>
          <span className="flex items-center text-sm">
            <CollapseToggle open={open} hasChildren={kids.length > 0} onToggle={() => toggle(area.id)} />
            {area.code && <span className="mr-1 font-mono text-xs text-slate-400">{area.code}</span>}
            {area.name}<span className="ml-2 text-xs text-slate-400">{area.kind}{area.quantity > 0 ? ` · ${area.quantity}${area.unit ? ` ${area.unit}` : ""}` : ""}{kids.length > 0 && !open ? ` · ${kids.length}` : ""}</span>
          </span>
          <div className="flex gap-1">
            {canAdd && <button onClick={() => setModal({ parentAreaId: area.id })} className="rounded p-1 text-slate-400 hover:text-[var(--brand)]" title="Add sub-area"><Plus className="h-3.5 w-3.5" /></button>}
            {canEdit && <button onClick={() => setModal({ parentAreaId: area.parentAreaId, area })} className="rounded p-1 text-slate-400 hover:text-slate-700" title="Edit"><Pencil className="h-3.5 w-3.5" /></button>}
            {canDelete && <button onClick={() => { if (confirm(`Delete area "${area.name}"?`)) del.mutate(area.id) }} className="rounded p-1 text-slate-400 hover:text-rose-600" title="Delete"><Trash2 className="h-3.5 w-3.5" /></button>}
          </div>
        </div>
        {open && kids.map((k) => <Node key={k.id} area={k} depth={depth + 1} />)}
      </>
    )
  }

  return (
    <Card className="p-4">
      <div className="mb-2 flex items-center justify-between">
        <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600"><FolderTree className="h-4 w-4" /> Areas</h3>
        <div className="flex items-center gap-2">
          {parentIds.size > 0 && <ExpandCollapseAll onExpand={expandAll} onCollapse={() => collapseAll(parentIds)} />}
          {canAdd && <Button variant="outline" className="h-8 text-xs" onClick={() => setModal({ parentAreaId: null })}><Plus className="h-4 w-4" /> Area</Button>}
        </div>
      </div>
      {(areas?.length ?? 0) === 0
        ? <p className="text-xs text-slate-400">No areas yet. Break the project into areas, sub-areas and units — BOQ items tagged to them roll up by location.</p>
        : <div>{childrenOf(null).map((r) => <Node key={r.id} area={r} depth={0} />)}</div>}
      {modal && <AreaModal projectId={projectId} parentAreaId={modal.parentAreaId} area={modal.area} onClose={() => setModal(null)} onSaved={() => { inval(); setModal(null) }} />}
    </Card>
  )
}

function AreaModal({ projectId, parentAreaId, area, onClose, onSaved }: { projectId: number; parentAreaId: number | null; area?: Area; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(area?.name ?? "")
  const [code, setCode] = useState(area?.code ?? "")
  const [kind, setKind] = useState(area?.kind ?? (parentAreaId == null ? "Area" : "SubArea"))
  const [quantity, setQuantity] = useState(String(area?.quantity ?? ""))
  const [unit, setUnit] = useState(area?.unit ?? "")
  const [busy, setBusy] = useState(false)
  async function save() {
    if (!name.trim()) { toast.error("Name is required"); return }
    const qty = Number(quantity || 0)
    if (qty < 0 || Number.isNaN(qty)) { toast.error("Quantity must be zero or more"); return }
    setBusy(true)
    try {
      const body = JSON.stringify({ name: name.trim(), code: code.trim() || null, kind, parentAreaId: area ? area.parentAreaId : parentAreaId, sortOrder: area?.sortOrder ?? 0, quantity: qty, unit: unit.trim() || null })
      if (area) await fetchApi(`/api/projects/${projectId}/areas/${area.id}`, { method: "PUT", body })
      else await fetchApi(`/api/projects/${projectId}/areas`, { method: "POST", body })
      toast.success(area ? "Area saved" : "Area added"); onSaved()
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }
  return (
    <Modal open onClose={onClose} title={area ? "Edit area" : "New area"}>
      <div className="space-y-3">
        <Field label="Name *"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Building A" /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Code"><Input value={code} onChange={(e) => setCode(e.target.value)} placeholder="BLK-A" /></Field>
          <Field label="Level"><Select value={kind} onChange={(e) => setKind(e.target.value)}><option value="Area">Area</option><option value="SubArea">Sub-area</option><option value="Unit">Unit</option></Select></Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Quantity"><Input type="number" step="0.0001" min={0} value={quantity} onChange={(e) => setQuantity(e.target.value)} placeholder="e.g. 120" /></Field>
          <Field label="Measure unit"><Input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="m², unit, key…" maxLength={16} /></Field>
        </div>
        <p className="text-xs text-slate-400">Optional. Used to report cost per unit/m² on the area roll-up — it never changes the bid.</p>
      </div>
      <div className="mt-4 flex justify-end gap-2"><Button variant="outline" onClick={onClose}>Cancel</Button><Button disabled={busy} onClick={save}>{busy ? "Saving…" : "Save"}</Button></div>
    </Modal>
  )
}
