import { NextResponse, type NextRequest } from "next/server"

/**
 * 29.A.4 — Security headers + CSP for the Next.js shell.
 *
 * Why a middleware (and not just `next.config.mjs` `headers()`): the CSP
 * needs a per-request nonce. The one inline script we ship (the no-flash
 * theme boot in `app/layout.tsx`) must be allow-listed without opening
 * `'unsafe-inline'` to anything else. The middleware mints a nonce per
 * request, writes it into the response CSP, and stamps it into a header the
 * layout reads via `next/headers` to apply `nonce={…}` on the `<script>`.
 *
 * `'strict-dynamic'` is the upgrade path — once Next's own bundled chunks
 * carry the nonce too, we can drop `'self'` from script-src. For now the
 * combination of nonce + 'self' covers our static bundle and the boot
 * script without giving any third-party an inline foothold.
 *
 * Routes excluded: `_next/static`, `_next/image`, asset files. They are
 * static + immutable, served from the same origin, and don't render HTML
 * that the CSP would protect anyway; running the middleware on every
 * static fetch would burn CPU + force dynamic rendering for nothing.
 */
export function middleware(req: NextRequest) {
  const nonce = generateNonce()

  // The exact API origin the SPA fetches. Next bakes NEXT_PUBLIC_API_URL at
  // build time → we read it the same way the client does. When unset
  // (single-origin Caddy deploys), 'self' is enough.
  const apiOrigin = process.env.NEXT_PUBLIC_API_URL || ""
  const connectSrc = ["'self'"]
  if (apiOrigin) connectSrc.push(apiOrigin)

  const csp = [
    `default-src 'self'`,
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'`,
    // Tailwind's compiled CSS lives in /_next/static — 'self' covers it. The
    // inline allowance is for `<style>` blocks Next emits during streaming
    // SSR; there is no clean way to nonce those today (Next 16 caveat).
    `style-src 'self' 'unsafe-inline'`,
    `img-src 'self' data: blob:`,
    `font-src 'self' data:`,
    // blob: covers the export-preview iframe (it loads object-URL PDFs).
    `connect-src ${connectSrc.join(" ")}`,
    `frame-src 'self' blob:`,
    `frame-ancestors 'none'`,
    `base-uri 'self'`,
    `form-action 'self'`,
    `object-src 'none'`,
    `upgrade-insecure-requests`,
  ].join("; ")

  // Forward the nonce to the request so app/layout.tsx can read it via
  // `headers().get("x-nonce")` and stamp it on the inline boot script.
  const requestHeaders = new Headers(req.headers)
  requestHeaders.set("x-nonce", nonce)

  const res = NextResponse.next({ request: { headers: requestHeaders } })

  res.headers.set("Content-Security-Policy", csp)
  res.headers.set(
    "Strict-Transport-Security",
    "max-age=31536000; includeSubDomains; preload",
  )
  res.headers.set("X-Frame-Options", "DENY")
  res.headers.set("X-Content-Type-Options", "nosniff")
  res.headers.set("Referrer-Policy", "strict-origin-when-cross-origin")
  res.headers.set(
    "Permissions-Policy",
    "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
  )

  return res
}

export const config = {
  matcher: [
    // Match every page except static assets and prefetched resources.
    "/((?!_next/static|_next/image|favicon\\.ico|.*\\.(?:svg|png|jpg|jpeg|gif|webp|ico)$).*)",
  ],
}

/**
 * 128-bit nonce, base64-encoded. Edge runtime ships Web Crypto — no Node
 * `crypto` import (which would force a Node runtime).
 */
function generateNonce(): string {
  const bytes = new Uint8Array(16)
  crypto.getRandomValues(bytes)
  let s = ""
  for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i])
  // Edge runtime has btoa; no Buffer needed.
  return btoa(s)
}
