"use client"
import { useState } from "react"
import { Trash2, Copy, MapPin, FolderInput, X } from "lucide-react"
import { Button } from "@/components/ui"
import { Select } from "@/components/form"
import { useT } from "@/lib/i18n"
import type { Area, SectionBreakdown } from "@/lib/types"

/**
 * 28.2 — Sticky BOQ bulk action bar.
 *
 * Appears once at least one BOQ row is selected (across any number of
 * sections — the parent unions per-section selections into a single set).
 * The four actions match the ROADMAP: Delete, Move to section, Duplicate,
 * Tag with area. Each one posts to a single bulk endpoint; the response
 * carries the recomputed breakdown which the parent applies to cache.
 *
 * Acceptance bar: "Selecting 10 lines and deleting them is one click
 * instead of 10." So Delete is a single button (with a confirm prompt);
 * Move and Tag open a tiny picker inline (one extra click each — the
 * destination IS a free parameter, can't be skipped). Duplicate is one
 * click.
 *
 * The bar is sticky to the bottom of the viewport so it stays reachable
 * regardless of where the user is in a long BOQ. Inside the editor the
 * BOQ surfaces are gated by `editEdit` / `editAdd` / `editDelete`; if a
 * permission is missing the matching action just renders disabled.
 */
export type BulkBoqAction =
  | { kind: "delete" }
  | { kind: "duplicate" }
  | { kind: "move"; targetSectionId: number }
  | { kind: "tag"; targetAreaId: number | null }

export function BulkBoqBar({
  selectedCount, sections, areas, canDelete, canAdd, canEdit, isPending, onAction, onClear,
}: {
  selectedCount: number
  sections: SectionBreakdown[]
  areas: Area[]
  canDelete: boolean
  canAdd: boolean
  canEdit: boolean
  isPending: boolean
  onAction: (a: BulkBoqAction) => void
  onClear: () => void
}) {
  const t = useT()
  // Inline pickers for move/tag — open one, the other auto-closes.
  const [picker, setPicker] = useState<"move" | "tag" | null>(null)
  const [moveTarget, setMoveTarget] = useState<string>("")
  const [tagTarget, setTagTarget] = useState<string>("")

  if (selectedCount === 0) return null

  function reset() { setPicker(null); setMoveTarget(""); setTagTarget("") }

  function runDelete() {
    if (!confirm(t("ed.boq.bulk.deleteConfirm", { count: selectedCount }))) return
    onAction({ kind: "delete" })
  }
  function runMove() {
    if (!moveTarget) return
    onAction({ kind: "move", targetSectionId: Number(moveTarget) })
    reset()
  }
  function runTag() {
    // Empty string in the dropdown == "clear area". We send a normalized
    // payload (null for clear) so the bar's contract matches the endpoint.
    const id = tagTarget === "" ? null : Number(tagTarget)
    onAction({ kind: "tag", targetAreaId: id })
    reset()
  }

  return (
    <div
      role="region"
      aria-label={t("ed.boq.bulk.selected", { count: selectedCount })}
      data-testid="boq-bulk-bar"
      // Sticky — pinned to the viewport bottom while the user scrolls a
      // long BOQ. Z above the sticky <thead>. The shadow + accent border
      // separates it from the table even on a fully scrolled page.
      className="sticky bottom-3 z-30 mt-3 flex flex-wrap items-center gap-2 rounded-lg border border-[var(--brand)]/30 bg-[var(--card)] px-3 py-2 text-sm shadow-lg shadow-black/10"
    >
      <span className="font-medium text-[var(--text)]" data-testid="boq-bulk-selected">
        {t("ed.boq.bulk.selected", { count: selectedCount })}
      </span>

      {canDelete && (
        <Button
          variant="outline"
          className="h-7 text-xs text-danger hover:bg-danger-soft"
          data-testid="boq-bulk-delete"
          disabled={isPending}
          onClick={runDelete}
        >
          <Trash2 className="h-3.5 w-3.5" /> {t("ed.boq.bulk.delete")}
        </Button>
      )}

      {canAdd && (
        <Button
          variant="outline"
          className="h-7 text-xs"
          data-testid="boq-bulk-duplicate"
          disabled={isPending}
          onClick={() => onAction({ kind: "duplicate" })}
        >
          <Copy className="h-3.5 w-3.5" /> {t("ed.boq.bulk.duplicate")}
        </Button>
      )}

      {canEdit && (
        <Button
          variant={picker === "move" ? "primary" : "outline"}
          className="h-7 text-xs"
          data-testid="boq-bulk-move"
          disabled={isPending}
          onClick={() => { setPicker(picker === "move" ? null : "move"); setTagTarget("") }}
        >
          <FolderInput className="h-3.5 w-3.5" /> {t("ed.boq.bulk.move")}
        </Button>
      )}

      {canEdit && (
        <Button
          variant={picker === "tag" ? "primary" : "outline"}
          className="h-7 text-xs"
          data-testid="boq-bulk-tag"
          disabled={isPending}
          onClick={() => { setPicker(picker === "tag" ? null : "tag"); setMoveTarget("") }}
        >
          <MapPin className="h-3.5 w-3.5" /> {t("ed.boq.bulk.tag")}
        </Button>
      )}

      <button
        type="button"
        onClick={() => { reset(); onClear() }}
        aria-label={t("ed.boq.bulk.clear")}
        className="ms-auto rounded p-1 text-muted hover:bg-[color-mix(in_oklab,var(--text)_8%,transparent)] hover:text-[var(--text)]"
      >
        <X className="h-3.5 w-3.5" />
      </button>

      {picker === "move" && (
        <div className="basis-full pt-1">
          <label className="flex items-center gap-2 text-xs text-muted">
            {t("ed.boq.bulk.moveTitle", { count: selectedCount })}
            <Select
              className="h-7 py-0 text-xs"
              value={moveTarget}
              onChange={(e) => setMoveTarget(e.target.value)}
              data-testid="boq-bulk-move-target"
            >
              <option value="">—</option>
              {sections.map((s) => (
                <option key={s.id} value={s.id}>{s.code} {s.title}</option>
              ))}
            </Select>
            <Button
              variant="primary" className="h-7 text-xs"
              disabled={!moveTarget || isPending}
              onClick={runMove}
              data-testid="boq-bulk-move-apply"
            >
              {t("ed.boq.bulk.apply")}
            </Button>
          </label>
        </div>
      )}

      {picker === "tag" && (
        <div className="basis-full pt-1">
          <label className="flex items-center gap-2 text-xs text-muted">
            {t("ed.boq.bulk.tagTitle", { count: selectedCount })}
            <Select
              className="h-7 py-0 text-xs"
              value={tagTarget}
              onChange={(e) => setTagTarget(e.target.value)}
              data-testid="boq-bulk-tag-target"
            >
              <option value="">{t("ed.boq.bulk.areaNone")}</option>
              {areas.map((a) => (
                <option key={a.id} value={a.id}>{a.name}</option>
              ))}
            </Select>
            <Button
              variant="primary" className="h-7 text-xs"
              disabled={isPending}
              onClick={runTag}
              data-testid="boq-bulk-tag-apply"
            >
              {t("ed.boq.bulk.apply")}
            </Button>
          </label>
        </div>
      )}
    </div>
  )
}
