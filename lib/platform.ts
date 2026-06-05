/**
 * Standalone client for the platform (SuperAdmin) console. Deliberately isolated
 * from lib/api.ts: a platform session has NO tenant, so it must not send an
 * X-Tenant-Id header, and its token is stored under its own key so it never
 * collides with (or clobbers) a normal tenant login in the same browser.
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
const PLATFORM_TOKEN_KEY = "bb_platform_token"

export const platformSession = {
  get: () => (typeof window === "undefined" ? null : localStorage.getItem(PLATFORM_TOKEN_KEY)),
  set: (token: string) => localStorage.setItem(PLATFORM_TOKEN_KEY, token),
  clear: () => localStorage.removeItem(PLATFORM_TOKEN_KEY),
}

export class PlatformError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
    this.name = "PlatformError"
  }
}

export async function platformApi<T>(path: string, options?: RequestInit): Promise<T> {
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    ...(options?.headers as Record<string, string> | undefined),
  }
  const token = platformSession.get()
  if (token) headers["Authorization"] = `Bearer ${token}`

  const res = await fetch(`${API_URL}${path}`, { ...options, headers })

  if (!res.ok) {
    if (res.status === 401) {
      platformSession.clear()
      throw new PlatformError(401, "Your platform session has expired. Please sign in again.")
    }
    const body = await res.json().catch(() => ({}))
    const msg =
      (body && typeof body === "object" && "error" in body && typeof (body as { error?: unknown }).error === "string"
        && (body as { error: string }).error) || `Request failed (${res.status})`
    throw new PlatformError(res.status, msg)
  }

  if (res.status === 204) return undefined as T
  return res.json()
}

// ── DTOs (mirror PlatformEndpoints.cs) ──────────────────────────────────────────
export interface PlatformTenant {
  id: string
  slug: string
  name: string
  defaultLocale: string
  isSuspended: boolean
  createdAt: string
  userCount: number
  projectCount: number
}

export interface PlatformLoginResponse {
  token: string
  expiresAt: string
  user: { id: number; name: string; email: string; role: string }
}

export interface CreateTenantInput {
  slug: string
  name: string
  defaultLocale: string
  adminName?: string
  adminEmail?: string
  adminPassword?: string
}
