"use client"

import { useEffect, useState } from "react"

/** 27.3 — Tailwind's `md` breakpoint. Editing surfaces collapse to read-only
 *  below this width; the AppShell already uses md: to flip the sidebar to an
 *  off-canvas drawer, so JS detection and CSS classes flip in lockstep. */
export const MOBILE_BREAKPOINT_PX = 768

/**
 * 27.3 — SSR-safe viewport hook for the mobile honest-mode gate.
 *
 * Returns `{ isMobile }` where `isMobile` is true when the viewport is
 * `(max-width: 768px)` — exactly below Tailwind's `md` breakpoint. The hook
 * MUST return a stable value during SSR + the first client render to avoid
 * a hydration mismatch (Next.js 16 App Router pre-renders the editor page),
 * so it defaults to `false` (desktop) and only flips to the real value in
 * useEffect. Edits land in the same paint as auth/permission rehydration
 * (lib/auth.tsx pattern).
 *
 * Uses matchMedia + a 'change' listener (not resize + innerWidth) because:
 *   • matchMedia fires once per breakpoint crossing, not on every resize px
 *   • the breakpoint is exact (no off-by-one between 768 and 767.98)
 *   • cleanup is per-listener, not per-element
 */
export function useViewport(): { isMobile: boolean } {
  const [isMobile, setIsMobile] = useState(false)
  useEffect(() => {
    // Tolerate the test environment (jsdom) which lacks matchMedia.
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") return
    const mql = window.matchMedia(`(max-width: ${MOBILE_BREAKPOINT_PX}px)`)
    setIsMobile(mql.matches)
    const onChange = (e: MediaQueryListEvent) => setIsMobile(e.matches)
    mql.addEventListener("change", onChange)
    return () => mql.removeEventListener("change", onChange)
  }, [])
  return { isMobile }
}
