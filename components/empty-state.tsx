"use client"
/**
 * 26.5 — `<EmptyState>` — the affordance a fresh tenant sees on every page
 * before they have data.
 *
 * Pre-26.5, an empty Projects / Resources / Quotes / Templates surface
 * rendered as a single muted line — *"No projects."* / *"No items yet."*
 * That tells the user nothing about *why* the surface is empty or *how* to
 * start. New tenants either bounce or open docs in another tab. This
 * component is the single place that pattern lives, so the 6 first-time
 * screens it powers (Projects · Resources · Assemblies · Subcontractor
 * Quotes · Quotes Register · Templates) read the same way.
 *
 * API:
 *   • illustration  ReactNode | string — render an inline SVG component
 *                   (preferred — colour-aware via currentColor) or a /public
 *                   path for a static asset. Keep ≤ 200×140px so the empty
 *                   state doesn't dominate the viewport.
 *   • title         Bold one-liner naming what the user *would* see here.
 *   • body          One or two-sentence explanation of what this surface
 *                   does + why it's empty + the very next step.
 *   • cta           Optional Button-ish ReactNode rendered under the body.
 *                   Use to open the create modal directly. Omit on read-
 *                   only surfaces (e.g. templates-card when the user can
 *                   only import, not create here).
 *   • secondaryCta  Optional second action (e.g. "Import…" next to "New").
 *
 * Layout: vertically-centered card with the illustration above the title.
 * Width clamps to keep the body readable on a wide window — empty states
 * shouldn't stretch the full table width.
 *
 * Accessibility: the outer wrapper is `role="status"` so screen readers
 * announce the empty state when it appears. If an illustration is decorative
 * (SVG component), pass `aria-hidden` on its root in your inline SVG.
 */
import type { ReactNode } from "react"
import { cn } from "@/lib/utils"

export function EmptyState({
  illustration, title, body, cta, secondaryCta, className,
}: {
  illustration?: ReactNode | string
  title: string
  body: string | ReactNode
  cta?: ReactNode
  secondaryCta?: ReactNode
  className?: string
}) {
  const illu = typeof illustration === "string"
    // String path → render via <img>. Width/height capped via the wrapper
    // so a too-large source still fits the empty-state column.
    ? <img src={illustration} alt="" aria-hidden className="max-h-32 max-w-[200px]" />
    : illustration

  return (
    <div
      role="status"
      className={cn(
        "mx-auto flex max-w-md flex-col items-center gap-3 px-6 py-10 text-center",
        className,
      )}
    >
      {illu && <div className="text-muted">{illu}</div>}
      <h3 className="text-base font-semibold text-slate-700">{title}</h3>
      <p className="text-sm text-muted">{body}</p>
      {(cta || secondaryCta) && (
        <div className="mt-2 flex flex-wrap items-center justify-center gap-2">
          {cta}
          {secondaryCta}
        </div>
      )}
    </div>
  )
}
