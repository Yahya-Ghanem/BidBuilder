"use client"

import { useEffect, useMemo, useRef, useState } from "react"
import { useRouter } from "next/navigation"
import { useQuery } from "@tanstack/react-query"
import { Search, FolderKanban, FileText, Library, Boxes, CornerDownLeft, Clock, Plus, LogOut, Settings as SettingsIcon, ArrowRight, Zap } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { SearchResults } from "@/lib/types"
import { useAuth } from "@/lib/auth"
import { usePermissions } from "@/lib/permissions"
import { useT } from "@/lib/i18n"
import { useRecentEntities, type RecentEntity } from "@/lib/useRecentEntities"

const ICONS: Record<string, typeof Search> = {
  project: FolderKanban,
  estimate: FileText,
  resource: Library,
  assembly: Boxes,
}

/** 28.5 — `@p Sun` filters the result set to projects only. The lookup table
 *  is a shortcut into SearchGroup.type — palette only renders the matched
 *  group's hits and hides the others. Anything that isn't a known prefix
 *  falls through to the normal multi-type search. */
const TYPE_PREFIXES: Record<string, string> = {
  p: "project",
  pr: "project",
  proj: "project",
  e: "estimate",
  est: "estimate",
  r: "resource",
  res: "resource",
  a: "assembly",
  asm: "assembly",
}

/** 28.5 — Action mode (`>` prefix). Each entry is a tiny command with an
 *  optional gate (`enabled` returns true for SOME tenants only — e.g. only
 *  admins see "new project"). Run() either navigates or calls a side effect. */
interface ActionDef {
  id: string
  label: string                       // i18n key for the row label
  icon: typeof Search
  /** Words the matcher searches against ("new", "create", "project") in addition
   *  to the label. Lowercase. */
  keywords: string[]
  /** Predicate from the palette's available context — defaults to always-on. */
  enabled?: (ctx: ActionCtx) => boolean
  /** What the action does when the user picks it. The palette closes first
   *  (`onPick`), then calls run(). Run() may navigate or call something else. */
  run: (ctx: ActionCtx) => void
}

interface ActionCtx {
  router: ReturnType<typeof useRouter>
  logout: () => void
  isAdmin: boolean
}

/**
 * 20.4 — Cross-project search palette. Opened from the header button or ⌘K / Ctrl-K.
 * Debounced query hits /api/search and renders grouped results; ↑/↓ move the
 * selection, Enter (or click) navigates, Esc / outside-click closes.
 *
 * 28.5 — Three modes:
 *   • empty query → top section shows the last 5 visited entities (per-browser)
 *   • `@p Sun`     → filters search to projects only (`@e`/`@r`/`@a` for the others)
 *   • `> sign`     → switches to action mode (new project, sign out, settings, …)
 * Navigation always records the destination into recents via useRecentEntities.
 */
export function SearchPalette() {
  const router = useRouter()
  const { logout } = useAuth()
  const { isAdmin } = usePermissions()
  const t = useT()
  const { recent, record } = useRecentEntities()

  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState("")
  const [term, setTerm] = useState("")
  const [active, setActive] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)

  // ── Parse the query into a mode + a search term ─────────────────────────
  const parsed = useMemo(() => parseQuery(query), [query])

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
      requestAnimationFrame(() => inputRef.current?.focus())
    }
  }, [open])

  // Debounce the user-visible term we ship to the API. Action mode + recent
  // mode resolve client-side, so we only need to debounce the search lane.
  useEffect(() => {
    const t = setTimeout(() => setTerm(parsed.mode === "search" ? parsed.term : ""), 220)
    return () => clearTimeout(t)
  }, [parsed])

  // ── Server search (only for the search/filter lanes) ─────────────────────
  const { data, isFetching } = useQuery<SearchResults>({
    queryKey: ["search", term],
    queryFn: () => fetchApi<SearchResults>(`/api/search?q=${encodeURIComponent(term)}`),
    enabled: open && parsed.mode === "search" && term.length >= 2,
    staleTime: 30_000,
  })

  // Project the API groups through the entity-type filter (if any).
  const groups = useMemo(() => {
    const all = data?.groups ?? []
    if (parsed.mode !== "search" || !parsed.entityType) return all
    return all.filter((g) => g.type === parsed.entityType)
  }, [data, parsed])

  // ── Action catalog (rebuilt with the current ctx on each render) ─────────
  const actions = useMemo<ActionDef[]>(() => buildActions(t), [t])
  const actionCtx: ActionCtx = useMemo(() => ({ router, logout, isAdmin }), [router, logout, isAdmin])
  const matchingActions = useMemo(() => {
    if (parsed.mode !== "actions") return []
    const q = parsed.term.toLowerCase()
    return actions.filter((a) => a.enabled?.(actionCtx) ?? true)
      .filter((a) => {
        if (!q) return true
        if (a.label.toLowerCase().includes(q)) return true
        return a.keywords.some((k) => k.includes(q))
      })
  }, [parsed, actions, actionCtx])

  // ── Build the flat keyboard-navigable list for the current mode ──────────
  // Each item is one of: a recent entry, a search hit, or an action.
  const flat = useMemo(() => {
    if (parsed.mode === "actions") return matchingActions.map((a) => ({ kind: "action" as const, action: a }))
    if (parsed.mode === "recent") return recent.map((e) => ({ kind: "recent" as const, entity: e }))
    return groups.flatMap((g) => g.hits.map((h) => ({ kind: "hit" as const, type: g.type, label: g.label, title: h.title, subtitle: h.subtitle, link: h.link })))
  }, [parsed.mode, matchingActions, recent, groups])

  useEffect(() => { setActive(0) }, [parsed.mode, term, data, matchingActions.length])

  function goLink(link: string, type: string, title: string, subtitle: string | null) {
    setOpen(false)
    record({ type, title, subtitle, link })
    router.push(link)
  }

  function pickItem(item: typeof flat[number]) {
    if (item.kind === "action") {
      setOpen(false)
      item.action.run(actionCtx)
      return
    }
    if (item.kind === "recent") {
      goLink(item.entity.link, item.entity.type, item.entity.title, item.entity.subtitle)
      return
    }
    goLink(item.link, item.type, item.title, item.subtitle)
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "Escape") { setOpen(false); return }
    if (!flat.length) return
    if (e.key === "ArrowDown") { e.preventDefault(); setActive((i) => (i + 1) % flat.length) }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActive((i) => (i - 1 + flat.length) % flat.length) }
    else if (e.key === "Enter") { e.preventDefault(); if (flat[active]) pickItem(flat[active]) }
  }

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        className="flex items-center gap-2 rounded-md border border-[var(--border)] px-2.5 py-1.5 text-sm text-muted hover:bg-[color-mix(in_oklab,var(--text)_6%,transparent)]"
        aria-label={t("palette.openLabel")}
        data-testid="search-palette-trigger"
      >
        <Search className="h-4 w-4" />
        <span className="hidden sm:inline">{t("palette.openLabel")}…</span>
        <kbd className="ml-2 hidden rounded border border-[var(--border)] bg-[color-mix(in_oklab,var(--text)_6%,transparent)] px-1.5 text-[10px] text-muted sm:inline">⌘K</kbd>
      </button>

      {open && (
        <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/30 p-4 pt-[12vh]"
             onMouseDown={(e) => { if (e.target === e.currentTarget) setOpen(false) }}
             data-testid="search-palette">
          <div ref={panelRef} className="w-full max-w-xl overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--card)] text-[var(--text)] shadow-2xl"
               onKeyDown={onKeyDown}>
            <div className="flex items-center gap-2 border-b border-[var(--border)] px-3">
              {parsed.mode === "actions" ? <Zap className="h-4 w-4 shrink-0 text-[var(--brand)]" />
                : parsed.mode === "recent" ? <Clock className="h-4 w-4 shrink-0 text-muted" />
                : <Search className="h-4 w-4 shrink-0 text-muted" />}
              <input
                ref={inputRef}
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={t("palette.placeholder")}
                className="w-full bg-transparent py-3 text-sm outline-none placeholder:text-muted"
                data-testid="search-palette-input"
              />
              {parsed.mode === "search" && isFetching && <span className="text-xs text-muted">…</span>}
            </div>

            <div className="max-h-[60vh] overflow-auto py-1" role="listbox" data-testid="search-palette-results">
              {/* — Action lane —————————————————————— */}
              {parsed.mode === "actions" && (
                <Group label={t("palette.actions")}>
                  {matchingActions.length === 0 ? (
                    <p className="px-4 py-6 text-center text-sm text-muted">{t("palette.actions.noMatch")}</p>
                  ) : matchingActions.map((a, i) => {
                    const isActive = i === active
                    const Icon = a.icon
                    return (
                      <button
                        key={a.id}
                        type="button"
                        data-testid={`palette-action-${a.id}`}
                        onMouseEnter={() => setActive(i)}
                        onClick={() => pickItem({ kind: "action", action: a })}
                        className={rowClass(isActive)}
                      >
                        <Icon className={iconClass(isActive)} />
                        <span className="min-w-0 flex-1 text-sm text-slate-700">{a.label}</span>
                        {isActive && <CornerDownLeft className="h-3.5 w-3.5 shrink-0 text-slate-300" />}
                      </button>
                    )
                  })}
                </Group>
              )}

              {/* — Recent lane (empty query) —————————————— */}
              {parsed.mode === "recent" && (
                <Group label={t("palette.recent")}>
                  {recent.length === 0 ? (
                    <p className="px-4 py-6 text-center text-sm text-muted">{t("palette.recent.empty")}</p>
                  ) : recent.map((e, i) => {
                    const isActive = i === active
                    const Icon = ICONS[e.type] ?? Search
                    return (
                      <button
                        key={`${e.type}-${e.link}`}
                        type="button"
                        data-testid={`palette-recent-${i}`}
                        onMouseEnter={() => setActive(i)}
                        onClick={() => pickItem({ kind: "recent", entity: e })}
                        className={rowClass(isActive)}
                      >
                        <Icon className={iconClass(isActive)} />
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-sm text-slate-700">{e.title}</span>
                          {e.subtitle && <span className="block truncate text-xs text-muted">{e.subtitle}</span>}
                        </span>
                        {isActive && <CornerDownLeft className="h-3.5 w-3.5 shrink-0 text-slate-300" />}
                      </button>
                    )
                  })}
                </Group>
              )}

              {/* — Search lane (with optional @type filter) ————————— */}
              {parsed.mode === "search" && (
                parsed.term.length < 2 ? (
                  <p className="px-4 py-6 text-center text-sm text-muted">{t("palette.empty.hint")}</p>
                ) : groups.length === 0 ? (
                  <p className="px-4 py-6 text-center text-sm text-muted">
                    {isFetching ? t("palette.searching") : t("palette.noMatches", { term: parsed.term })}
                  </p>
                ) : (
                  groups.map((g) => {
                    const Icon = ICONS[g.type] ?? Search
                    return (
                      <Group key={g.type} label={g.label}>
                        {g.hits.map((h) => {
                          // The flat index of THIS hit (groups can be filtered).
                          const idx = flat.findIndex((f) => f.kind === "hit" && f.link === h.link && f.type === g.type)
                          const isActive = idx === active
                          return (
                            <button key={`${g.type}-${h.link}-${h.title}`}
                              onMouseEnter={() => setActive(idx)}
                              onClick={() => goLink(h.link, g.type, h.title, h.subtitle)}
                              className={rowClass(isActive)}>
                              <Icon className={iconClass(isActive)} />
                              <span className="min-w-0 flex-1">
                                <span className="block truncate text-sm text-slate-700">{h.title}</span>
                                {h.subtitle && <span className="block truncate text-xs text-muted">{h.subtitle}</span>}
                              </span>
                              {isActive && <CornerDownLeft className="h-3.5 w-3.5 shrink-0 text-slate-300" />}
                            </button>
                          )
                        })}
                      </Group>
                    )
                  })
                )
              )}
            </div>
          </div>
        </div>
      )}
    </>
  )
}

/* ── Helpers ──────────────────────────────────────────────────────────────── */

function rowClass(isActive: boolean) {
  return `flex w-full items-center gap-3 rounded-md px-3 py-2 text-left ${isActive ? "bg-[var(--brand)]/10" : "hover:bg-[color-mix(in_oklab,var(--text)_6%,transparent)]"}`
}
function iconClass(isActive: boolean) {
  return `h-4 w-4 shrink-0 ${isActive ? "text-[var(--brand)]" : "text-muted"}`
}

function Group({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="px-1 py-1">
      <div className="px-3 py-1 text-[11px] font-semibold uppercase tracking-wide text-muted">{label}</div>
      {children}
    </div>
  )
}

/**
 * Parse the raw input into one of three modes. Pure function (no React) so it
 * stays unit-testable; co-located here because the palette is its only caller
 * and inlining keeps the data flow obvious at the call site.
 *
 *   ""             → recent mode (last 5)
 *   "> ..."        → action mode, term = "..."
 *   "@p Sun"       → search mode + entityType=project, term="Sun"
 *   "Sun"          → search mode, term="Sun" (no filter)
 *
 * Whitespace after the prefix is consumed; the rest is the term verbatim.
 */
export function parseQuery(raw: string): { mode: "recent"; term: "" }
                                       | { mode: "actions"; term: string }
                                       | { mode: "search"; entityType: string | null; term: string } {
  const q = raw.trimStart()
  if (q.length === 0) return { mode: "recent", term: "" }
  if (q.startsWith(">")) return { mode: "actions", term: q.slice(1).trim() }
  if (q.startsWith("@")) {
    // Split off the prefix word (`@p`, `@proj`, …) from the rest of the query.
    const space = q.search(/\s/)
    const tag = (space < 0 ? q.slice(1) : q.slice(1, space)).toLowerCase()
    const rest = space < 0 ? "" : q.slice(space + 1).trim()
    const entityType = TYPE_PREFIXES[tag] ?? null
    // Unknown prefix → treat the whole input as a search term (so the user
    // gets matches instead of an empty result). The PARSED entityType stays
    // null so no filter applies.
    if (!entityType) return { mode: "search", entityType: null, term: q.trim() }
    return { mode: "search", entityType, term: rest }
  }
  return { mode: "search", entityType: null, term: q.trim() }
}

function buildActions(t: (key: string, vars?: Record<string, string | number>) => string): ActionDef[] {
  return [
    {
      id: "new-project",
      label: t("palette.act.newProject"),
      icon: Plus,
      keywords: ["new", "create", "project", "add"],
      enabled: (c) => c.isAdmin,
      // The projects page reads ?new=1 on first render and pops the modal.
      // Replace state so we don't trap the user in a re-open loop on back.
      run: (c) => c.router.push("/projects?new=1"),
    },
    {
      id: "settings",
      label: t("palette.act.settings"),
      icon: SettingsIcon,
      keywords: ["settings", "config", "tenant", "branding"],
      run: (c) => c.router.push("/settings"),
    },
    {
      id: "go-projects",
      label: t("palette.act.projects"),
      icon: ArrowRight,
      keywords: ["projects", "list", "all"],
      run: (c) => c.router.push("/projects"),
    },
    {
      id: "go-resources",
      label: t("palette.act.resources"),
      icon: Library,
      keywords: ["resources", "library", "catalog"],
      run: (c) => c.router.push("/resources"),
    },
    {
      id: "sign-out",
      label: t("palette.act.signOut"),
      icon: LogOut,
      keywords: ["sign", "out", "logout", "log out", "exit"],
      run: (c) => c.logout(),
    },
  ]
}
