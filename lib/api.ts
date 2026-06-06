/**
 * JSON API client for BidBuilder. Attaches the JWT (Authorization: Bearer) and
 * the tenant slug (X-Tenant-Id) the backend's TenantResolutionMiddleware reads.
 * Throws ApiError on non-2xx; a 401 clears the session.
 */

import type { AuthUser } from "@/lib/types"

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"

const TOKEN_KEY = "bb_token"
const TENANT_KEY = "bb_tenant"
const USER_KEY = "bb_user"

/** Narrow an untrusted localStorage payload to a well-formed AuthUser. */
function isAuthUser(u: unknown): u is AuthUser {
  return !!u && typeof u === "object"
    && typeof (u as AuthUser).id === "number"
    && typeof (u as AuthUser).name === "string"
    && typeof (u as AuthUser).email === "string"
    && typeof (u as AuthUser).role === "string"
}

export const session = {
  getToken: () => (typeof window === "undefined" ? null : localStorage.getItem(TOKEN_KEY)),
  getTenant: () => (typeof window === "undefined" ? "default" : localStorage.getItem(TENANT_KEY) ?? "default"),
  getUser: (): AuthUser | null => {
    if (typeof window === "undefined") return null
    const raw = localStorage.getItem(USER_KEY)
    if (!raw) return null
    // Validate at this trust boundary: a malformed or legacy payload must not
    // propagate as a bad AuthUser — drop it and fail closed to signed-out.
    try {
      const parsed: unknown = JSON.parse(raw)
      if (isAuthUser(parsed)) return parsed
    } catch { /* corrupt JSON */ }
    localStorage.removeItem(USER_KEY)
    return null
  },
  set: (token: string, tenant: string, user: unknown) => {
    localStorage.setItem(TOKEN_KEY, token)
    localStorage.setItem(TENANT_KEY, tenant)
    localStorage.setItem(USER_KEY, JSON.stringify(user))
  },
  clear: () => {
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(USER_KEY)
    // keep tenant so the login form stays pre-filled
  },
}

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly body: unknown, message: string) {
    super(message)
    this.name = "ApiError"
  }
}

export async function fetchApi<T>(path: string, options?: RequestInit): Promise<T> {
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    "X-Tenant-Id": session.getTenant(),
    ...(options?.headers as Record<string, string> | undefined),
  }
  const token = session.getToken()
  if (token) headers["Authorization"] = `Bearer ${token}`

  const res = await fetch(`${API_URL}${path}`, { ...options, headers })

  if (!res.ok) {
    if (res.status === 401) {
      session.clear()
      if (typeof window !== "undefined" && !window.location.pathname.startsWith("/login")) {
        window.location.href = "/login"
      }
      throw new ApiError(401, null, "Your session has expired. Please sign in again.")
    }
    const body = await res.json().catch(() => ({}))
    const msg =
      (body && typeof body === "object" && "error" in body && typeof (body as any).error === "string" && (body as any).error) ||
      `Request failed (${res.status})`
    throw new ApiError(res.status, body, msg)
  }

  if (res.status === 204) return undefined as T
  return res.json()
}

/** Upload a single file (multipart/form-data) to an authenticated endpoint.
 *  Does NOT set Content-Type — the browser adds the multipart boundary. */
export async function uploadFile<T>(path: string, file: File, extraHeaders?: Record<string, string>): Promise<T> {
  const headers: Record<string, string> = { "X-Tenant-Id": session.getTenant(), ...(extraHeaders ?? {}) }
  const token = session.getToken()
  if (token) headers["Authorization"] = `Bearer ${token}`

  const form = new FormData()
  form.append("file", file)

  const res = await fetch(`${API_URL}${path}`, { method: "POST", headers, body: form })
  if (!res.ok) {
    if (res.status === 401) {
      session.clear()
      if (typeof window !== "undefined" && !window.location.pathname.startsWith("/login")) window.location.href = "/login"
      throw new ApiError(401, null, "Your session has expired. Please sign in again.")
    }
    const body = await res.json().catch(() => ({}))
    const msg = (body && typeof body === "object" && "error" in body && typeof (body as any).error === "string" && (body as any).error) || `Upload failed (${res.status})`
    throw new ApiError(res.status, body, msg)
  }
  if (res.status === 204) return undefined as T
  return res.json()
}

/** Download a file from an authenticated endpoint (attaches token + tenant). */
export async function downloadFile(path: string, filename: string): Promise<void> {
  const headers: Record<string, string> = { "X-Tenant-Id": session.getTenant() }
  const token = session.getToken()
  if (token) headers["Authorization"] = `Bearer ${token}`

  const res = await fetch(`${API_URL}${path}`, { headers })
  if (!res.ok) {
    const body = await res.json().catch(() => ({}))
    throw new ApiError(res.status, body, (body as any)?.error || `Download failed (${res.status})`)
  }
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  const a = document.createElement("a")
  a.href = url
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}

/** Fetch a binary resource with auth and return an object URL (caller must revoke it). */
export async function fetchObjectUrl(path: string): Promise<string> {
  const headers: Record<string, string> = { "X-Tenant-Id": session.getTenant() }
  const token = session.getToken()
  if (token) headers["Authorization"] = `Bearer ${token}`

  const res = await fetch(`${API_URL}${path}`, { headers })
  if (!res.ok) throw new ApiError(res.status, null, `Fetch failed (${res.status})`)
  return URL.createObjectURL(await res.blob())
}
