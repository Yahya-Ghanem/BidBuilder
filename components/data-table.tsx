"use client"
/**
 * 26.4 — `<DataTable>` — a single dense-grid wrapper used everywhere a
 * `<table>` of rows-of-data appears in BidBuilder.
 *
 * The BOQ, area roll-up, quotes register, audit log, users / teams / cost-
 * type tables in Settings, subcontractor quotes — all hand-rolled `<table>`
 * variants. Each subtly different: some have selection, some have inline
 * editing, some have sticky headers, some don't. Pulling them onto one
 * primitive lets us add column resize, virtualization, sort, keyboard nav,
 * and density toggles in one place.
 *
 * Built on TanStack Table v8 (headless model + observable state) + TanStack
 * Virtual (windowed rendering). TanStack Table doesn't render anything by
 * itself — it tells us which rows + columns to render and tracks sort /
 * selection / column-resize state. We supply the JSX, the styling, and the
 * scroll container.
 *
 * Why a wrapper instead of using TanStack Table directly per surface?
 *
 *   1. **Consistent styling.** Every existing table reaches for the same
 *      slate-50 thead, `text-sm`, border-t-between-rows, font-medium totals
 *      column. Centralising stops drift.
 *   2. **Optional features defaulted.** Sticky thead, sort, selection,
 *      virtualization — each is a prop, each defaults to "off" so existing
 *      surfaces opt in gradually.
 *   3. **Virtualization for free at a threshold.** BOQ already had custom
 *      DOM-math virtualization; with this wrapper, anyone with > N rows
 *      gets it without writing windowing code.
 *
 * BOQ is the first consumer (Phase 26.4) and we migrate other tables
 * opportunistically (the ROADMAP §26.4 explicitly says "BOQ first, others
 * later"). Each subsequent migration is small — just map the existing
 * columns to a `ColumnDef<T>[]`.
 *
 * Selection state notes:
 *   • `onSelectionChange` fires with a Set<rowId> on every change. We
 *     deliberately surface a Set (not an array) so consumers don't have to
 *     dedupe — they have O(1) `has()` lookup.
 *   • When selection is enabled, a leading checkbox column is auto-inserted.
 *     A click on a header checkbox toggles all CURRENTLY-VISIBLE rows; this
 *     matches the existing Resources page UX so we don't surprise users.
 *
 * Virtualization notes:
 *   • We use TanStack Virtual's `useVirtualizer`. The scroll container is
 *     a `<div>` (not the `<table>`) — `<table>` doesn't compose with
 *     position:sticky on every layout engine.
 *   • The virtualizer measures actual row heights on mount + resize so rows
 *     with inline edit (Inputs / Selects expand the row) don't visually jump.
 *   • We virtualize ONLY when `data.length >= virtualizeThreshold`. Below
 *     that, paying the windowing cost (a wrapper div, padding spacers,
 *     scroll listeners) is net-negative. 150 matches the previous BOQ
 *     custom virtualizer.
 *
 * Accessibility:
 *   • The header `<th>`s get scope="col" and (if sortable) aria-sort.
 *   • The selection checkbox column has its `<th>` labelled "Select all".
 *   • Sticky `thead` still renders inside a semantic `<table>` so screen
 *     readers get the row/column structure for free.
 */

import { useEffect, useMemo, useRef, type ReactNode } from "react"
import {
  type ColumnDef,
  type RowSelectionState,
  type SortingState,
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
} from "@tanstack/react-table"
import { useVirtualizer } from "@tanstack/react-virtual"
import { ChevronDown, ChevronUp, ChevronsUpDown } from "lucide-react"
import { useState } from "react"
import { cn } from "@/lib/utils"

/** Public props. Re-exporting `ColumnDef` lets consumers type their columns
 *  without pulling TanStack Table imports into every file — keeps the
 *  primitive's surface area visible from a single import. */
export type { ColumnDef } from "@tanstack/react-table"

export type DataTableProps<T> = {
  /** The rows. Memoize at the call site if expensive to compute. */
  data: T[]
  /** Column definitions. Stable identity recommended (useMemo at the call site). */
  columns: ColumnDef<T, unknown>[]
  /** Stable id per row — used by selection, virtualization, React keys.
   *  Required so we never silently fall back to index keys (which break
   *  selection across re-sorts). */
  getRowId: (row: T) => string

  /** Render the `<thead>`. Default: true. */
  showHeader?: boolean
  /** Make the `<thead>` stick to the top of the scroll container. Default: true. */
  stickyHeader?: boolean
  /** Click headers to sort. Default: false (most BidBuilder tables defer to
   *  backend `sortOrder`; a few opt in). */
  sortable?: boolean

  /** When provided, an additional leading checkbox column is rendered and
   *  the callback fires on every selection change. */
  onSelectionChange?: (selected: Set<string>) => void
  /** 28.2 — Bumping this prop clears the internal selection state. Selection
   *  lives inside the DataTable, but a consumer that just ran a bulk action
   *  on the selected rows needs to drop them all. Pass any value that changes
   *  (a number from `useState`/`useRef`, or a stable token like the breakdown's
   *  `rowVersion`) and the next render clears `{}` into TanStack Table. */
  selectionResetKey?: string | number

  /** Switch to virtualized rendering when `data.length >= threshold`.
   *  Default: 150 (matches the BOQ pre-26.4 custom virtualizer). */
  virtualizeThreshold?: number
  /** Estimated row height in px for virtualization. Default: 56. */
  rowEstimatedPx?: number
  /** Overscan rows on each side of the viewport. Default: 12. */
  overscan?: number
  /** Hard cap on the scroll viewport height when virtualized. Default: "60vh". */
  maxHeight?: string

  /** Extra classes on the outer container. */
  className?: string
  /** Extra classes on the `<table>` itself. */
  tableClassName?: string
  /** Test id on the scroll container — useful for E2E selectors. */
  testId?: string
  /** Banner rendered above the table (e.g. "Showing N of M rows"). */
  banner?: ReactNode
  /** Empty-state node when `data.length === 0`. */
  emptyState?: ReactNode
}

export function DataTable<T>({
  data, columns, getRowId,
  showHeader = true, stickyHeader = true, sortable = false,
  onSelectionChange, selectionResetKey,
  virtualizeThreshold = 150, rowEstimatedPx = 56, overscan = 12, maxHeight = "60vh",
  className, tableClassName, testId, banner, emptyState,
}: DataTableProps<T>) {
  const [sorting, setSorting] = useState<SortingState>([])
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})

  // Surface selection changes to the consumer. Translate TanStack Table's
  // internal `{rowId: true}` map into the public Set<string> contract.
  useEffect(() => {
    if (!onSelectionChange) return
    onSelectionChange(new Set(Object.keys(rowSelection).filter((k) => rowSelection[k])))
  }, [rowSelection, onSelectionChange])

  // 28.2 — External reset hook. A bulk action consumer bumps `selectionResetKey`
  // after a successful round-trip; we drop every selected row in one shot.
  // Skipping the initial mount keeps the empty-from-start path noise-free.
  const firstResetRender = useRef(true)
  useEffect(() => {
    if (firstResetRender.current) { firstResetRender.current = false; return }
    setRowSelection({})
  }, [selectionResetKey])

  // Auto-insert the checkbox column when selection is enabled. We do this
  // here (not in the call site) so every selection-enabled DataTable has
  // the same accessible checkbox UX without each consumer reimplementing it.
  const effectiveColumns = useMemo<ColumnDef<T, unknown>[]>(() => {
    if (!onSelectionChange) return columns
    const checkboxCol: ColumnDef<T, unknown> = {
      id: "__select__",
      // 32px is enough for the checkbox + some padding; sticky col-width is
      // important because the header checkbox aligns above the body checks.
      size: 32,
      header: ({ table }) => (
        <input
          type="checkbox"
          aria-label="Select all rows"
          checked={table.getIsAllRowsSelected()}
          // Toggle selection across all CURRENTLY-VISIBLE rows. The
          // `getIsAllPageRowsSelected` variant returns a 3-state value
          // (true / false / "indeterminate" represented as bool), so we
          // negate to flip correctly.
          ref={(el) => { if (el) el.indeterminate = table.getIsSomeRowsSelected() && !table.getIsAllRowsSelected() }}
          onChange={table.getToggleAllRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          type="checkbox"
          aria-label={`Select row ${row.id}`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          // Don't bubble row-click handlers (if a consumer adds onClick
          // to the row later) when the user just wanted to toggle.
          onClick={(e) => e.stopPropagation()}
        />
      ),
      enableSorting: false,
    }
    return [checkboxCol, ...columns]
  }, [columns, onSelectionChange])

  const table = useReactTable({
    data, columns: effectiveColumns, getRowId,
    state: { sorting, rowSelection },
    onSortingChange: setSorting,
    onRowSelectionChange: setRowSelection,
    enableSorting: sortable,
    enableRowSelection: !!onSelectionChange,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: sortable ? getSortedRowModel() : undefined,
  })

  const rows = table.getRowModel().rows
  const shouldVirtualize = rows.length >= virtualizeThreshold

  // Virtualizer hook is called unconditionally per React rules but only
  // produces row-positions when shouldVirtualize is true (we don't read
  // its output otherwise — the non-virtualized branch renders raw rows).
  const scrollRef = useRef<HTMLDivElement>(null)
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => rowEstimatedPx,
    overscan,
    // TanStack Virtual default scrolls inside the closest scrolling
    // ancestor; we explicitly want the scrollRef container.
  })

  if (data.length === 0 && emptyState) {
    return <div className={className}>{emptyState}</div>
  }

  const headRow = showHeader && (
    <thead className={cn(
      "bg-slate-50 text-start text-sm text-muted",
      stickyHeader && "sticky top-0 z-10",
    )}>
      {table.getHeaderGroups().map((hg) => (
        <tr key={hg.id} className="border-b border-[var(--border)]">
          {hg.headers.map((h) => {
            const canSort = sortable && h.column.getCanSort()
            const sortDir = h.column.getIsSorted()
            return (
              <th
                key={h.id}
                scope="col"
                aria-sort={
                  sortDir === "asc" ? "ascending" :
                  sortDir === "desc" ? "descending" :
                  canSort ? "none" : undefined
                }
                style={{ width: h.getSize() !== 150 /* default */ ? h.getSize() : undefined }}
                className={cn("px-3 py-2 text-start font-medium", canSort && "cursor-pointer select-none")}
                onClick={canSort ? h.column.getToggleSortingHandler() : undefined}
              >
                <span className="inline-flex items-center gap-1">
                  {h.isPlaceholder ? null : flexRender(h.column.columnDef.header, h.getContext())}
                  {canSort && (
                    sortDir === "asc" ? <ChevronUp className="h-3 w-3" aria-hidden /> :
                    sortDir === "desc" ? <ChevronDown className="h-3 w-3" aria-hidden /> :
                    <ChevronsUpDown className="h-3 w-3 opacity-40" aria-hidden />
                  )}
                </span>
              </th>
            )
          })}
        </tr>
      ))}
    </thead>
  )

  if (!shouldVirtualize) {
    return (
      <div className={cn("w-full", className)}>
        {banner}
        <table className={cn("w-full text-sm", tableClassName)}>
          {headRow}
          <tbody>
            {rows.map((row) => (
              <tr key={row.id} className="border-t border-[var(--border)]">
                {row.getVisibleCells().map((cell) => (
                  <td key={cell.id} className="px-3 py-2 align-top">
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    )
  }

  // Virtualized branch: padding spacers reserve the full scroll height; we
  // render only the visible window of rows positioned via `transform`.
  const virtRows = virtualizer.getVirtualItems()
  const totalSize = virtualizer.getTotalSize()
  const paddingTop = virtRows[0]?.start ?? 0
  const paddingBottom = totalSize - (virtRows[virtRows.length - 1]?.end ?? 0)

  return (
    <div className={cn("w-full", className)}>
      {banner}
      <div
        ref={scrollRef}
        data-testid={testId}
        className="overflow-y-auto"
        style={{ maxHeight }}
      >
        <table className={cn("w-full text-sm", tableClassName)}>
          {headRow}
          <tbody>
            {paddingTop > 0 && <tr aria-hidden style={{ height: paddingTop }} />}
            {virtRows.map((vr) => {
              const row = rows[vr.index]
              return (
                <tr
                  key={row.id}
                  ref={(el) => { if (el) virtualizer.measureElement(el) }}
                  data-index={vr.index}
                  className="border-t border-[var(--border)]"
                >
                  {row.getVisibleCells().map((cell) => (
                    <td key={cell.id} className="px-3 py-2 align-top">
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </td>
                  ))}
                </tr>
              )
            })}
            {paddingBottom > 0 && <tr aria-hidden style={{ height: paddingBottom }} />}
          </tbody>
        </table>
      </div>
    </div>
  )
}
