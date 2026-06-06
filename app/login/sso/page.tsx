"use client"

import { useEffect, useState } from "react"
import { LayoutGrid } from "lucide-react"
import { session, isAuthUser } from "@/lib/api"
import type { AuthUser } from "@/lib/types"

/**
 * 20.8b — SAML ACS landing page. The API's Assertion Consumer Service redirects the
 * browser here with the issued session in the URL FRAGMENT
 * (#token=…&tenant=…) — the fragment is never sent to a server, so the JWT isn't
 * logged by proxies or access logs. We decode the JWT to populate the local AuthUser,
 * persist the session, then hard-navigate to the app so the AuthProvider rehydrates.
 */
export default function SsoCallbackPage() {
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const hash = window.location.hash.startsWith("#") ? window.location.hash.slice(1) : window.location.hash
    const params = new URLSearchParams(hash)
    const token = params.get("token")
    const tenant = params.get("tenant") ?? "default"

    if (!token) { setError("No sign-in token was returned. Please try again."); return }
    const user = decodeUser(token)
    if (!user) { setError("The sign-in token could not be read. Please try again."); return }

    session.set(token, tenant, user)
    // Full navigation (not client routing) so AuthProvider re-reads localStorage on mount.
    window.location.replace("/projects")
  }, [])

  return (
    <div className="grid min-h-screen place-items-center p-4">
      <div className="flex flex-col items-center gap-3 text-slate-500">
        <LayoutGrid className="h-8 w-8 text-[var(--brand)]" />
        {error ? (
          <>
            <p className="text-rose-600">{error}</p>
            <a href="/login" className="text-sm text-[var(--brand)] hover:underline">Back to sign in</a>
          </>
        ) : (
          <p>Completing sign-in…</p>
        )}
      </div>
    </div>
  )
}

/** Decode the JWT payload (no verification — that already happened server-side) into
 * an AuthUser. Returns null if the token is malformed. */
function decodeUser(token: string): AuthUser | null {
  try {
    const payload = token.split(".")[1]
    if (!payload) return null
    const json = atob(payload.replace(/-/g, "+").replace(/_/g, "/"))
    const c = JSON.parse(json) as Record<string, unknown>
    const user = {
      id: Number(c.sub ?? 0),
      name: String(c.name ?? ""),
      email: String(c.email ?? ""),
      role: String(c.role ?? ""),
    }
    return isAuthUser(user) ? user : null
  } catch {
    return null
  }
}
