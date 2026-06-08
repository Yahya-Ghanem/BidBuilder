"use client"

import { cn } from "@/lib/utils"
import { ChevronDown } from "lucide-react"
import { useEffect, useRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from "react"

export function Button({
  className, variant = "primary", ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "primary" | "ghost" | "outline" }) {
  // 28.1 — primary button uses --brand-fg (white in light, teal-950 in dark) so
  // the label stays legible inside the brightened dark teal. Ghost/outline use
  // the surface tokens (--card / --text) so they invert under [data-theme=dark]
  // instead of leaking white through.
  const styles = {
    primary: "bg-[var(--brand)] text-[var(--brand-fg)] hover:opacity-90",
    ghost: "bg-transparent hover:bg-[color-mix(in_oklab,var(--text)_8%,transparent)] text-[var(--text)]",
    outline: "border border-[var(--border)] bg-[var(--card)] hover:bg-[color-mix(in_oklab,var(--text)_5%,transparent)] text-[var(--text)]",
  }[variant]
  return (
    <button
      className={cn(
        "inline-flex items-center justify-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition disabled:opacity-50 disabled:pointer-events-none",
        styles, className,
      )}
      {...props}
    />
  )
}

export function Input({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      className={cn(
        // 28.1 — input surface follows --card so the dark theme paints inputs
        // on slate-900 instead of pure white. text-[var(--text)] handles the
        // caret + typed content color.
        "w-full rounded-md border border-[var(--border)] bg-[var(--card)] text-[var(--text)] px-3 py-2 text-sm outline-none",
        "focus:border-[var(--brand)] focus:ring-2 focus:ring-[var(--brand)]/20",
        className,
      )}
      {...props}
    />
  )
}

export function Card({ className, children }: { className?: string; children: ReactNode }) {
  return (
    <div className={cn("rounded-lg border border-[var(--border)] bg-[var(--card)] shadow-sm", className)}>
      {children}
    </div>
  )
}

/**
 * 20.7 — Horizontal-scroll container for data tables on narrow screens. Wrap a
 * `<table>` (give it a sensible `min-w-…`) so columns keep their width and the
 * region scrolls sideways on a phone instead of crushing or breaking the layout.
 */
export function TableScroll({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("w-full overflow-x-auto", className)}>{children}</div>
}

export function Badge({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <span className={cn("inline-flex rounded-full px-2.5 py-0.5 text-xs font-medium", className)}>
      {children}
    </span>
  )
}

/**
 * 25.4 — Dropdown menu attached to a button. Used to collapse the revision-bar
 * lifecycle and export clusters from N visible buttons into a single labelled
 * trigger ("Actions ▾" / "Export ▾"). The menu items are passed as `items` so
 * each consumer keeps its own click handlers / disabled state.
 *
 * Accessibility: the trigger gets `aria-haspopup="menu"` + `aria-expanded`,
 * the menu gets `role="menu"`, each item gets `role="menuitem"`. Click-outside
 * and Escape close the menu. Each item click closes the menu (the consumer's
 * `onSelect` fires first). No internal keyboard nav (Tab order through the
 * items is enough for a < 6-item menu).
 */
export type DropdownItem = {
  /** Stable key for React. */
  key: string
  /** What renders inside the menu item (icon + label). */
  label: ReactNode
  /** Click handler. Menu closes after this fires. */
  onSelect: () => void
  /** Item is dimmed and unclickable when true. */
  disabled?: boolean
  /** Render this item in destructive (rose-600) color. */
  danger?: boolean
}

export function DropdownButton({
  label, items, className, menuClassName, ariaLabel, variant = "outline", disabled,
}: {
  /** What renders on the trigger (icon + text). */
  label: ReactNode
  items: DropdownItem[]
  className?: string
  menuClassName?: string
  /** Required when `label` is icon-only; describes the menu for screen readers. */
  ariaLabel?: string
  variant?: "primary" | "ghost" | "outline"
  disabled?: boolean
}) {
  const [open, setOpen] = useState(false)
  const wrap = useRef<HTMLDivElement>(null)

  // Close on click-outside or Escape so a menu never gets stuck open.
  useEffect(() => {
    if (!open) return
    function onDoc(e: MouseEvent) {
      if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false)
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false)
    }
    document.addEventListener("mousedown", onDoc)
    document.addEventListener("keydown", onKey)
    return () => {
      document.removeEventListener("mousedown", onDoc)
      document.removeEventListener("keydown", onKey)
    }
  }, [open])

  return (
    <div ref={wrap} className="relative inline-block">
      <Button
        type="button"
        variant={variant}
        disabled={disabled}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={ariaLabel}
        onClick={() => setOpen((o) => !o)}
        className={cn("gap-1", className)}
      >
        {label}
        <ChevronDown className="h-3.5 w-3.5" aria-hidden />
      </Button>
      {open && (
        <div
          role="menu"
          aria-label={ariaLabel}
          className={cn(
            "absolute right-0 z-30 mt-1 min-w-[12rem] overflow-hidden rounded-md border border-[var(--border)] bg-[var(--card)] py-1 shadow-md",
            menuClassName,
          )}
        >
          {items.map((it) => (
            <button
              key={it.key}
              type="button"
              role="menuitem"
              disabled={it.disabled}
              onClick={() => { if (!it.disabled) { setOpen(false); it.onSelect() } }}
              className={cn(
                "flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm transition",
                // 28.1 — hover uses a token-mixed wash (light grey in light,
                // light wash on dark slate in dark). The "disabled hover"
                // reset goes back to transparent rather than bg-white so it
                // doesn't punch a white hole through the dark dropdown.
                "hover:bg-[color-mix(in_oklab,var(--text)_6%,transparent)] disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:bg-transparent",
                it.danger ? "text-danger" : "text-[var(--text)]",
              )}
            >
              {it.label}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

/**
 * 26.1 — Status-pill colors map to semantic tokens. Backgrounds keep their
 * 100-tier Tailwind literals (those encode a "stronger soft" tier that the
 * default 50-tier `*-soft` tokens don't cover); only the *text* colors route
 * through tokens so a future palette flip still picks them up.
 */
export function statusColor(status: string) {
  switch (status.toLowerCase()) {
    case "bidding": return "bg-amber-100 text-warning"
    case "won": return "bg-emerald-100 text-success"
    case "lost": return "bg-rose-100 text-danger"
    case "submitted": return "bg-sky-100 text-info"
    case "archived": return "bg-slate-100 text-slate-600"
    default: return "bg-slate-100 text-slate-700"   // draft
  }
}
