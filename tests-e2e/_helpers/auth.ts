import { type APIRequestContext, type Page, expect } from "@playwright/test"

/**
 * 29.A.1 — Per-spec E2E auth.
 *
 * Every spec used to sign in through the login UI as the one shared seeded
 * admin account. With 13 specs behind one runner IP that tripped the 10/60s
 * login rate limit, and the "fix" (f835459) loosened the CI limit to 200/IP —
 * a security control papering over fragile tests.
 *
 * Instead, each spec now mints its OWN throwaway TenantAdmin through the
 * dev-only POST /api/test/users endpoint. The endpoint issues the JWT directly
 * (it never touches /api/auth/login), so signing a spec in puts ZERO pressure
 * on the login rate limiter. Only a spec that deliberately exercises the login
 * UI (happy-path) performs a real login — and one login fits comfortably
 * inside the default 10/60s budget.
 */

export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8081"
export const TENANT = "default"

export interface TestUser {
  id: number
  name: string
  email: string
  role: string
  /** Raw password — only needed by specs that drive the real login form. */
  password: string
  /** Ready-to-use JWT minted server-side (not via /api/auth/login). */
  token: string
}

/** Mint a throwaway TenantAdmin via the dev-only test-support endpoint. */
export async function createTestUser(request: APIRequestContext): Promise<TestUser> {
  const res = await request.post(`${API_URL}/api/test/users`, {
    headers: { "X-Tenant-Id": TENANT, "Content-Type": "application/json" },
    data: {},
  })
  expect(res.ok(), `POST /api/test/users → ${res.status()} (is the API running in Development?)`).toBeTruthy()
  const body = (await res.json()) as {
    token: string
    email: string
    password: string
    user: { id: number; name: string; email: string; role: string }
  }
  return {
    id: body.user.id,
    name: body.user.name,
    email: body.email,
    role: body.user.role,
    password: body.password,
    token: body.token,
  }
}

/** Best-effort teardown — never fails a spec over cleanup. */
export async function deleteTestUser(request: APIRequestContext, user: TestUser | undefined): Promise<void> {
  if (!user) return
  try {
    await request.delete(`${API_URL}/api/test/users/${user.id}`, { headers: { "X-Tenant-Id": TENANT } })
  } catch {
    /* the run is over; a leaked e2e-* row in a dev DB is harmless */
  }
}

/**
 * Sign the page in WITHOUT touching /api/auth/login: seed the exact session
 * keys the SPA reads (bb_token / bb_tenant / bb_user — see lib/api.ts) before
 * any document loads, then land on /projects like a fresh post-login session.
 */
export async function signIn(page: Page, user: TestUser): Promise<void> {
  await page.addInitScript(
    ({ token, tenant, authUser }) => {
      localStorage.setItem("bb_token", token)
      localStorage.setItem("bb_tenant", tenant)
      localStorage.setItem("bb_user", JSON.stringify(authUser))
    },
    {
      token: user.token,
      tenant: TENANT,
      authUser: { id: user.id, name: user.name, email: user.email, role: user.role },
    },
  )
  await page.goto("/projects")
  await expect(page).toHaveURL(/\/projects/, { timeout: 15_000 })
}

/** Auth headers for direct API seeding calls from a spec. */
export function authHeaders(user: TestUser): Record<string, string> {
  return { Authorization: `Bearer ${user.token}`, "X-Tenant-Id": TENANT }
}
