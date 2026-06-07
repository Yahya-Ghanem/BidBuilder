/**
 * 26.5 — Line-art SVG illustrations for empty states. One component per
 * first-time surface so the `<EmptyState>` consumer picks the matching one
 * by name. All illustrations are stroke-only with `currentColor` so they
 * inherit the parent's text color (we put them in a `text-muted` wrapper
 * inside EmptyState).
 *
 * Why React components (not /public/illustrations/*.svg files): the
 * roadmap hints at static SVGs but `<img src=...>` doesn't inherit
 * `currentColor`, so light-mode / dark-mode / brand-tinted variants would
 * need separate files. As inline JSX they're one-time tree-shaken per
 * usage and pick up tone changes for free. Each illustration is ≤ 1KB
 * minified.
 *
 * Style notes:
 *   • viewBox 0 0 200 140 — keeps the 5:3.5 aspect ratio that fits the
 *     200×140 cap the EmptyState wrapper enforces.
 *   • strokeWidth 1.6 — readable at 200px wide, doesn't get heavy when
 *     scaled down to ~80px on narrow viewports.
 *   • aria-hidden — illustrations are decorative; the EmptyState's title
 *     and body carry the meaning.
 */
import type { SVGProps } from "react"

const baseProps: SVGProps<SVGSVGElement> = {
  viewBox: "0 0 200 140",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.6,
  strokeLinecap: "round",
  strokeLinejoin: "round",
  "aria-hidden": true,
}

/** Projects — stacked folder tabs hinting at a portfolio. */
export function ProjectsIllustration(props: SVGProps<SVGSVGElement>) {
  return (
    <svg {...baseProps} {...props}>
      <rect x="30" y="40" width="120" height="80" rx="6" />
      <path d="M30 56 h120" />
      <path d="M55 40 v-8 a4 4 0 0 1 4 -4 h28 a4 4 0 0 1 4 4 v8" />
      <rect x="50" y="55" width="100" height="60" rx="4" opacity="0.6" />
      <path d="M70 75 h60" opacity="0.4" />
      <path d="M70 90 h40" opacity="0.4" />
      <path d="M70 105 h50" opacity="0.4" />
    </svg>
  )
}

/** Resources — a row of price-tagged supply icons. */
export function ResourcesIllustration(props: SVGProps<SVGSVGElement>) {
  return (
    <svg {...baseProps} {...props}>
      {/* Box */}
      <rect x="32" y="60" width="40" height="40" rx="3" />
      <path d="M32 75 h40" />
      <path d="M52 60 v40" opacity="0.4" />
      {/* Bag of cement */}
      <path d="M88 64 h32 v36 a4 4 0 0 1 -4 4 h-24 a4 4 0 0 1 -4 -4 z" />
      <path d="M96 64 v-6 h16 v6" />
      {/* Price tag */}
      <path d="M138 70 l24 0 l8 8 l-24 24 l-8 -8 z" />
      <circle cx="148" cy="80" r="3" />
    </svg>
  )
}

/** Assemblies — interconnected blocks. */
export function AssembliesIllustration(props: SVGProps<SVGSVGElement>) {
  return (
    <svg {...baseProps} {...props}>
      <rect x="40" y="40" width="44" height="28" rx="3" />
      <rect x="116" y="40" width="44" height="28" rx="3" />
      <rect x="78" y="86" width="44" height="28" rx="3" />
      <path d="M62 68 l 0 18 m76 -18 l0 18 m-58 -8 h36" />
      <circle cx="62" cy="86" r="2" opacity="0.7" />
      <circle cx="138" cy="86" r="2" opacity="0.7" />
      <path d="M50 50 h24" opacity="0.4" />
      <path d="M126 50 h24" opacity="0.4" />
      <path d="M88 96 h24" opacity="0.4" />
    </svg>
  )
}

/** Subcontractor Quotes — a paper with a signature line. */
export function SubcontractorQuotesIllustration(props: SVGProps<SVGSVGElement>) {
  return (
    <svg {...baseProps} {...props}>
      <rect x="50" y="20" width="100" height="100" rx="6" />
      <path d="M70 40 h60" opacity="0.5" />
      <path d="M70 52 h50" opacity="0.5" />
      <path d="M70 64 h60" opacity="0.5" />
      <path d="M70 76 h40" opacity="0.5" />
      <path d="M70 100 h60" />
      <path d="M76 95 q 8 -10 16 -2 t 16 -2" />
      <circle cx="135" cy="100" r="2" />
    </svg>
  )
}

/** Quotes Register — a stack of quote chits. */
export function QuotesRegisterIllustration(props: SVGProps<SVGSVGElement>) {
  return (
    <svg {...baseProps} {...props}>
      <rect x="30" y="36" width="100" height="60" rx="4" opacity="0.5" />
      <rect x="50" y="48" width="100" height="60" rx="4" opacity="0.7" />
      <rect x="70" y="60" width="100" height="60" rx="4" />
      <path d="M86 76 h60" opacity="0.6" />
      <path d="M86 88 h40" opacity="0.6" />
      <path d="M86 100 h50" opacity="0.6" />
      <circle cx="148" cy="100" r="6" />
      <path d="M145 100 l3 3 l5 -6" opacity="0.7" />
    </svg>
  )
}

/** Templates — document with a bookmark ribbon. */
export function TemplatesIllustration(props: SVGProps<SVGSVGElement>) {
  return (
    <svg {...baseProps} {...props}>
      <rect x="50" y="20" width="100" height="100" rx="6" />
      <path d="M70 44 h60" opacity="0.5" />
      <path d="M70 60 h60" opacity="0.5" />
      <path d="M70 76 h50" opacity="0.5" />
      <path d="M70 92 h60" opacity="0.5" />
      <path d="M124 16 v34 l8 -8 l8 8 v-34 z" />
    </svg>
  )
}
