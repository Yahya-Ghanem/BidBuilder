"use client"

import { useCallback, useEffect, useState } from "react"
import { fetchApi } from "@/lib/api"
import type { ThemePreference } from "@/lib/types"

/**
 * 28.1 — Dark mode.
 *
 * Three things live together because they have to stay in sync:
 *
 *   1. The *preference* — "system" | "light" | "dark", the user's intent.
 *      Persisted in two places: localStorage (so the boot script in
 *      layout.tsx can apply it before React hydrates — no FOUC) AND on the
 *      server via PUT /api/preferences/theme (so the choice follows the user
 *      to other devices / browsers after they sign in).
 *
 *   2. The *resolved* theme — "light" | "dark", what's actually on the page.
 *      When the preference is "system" this tracks `prefers-color-scheme`
 *      live: flipping the OS theme while the tab is open flips the app.
 *
 *   3. The DOM — `<html data-theme="…">`. Set from this hook on the client
 *      and from the boot script on first paint. Always equal to the resolved
 *      theme, never the raw preference.
 *
 * The boot script in app/layout.tsx is intentionally a duplicate of the
 * resolve() logic below — it has to run *before* React, in a regular
 * <script>, so it can't share code. Keep the two in sync (system → OS,
 * unrecognised values → "light").
 *
 * SSR safety: every browser-only API is gated behind `typeof window`. The
 * hook returns the safe default ("system" / "light") during SSR and on the
 * first client render before the effect mounts, matching what the boot
 * script wrote so React doesn't see a mismatch.
 */

const STORAGE_KEY = "bb.theme"
const VALID = new Set<ThemePreference>(["system", "light", "dark"])

/** Read the persisted user pick, falling back to "system" if missing/bad. */
export function readStoredTheme(): ThemePreference {
  if (typeof window === "undefined") return "system"
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    return VALID.has(raw as ThemePreference) ? (raw as ThemePreference) : "system"
  } catch {
    // Safari private mode / disabled storage — silently degrade to default.
    return "system"
  }
}

/** Resolve a preference to a concrete light/dark value, consulting the OS
 *  media query when the user picked "system". */
export function resolveTheme(pref: ThemePreference): "light" | "dark" {
  if (pref !== "system") return pref
  if (typeof window === "undefined") return "light"
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light"
}

/** Apply the resolved theme to the <html> element. Idempotent. */
function applyTheme(resolved: "light" | "dark") {
  if (typeof document === "undefined") return
  document.documentElement.setAttribute("data-theme", resolved)
}

/**
 * Hook for the toggle UI. Returns the current preference, the resolved value
 * (so callers can render an icon based on what's actually shown), and a
 * setter that persists locally, applies to the DOM, and syncs to the server
 * (best-effort — a failed PUT logs only; the local pick still applies).
 */
export function useTheme() {
  // Server-render returns the safe default. The matching boot script will
  // have applied the real value to <html> before this component hydrates.
  const [preference, setPreference] = useState<ThemePreference>("system")
  const [resolved, setResolved] = useState<"light" | "dark">("light")

  // First mount: read the persisted preference + resolve. We deliberately
  // *don't* call applyTheme() here — the boot script already did, and
  // re-setting the attribute would just trigger transition flicker.
  useEffect(() => {
    const initial = readStoredTheme()
    setPreference(initial)
    setResolved(resolveTheme(initial))
  }, [])

  // When the preference is "system", react to OS theme changes live so the
  // app flips alongside Settings → Appearance without a manual refresh.
  useEffect(() => {
    if (preference !== "system" || typeof window === "undefined") return
    const mq = window.matchMedia("(prefers-color-scheme: dark)")
    const onChange = () => {
      const next = mq.matches ? "dark" : "light"
      setResolved(next)
      applyTheme(next)
    }
    // addEventListener has wider support than the legacy addListener API,
    // and on older Safari versions matchMedia returns a MediaQueryList whose
    // change event is on the parent EventTarget.
    mq.addEventListener?.("change", onChange)
    return () => mq.removeEventListener?.("change", onChange)
  }, [preference])

  const setTheme = useCallback((next: ThemePreference) => {
    if (!VALID.has(next)) return
    setPreference(next)
    const r = resolveTheme(next)
    setResolved(r)
    applyTheme(r)
    try { window.localStorage.setItem(STORAGE_KEY, next) } catch { /* ignore */ }
    // Server sync — best-effort. The local pick is the source of truth for
    // *this* device on the next load (the boot script reads localStorage);
    // the server value is what other devices will pull on their next sign-in.
    fetchApi("/api/preferences/theme", { method: "PUT", body: JSON.stringify({ theme: next }) })
      .catch(() => { /* offline / unauthenticated — local-only pick is still applied */ })
  }, [])

  return { preference, resolved, setTheme }
}
