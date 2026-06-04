"use client"

import { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from "react"
import { useRouter } from "next/navigation"
import { fetchApi, session } from "@/lib/api"
import type { AuthUser, LoginResponse } from "@/lib/types"

interface AuthContextType {
  user: AuthUser | null
  isLoading: boolean
  isAuthenticated: boolean
  login: (email: string, password: string, tenant: string) => Promise<void>
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

  const login = useCallback(async (email: string, password: string, tenant: string) => {
    setError(null)
    // Persist the tenant first so the login request carries the right header.
    localStorage.setItem("bb_tenant", tenant)
    try {
      const res = await fetchApi<LoginResponse>("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({ email, password }),
      })
      session.set(res.token, tenant, res.user)
      setUser(res.user)
    } catch (err) {
      const message = err instanceof Error ? err.message : "Login failed"
      setError(message)
      throw err
    }
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
