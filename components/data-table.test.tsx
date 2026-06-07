/** @vitest-environment jsdom */
import { describe, it, expect, afterEach } from "vitest"
import { render, screen, fireEvent, cleanup } from "@testing-library/react"
import { useState } from "react"
import { DataTable, type ColumnDef } from "./data-table"

// Vitest doesn't auto-import @testing-library/react's afterEach cleanup the
// way Jest does, so the previous test's DOM bleeds into the next render
// and getByText finds duplicates. Wire it manually.
afterEach(() => cleanup())

type Row = { id: number; code: string; qty: number }

const rows: Row[] = [
  { id: 1, code: "B-101", qty: 12 },
  { id: 2, code: "B-102", qty: 7 },
  { id: 3, code: "B-103", qty: 21 },
]

const columns: ColumnDef<Row, unknown>[] = [
  { id: "code", header: "Code", accessorFn: (r) => r.code, cell: ({ getValue }) => String(getValue()) },
  { id: "qty", header: "Qty", accessorFn: (r) => r.qty, cell: ({ getValue }) => String(getValue()) },
]

describe("DataTable (26.4)", () => {
  it("renders a <thead> with column labels when showHeader is true (default)", () => {
    render(<DataTable data={rows} columns={columns} getRowId={(r) => String(r.id)} />)
    // Both headers visible.
    expect(screen.getByRole("columnheader", { name: /code/i })).toBeTruthy()
    expect(screen.getByRole("columnheader", { name: /qty/i })).toBeTruthy()
  })

  it("renders every row in order", () => {
    render(<DataTable data={rows} columns={columns} getRowId={(r) => String(r.id)} />)
    expect(screen.getByText("B-101")).toBeTruthy()
    expect(screen.getByText("B-102")).toBeTruthy()
    expect(screen.getByText("B-103")).toBeTruthy()
  })

  it("renders the emptyState when data is empty", () => {
    render(<DataTable data={[]} columns={columns} getRowId={(r: Row) => String(r.id)} emptyState={<p>No rows yet</p>} />)
    expect(screen.getByText("No rows yet")).toBeTruthy()
    // No table rendered when empty + emptyState provided.
    expect(screen.queryByRole("columnheader")).toBeNull()
  })

  it("sorts when sortable + header is clicked", () => {
    render(<DataTable data={rows} columns={columns} getRowId={(r) => String(r.id)} sortable />)
    // TanStack Table sorts numeric columns DESCENDING on the first click
    // (sortDescFirst defaults to undefined → numeric → desc). String columns
    // sort ascending first. So click Qty once → 21, 12, 7.
    const qtyHeader = screen.getByRole("columnheader", { name: /qty/i })
    fireEvent.click(qtyHeader)
    const cells = screen.getAllByRole("cell").filter((c) => /^\d+$/.test(c.textContent ?? ""))
    expect(cells[0].textContent).toBe("21")
    expect(cells[2].textContent).toBe("7")
    // Click again — ascending (7, 12, 21).
    fireEvent.click(qtyHeader)
    const cells2 = screen.getAllByRole("cell").filter((c) => /^\d+$/.test(c.textContent ?? ""))
    expect(cells2[0].textContent).toBe("7")
    expect(cells2[2].textContent).toBe("21")
  })

  it("exposes selection through onSelectionChange (Set<string>)", () => {
    function Harness() {
      const [sel, setSel] = useState<Set<string>>(new Set())
      return (
        <>
          <DataTable data={rows} columns={columns} getRowId={(r) => String(r.id)} onSelectionChange={setSel} />
          <output data-testid="count">{sel.size}</output>
        </>
      )
    }
    render(<Harness />)
    // Tick the second row's checkbox; the "Select all" header checkbox is the
    // first checkbox in the document so we skip it.
    const checkboxes = screen.getAllByRole("checkbox")
    expect(checkboxes.length).toBe(4) // 1 header + 3 rows
    fireEvent.click(checkboxes[2]) // second body row
    expect(screen.getByTestId("count").textContent).toBe("1")
  })

  it("aria-sort communicates current sort direction", () => {
    render(<DataTable data={rows} columns={columns} getRowId={(r) => String(r.id)} sortable />)
    const qtyHeader = screen.getByRole("columnheader", { name: /qty/i })
    expect(qtyHeader.getAttribute("aria-sort")).toBe("none")
    fireEvent.click(qtyHeader)
    // Numeric column first-click is descending (TanStack default).
    expect(qtyHeader.getAttribute("aria-sort")).toBe("descending")
    fireEvent.click(qtyHeader)
    expect(qtyHeader.getAttribute("aria-sort")).toBe("ascending")
  })

  it("renders without thead when showHeader=false", () => {
    render(<DataTable data={rows} columns={columns} getRowId={(r) => String(r.id)} showHeader={false} />)
    expect(screen.queryByRole("columnheader")).toBeNull()
    expect(screen.getByText("B-101")).toBeTruthy()
  })
})
