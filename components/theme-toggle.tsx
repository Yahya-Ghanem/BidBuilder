"use client"

import { Monitor, Moon, Sun } from "lucide-react"
import { useEffect, useRef, useState } from "react"
import { useTheme } from "@/lib/useTheme"
import { useT } from "@/lib/i18n"
import { cn } from "@/lib/utils"
import type { ThemePreference } from "@/lib/types"

/**
 * 28.1 — Theme toggle that lives in the sidebar footer, next to Account /
 * Sign out. Renders as a single button showing the *current* resolved icon
 * (sun in light, moon in dark, monitor when preference is "system"). Clicking
 * opens a tiny popover with all three options labelled.
 *
 * Why a popover and not a 3-way segmented control inline:
 *   - The sidebar footer is dense (name, role, account link, sign-out).
 *     A 3-way control eats horizontal space and would push the layout.
 *   - Switching theme is rare — once-per-session at most. Burying it one
 *     click deep is fine for that frequency, and the popover lets us
 *     label each option for screen readers without abbreviation.
 *
 * a11y:
 *   - The trigger has an explicit aria-label (current theme) + aria-haspopup.
 *   - The popover has role="menu", each option role="menuitemradio" so AT
 *     announces "selected" on the active choice.
 *   - Click-outside + Escape both close.
 *   - Focus moves into the menu on open; closes on selection.
 */
export function ThemeToggle({ className }: { className?: string }) {
  const { preference, resolved, setTheme } = useTheme()
  const t = useT()
  const [open, setOpen] = useState(false)
  const wrapperRef = useRef<HTMLDivElement | null>(null)
  const firstItemRef = useRef<HTMLButtonElement | null>(null)

  // Close on Escape or click outside. The handlers attach only while the
  // popover is open so we're not paying for them otherwise.
  useEffect(() => {
    if (!open) return
    const onDocClick = (ev: MouseEvent) => {
      if (wrapperRef.current && !wrapperRef.current.contains(ev.target as Node)) setOpen(false)
    }
    const onKey = (ev: KeyboardEvent) => { if (ev.key === "Escape") setOpen(false) }
    document.addEventListener("mousedown", onDocClick)
    document.addEventListener("keydown", onKey)
    // Move focus into the menu so keyboard users can cycle with Tab.
    firstItemRef.current?.focus()
    return () => {
      document.removeEventListener("mousedown", onDocClick)
      document.removeEventListener("keydown", onKey)
    }
  }, [open])

  // Trigger icon mirrors what's actually on the page so the affordance is
  // honest: in system-following dark, the sidebar shows a moon, not a monitor.
  const TriggerIcon = preference === "system" ? Monitor : (resolved === "dark" ? Moon : Sun)
  const triggerLabel = t(
    preference === "system" ? "theme.triggerSystem" :
    preference === "dark"   ? "theme.triggerDark"   :
                              "theme.triggerLight"
  )

  return (
    <div ref={wrapperRef} className={cn("relative", className)}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-label={triggerLabel}
        aria-haspopup="menu"
        aria-expanded={open}
        className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm text-[var(--text)] hover:bg-[color-mix(in_oklab,var(--text)_8%,transparent)]"
      >
        <TriggerIcon className="h-4 w-4" />
        <span className="flex-1 text-start">{t("theme.label")}</span>
        <span className="text-xs text-muted">{t(`theme.opt.${preference}`)}</span>
      </button>

      {open && (
        <div
          role="menu"
          aria-label={t("theme.label")}
          className="absolute bottom-full start-0 z-50 mb-2 w-44 rounded-md border border-[var(--border)] bg-[var(--card)] p-1 shadow-lg"
        >
          {(["system", "light", "dark"] as ThemePreference[]).map((opt, i) => {
            const Icon = opt === "system" ? Monitor : opt === "dark" ? Moon : Sun
            const selected = preference === opt
            return (
              <button
                key={opt}
                ref={i === 0 ? firstItemRef : undefined}
                role="menuitemradio"
                aria-checked={selected}
                type="button"
                onClick={() => { setTheme(opt); setOpen(false) }}
                className={cn(
                  "flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm",
                  selected ? "bg-[var(--brand)]/15 text-[var(--brand)]" : "text-[var(--text)] hover:bg-[color-mix(in_oklab,var(--text)_8%,transparent)]",
                )}
              >
                <Icon className="h-4 w-4" />
                {t(`theme.opt.${opt}`)}
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}
