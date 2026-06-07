"use client"

import { useEffect, useMemo, useRef, useState } from "react"
import { useRouter } from "next/navigation"
import { useQuery } from "@tanstack/react-query"
import { Search, FolderKanban, FileText, Library, Boxes, CornerDownLeft } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { SearchResults } from "@/lib/types"

const ICONS: Record<string, typeof Search> = {
  project: FolderKanban,
  estimate: FileText,
  resource: Library,
  assembly: Boxes,
}

/**
 * 20.4 — Cross-project search palette. Opened from the header button or ⌘K / Ctrl-K.
 * Debounced query hits /api/search and renders grouped results; ↑/↓ move the
 * selection, Enter (or click) navigates, Esc / outside-click closes.
 */
export function SearchPalette() {
  const router = useRouter()
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState("")
  const [term, setTerm] = useState("")
  const [active, setActive] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)

  // Global ⌘K / Ctrl-K to open.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault()
        setOpen(true)
      }
    }
    document.addEventListener("keydown", onKey)
    return () => document.removeEventListener("keydown", onKey)
  }, [])

  // Reset + focus on open/close.
  useEffect(() => {
    if (open) {
      setQuery(""); setTerm(""); setActive(0)
      // Focus after the panel mounts.
      requestAnimationFrame(() => inputRef.current?.focus())
    }
  }, [open])

  // Debounce the query into the fetched term.
  useEffect(() => {
    const t = setTimeout(() => setTerm(query.trim()), 220)
    return () => clearTimeout(t)
  }, [query])

  const { data, isFetching } = useQuery<SearchResults>({
    queryKey: ["search", term],
    queryFn: () => fetchApi<SearchResults>(`/api/search?q=${encodeURIComponent(term)}`),
    enabled: open && term.length >= 2,
    staleTime: 30_000,
  })

  const groups = data?.groups ?? []
  // Flatten for keyboard navigation; remember each hit's running index.
  const flat = useMemo(() => groups.flatMap((g) => g.hits.map((h) => h.link)), [groups])
  useEffect(() => { setActive(0) }, [term, data])

  function go(link: string) {
    setOpen(false)
    router.push(link)
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "Escape") { setOpen(false); return }
    if (!flat.length) return
    if (e.key === "ArrowDown") { e.preventDefault(); setActive((i) => (i + 1) % flat.length) }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActive((i) => (i - 1 + flat.length) % flat.length) }
    else if (e.key === "Enter") { e.preventDefault(); if (flat[active]) go(flat[active]) }
  }

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        className="flex items-center gap-2 rounded-md border border-[var(--border)] px-2.5 py-1.5 text-sm text-slate-600 hover:bg-slate-50"
        aria-label="Search"
      >
        <Search className="h-4 w-4" />
        <span className="hidden sm:inline">Search…</span>
        <kbd className="ml-2 hidden rounded border border-[var(--border)] bg-slate-50 px-1.5 text-[10px] text-slate-600 sm:inline">⌘K</kbd>
      </button>

      {open && (
        <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/30 p-4 pt-[12vh]"
             onMouseDown={(e) => { if (e.target === e.currentTarget) setOpen(false) }}>
          <div ref={panelRef} className="w-full max-w-xl overflow-hidden rounded-xl border border-[var(--border)] bg-white shadow-2xl"
               onKeyDown={onKeyDown}>
            <div className="flex items-center gap-2 border-b border-[var(--border)] px-3">
              <Search className="h-4 w-4 shrink-0 text-slate-400" />
              <input
                ref={inputRef}
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Search projects, estimates, resources, assemblies…"
                className="w-full bg-transparent py-3 text-sm outline-none placeholder:text-slate-400"
              />
              {isFetching && <span className="text-xs text-slate-400">…</span>}
            </div>

            <div className="max-h-[60vh] overflow-auto py-1">
              {term.length < 2 ? (
                <p className="px-4 py-6 text-center text-sm text-slate-400">Type at least 2 characters to search.</p>
              ) : groups.length === 0 ? (
                <p className="px-4 py-6 text-center text-sm text-slate-400">{isFetching ? "Searching…" : `No matches for “${term}”.`}</p>
              ) : (
                groups.map((g) => {
                  const Icon = ICONS[g.type] ?? Search
                  return (
                    <div key={g.type} className="px-1 py-1">
                      <div className="px-3 py-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400">{g.label}</div>
                      {g.hits.map((h) => {
                        const idx = flat.indexOf(h.link)
                        const isActive = idx === active
                        return (
                          <button key={`${g.type}-${h.link}-${h.title}`}
                            onMouseEnter={() => setActive(idx)}
                            onClick={() => go(h.link)}
                            className={`flex w-full items-center gap-3 rounded-md px-3 py-2 text-left ${isActive ? "bg-[var(--brand)]/10" : "hover:bg-slate-50"}`}>
                            <Icon className={`h-4 w-4 shrink-0 ${isActive ? "text-[var(--brand)]" : "text-slate-400"}`} />
                            <span className="min-w-0 flex-1">
                              <span className="block truncate text-sm text-slate-700">{h.title}</span>
                              {h.subtitle && <span className="block truncate text-xs text-slate-400">{h.subtitle}</span>}
                            </span>
                            {isActive && <CornerDownLeft className="h-3.5 w-3.5 shrink-0 text-slate-300" />}
                          </button>
                        )
                      })}
                    </div>
                  )
                })
              )}
            </div>
          </div>
        </div>
      )}
    </>
  )
}
