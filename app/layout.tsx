import type { Metadata } from "next"
import "./globals.css"
import { Providers } from "./providers"

export const metadata: Metadata = {
  title: "BidBuilder",
  description: "Construction bid estimating",
}

/**
 * 28.1 — No-flash dark-mode boot script.
 *
 * Why this lives here (inline, before hydration): if we let React set
 * data-theme="dark" from the useTheme effect, the page would render light
 * for a frame and then snap to dark — the classic FOUC. By running this
 * synchronously in <head> via dangerouslySetInnerHTML, we set the
 * attribute BEFORE the first paint, so dark-mode users never see the light
 * background.
 *
 * The logic must stay in lockstep with lib/useTheme.ts: read
 * localStorage("bb.theme"), fall back to prefers-color-scheme when the
 * value is "system" / missing, and write the resolved light/dark to
 * <html data-theme>. We can't import from lib/ here — this runs as a raw
 * string before any module loads — so the duplication is intentional and
 * documented at both ends.
 *
 * Failure modes are silent: a try/catch around localStorage covers Safari
 * private mode + storage-disabled browsers. If anything throws, we leave
 * the attribute unset and CSS falls back to the light defaults.
 */
const THEME_BOOT = `(function(){try{
  var p = localStorage.getItem('bb.theme');
  if (p !== 'light' && p !== 'dark' && p !== 'system') p = 'system';
  var r = p === 'system'
    ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
    : p;
  document.documentElement.setAttribute('data-theme', r);
}catch(e){}})();`

export default function RootLayout({ children }: { children: React.ReactNode }) {
  // suppressHydrationWarning on <html> — the boot script sets data-theme
  // before hydration, so server (no attribute) and client (attribute set)
  // legitimately differ on that one prop. React warns by default; we silence
  // it here on this single element only.
  return (
    <html lang="en" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: THEME_BOOT }} />
      </head>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  )
}
