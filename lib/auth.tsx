"use client"

import { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from "react"
import { useRouter } from "next/navigation"
import { API_URL, session } from "@/lib/api"
import type { AuthUser } from "@/lib/types"

/** Optional second factor supplied on the MFA step of login (20.8). */
export interface LoginSecondFactor { totpCode?: string; recoveryCode?: string }
/** "ok" = signed in; "mfa" = password accepted, a second factor is required. */
export type LoginResult = "ok" | "mfa"

interface AuthContextType {
  user: AuthUser | null
  isLoading: boolean
  isAuthenticated: boolean
  login: (email: string, password: string, tenant: string, second?: LoginSecondFactor) => Promise<LoginResult>
  logout: () => void
  error: string | null
}

const AuthContext = createContext<AuthContextType | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  // Rehydrate from localStorage on mount.
  useEffect(() => {
    setUser(session.getUser())
    setIsLoading(false)
  }, [])

  const login = useCallback(async (
    email: string, password: string, tenant: string, second?: LoginSecondFactor,
  ): Promise<LoginResult> => {
    setError(null)
    // Persist the tenant first so the login request carries the right header.
    localStorage.setItem("bb_tenant", tenant)
    // Raw fetch (not fetchApi): we must read the body on a 401 to tell a wrong
    // 2FA code (mfaRequired) apart from a generic auth failure.
    const res = await fetch(`${API_URL}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Tenant-Id": tenant },
      body: JSON.stringify({ email, password, totpCode: second?.totpCode, recoveryCode: second?.recoveryCode }),
    })
    const body: unknown = await res.json().catch(() => ({}))
    const b = (body ?? {}) as { token?: string; user?: AuthUser; mfaRequired?: boolean; error?: string }

    if (res.ok && b.token && b.user) {
      session.set(b.token, tenant, b.user)
      setUser(b.user)
      return "ok"
    }
    // Password accepted, second factor needed (no token issued yet).
    if (res.ok && b.mfaRequired) return "mfa"

    const message = b.error || "Login failed"
    setError(message)
    const err = new Error(message) as Error & { mfaRequired?: boolean }
    err.mfaRequired = b.mfaRequired === true   // true ⇒ bad/again-needed code on the MFA step
    throw err
  }, [])

  const logout = useCallback(() => {
    session.clear()
    setUser(null)
  }, [])

  return (
    <AuthContext.Provider
      value={{ user, isLoading, isAuthenticated: user !== null, login, logout, error }}
    >
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error("useAuth must be used within AuthProvider")
  return ctx
}

/** Redirect to /login when not authenticated. */
export function useRequireAuth() {
  const { isLoading, isAuthenticated } = useAuth()
  const router = useRouter()
  useEffect(() => {
    if (!isLoading && !isAuthenticated) router.replace("/login")
  }, [isLoading, isAuthenticated, router])
  return { isLoading, isAuthenticated }
}
