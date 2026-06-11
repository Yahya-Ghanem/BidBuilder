"use client"

import type { ReactNode } from "react"
import { useFlag } from "@/lib/useFlag"

/**
 * 29.B.1 — Declarative feature gate.
 *
 * Wrap any chunk of UI in a &lt;FeatureGate flag="..."&gt; and it renders only
 * when the flag is enabled for the current tenant. The `fallback` prop is
 * the off-state replacement — usually `null` (hide entirely) but sometimes a
 * placeholder/empty state when the surrounding layout needs to stay stable.
 *
 * Why a wrapper component (and not just `useFlag` inline): keeps the
 * "feature ↔ surface" mapping greppable. `git grep flag="export-preview"`
 * shows every callsite gated by that flag — handy when an operator asks
 * "what surfaces does this kill switch turn off?".
 */
export function FeatureGate({
  flag,
  children,
  fallback = null,
}: {
  flag: string
  children: ReactNode
  fallback?: ReactNode
}) {
  return useFlag(flag) ? <>{children}</> : <>{fallback}</>
}
