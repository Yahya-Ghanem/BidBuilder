"use client"

/**
 * 25.3 — Accessible tab strip with optional URL deep-linking.
 *
 * Why a primitive? Phase 25 introduces tabbed restructures on the project
 * detail page (here) and the settings page (25.2). Both want the same
 * thing: a flat WAI-ARIA tablist + tabpanel pair where the active tab id
 * lives in `?tab=` so the browser back button works and links into a
 * specific tab can be shared.
 *
 * Mounting behaviour: every panel mounts once and is hidden when inactive
 * (display:none via the HTML `hidden` attribute). Keeping inactive panels
 * mounted preserves their internal state — typed input, scroll position,
 * fetched query — when the user flips between tabs. That's the right
 * default for editor-style surfaces, which is what 25.3 needs.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react"
import { usePathname, useRouter, useSearchParams } from "next/navigation"
import { cn } from "@/lib/utils"

export type TabDef = {
  /** Stable string id — also the `?tab=` value. Keep URL-safe (a–z, dashes). */
  id: string
  /** What appears on the tab button. */
  label: ReactNode
  /** What renders in the panel. */
  content: ReactNode
}

export function Tabs({
  tabs,
  defaultId,
  paramKey = "tab",
  ariaLabel,
  className,
  listClassName,
}: {
  tabs: TabDef[]
  /** Which tab to open when neither the URL nor a previous click has selected one. */
  defaultId?: string
  /** Search-param name carrying the active tab. Defaults to "tab". */
  paramKey?: string
  /** Required for screen-reader users — describes what this set of tabs is. */
  ariaLabel: string
  className?: string
  listClassName?: string
}) {
  const router = useRouter()
  const pathname = usePathname()
  const search = useSearchParams()

  const validIds = useMemo(() => new Set(tabs.map((t) => t.id)), [tabs])
  const fromUrl = search?.get(paramKey)
  const fallback = defaultId && validIds.has(defaultId) ? defaultId : tabs[0]?.id ?? ""

  const [activeId, setActiveId] = useState<string>(() =>
    fromUrl && validIds.has(fromUrl) ? fromUrl : fallback,
  )

  // Reflect URL changes (eg. back/forward, language switch) into local state.
  useEffect(() => {
    if (fromUrl && validIds.has(fromUrl) && fromUrl !== activeId) setActiveId(fromUrl)
  }, [fromUrl, validIds, activeId])

  const setActive = useCallback((id: string) => {
    if (id === activeId) return
    setActiveId(id)
    const sp = new URLSearchParams(search?.toString() ?? "")
    sp.set(paramKey, id)
    router.replace(`${pathname}?${sp.toString()}`, { scroll: false })
  }, [activeId, paramKey, pathname, router, search])

  // Roving-tabindex keyboard nav per the WAI-ARIA tabs pattern: ←/→ cycle,
  // Home/End jump to the ends. Activation happens on focus to keep keyboard
  // and mouse users on the same flow.
  const refs = useRef<Record<string, HTMLButtonElement | null>>({})
  function onKeyDown(e: KeyboardEvent<HTMLButtonElement>) {
    const idx = tabs.findIndex((t) => t.id === activeId)
    let next = idx
    if (e.key === "ArrowRight") next = (idx + 1) % tabs.length
    else if (e.key === "ArrowLeft") next = (idx - 1 + tabs.length) % tabs.length
    else if (e.key === "Home") next = 0
    else if (e.key === "End") next = tabs.length - 1
    else return
    e.preventDefault()
    const target = tabs[next]
    if (!target) return
    setActive(target.id)
    refs.current[target.id]?.focus()
  }

  return (
    <div className={className}>
      <div role="tablist" aria-label={ariaLabel} className={cn(
        "flex flex-wrap gap-1 border-b border-[var(--border)]",
        listClassName,
      )}>
        {tabs.map((t) => {
          const active = t.id === activeId
          return (
            <button
              key={t.id}
              type="button"
              role="tab"
              id={`tab-${t.id}`}
              aria-selected={active}
              aria-controls={`panel-${t.id}`}
              tabIndex={active ? 0 : -1}
              ref={(el) => { refs.current[t.id] = el }}
              onClick={() => setActive(t.id)}
              onKeyDown={onKeyDown}
              className={cn(
                "-mb-px border-b-2 px-4 py-2 text-sm font-medium transition outline-none",
                "focus-visible:bg-slate-50",
                active
                  ? "border-[var(--brand)] text-[var(--brand)]"
                  : "border-transparent text-slate-600 hover:text-slate-900",
              )}
            >
              {t.label}
            </button>
          )
        })}
      </div>
      {tabs.map((t) => (
        <div
          key={t.id}
          role="tabpanel"
          id={`panel-${t.id}`}
          aria-labelledby={`tab-${t.id}`}
          hidden={t.id !== activeId}
          className="pt-4"
        >
          {t.content}
        </div>
      ))}
    </div>
  )
}
