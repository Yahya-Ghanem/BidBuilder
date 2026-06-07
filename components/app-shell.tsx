"use client"

import Link from "next/link"
import { usePathname } from "next/navigation"
import { LayoutGrid, FolderKanban, Library, Boxes, LogOut, Settings, ScrollText, Users, BarChart3, Menu, X, Receipt, Trophy, HardHat, ShieldCheck } from "lucide-react"
import { useAuth, useRequireAuth } from "@/lib/auth"
import { usePermissions } from "@/lib/permissions"
import { useT } from "@/lib/i18n"
import { cn } from "@/lib/utils"
import { ProjectsTreeProvider, ProjectsSidebarTree } from "@/components/projects-tree"
import { NotificationsBell } from "@/components/notifications-bell"
import { SearchPalette } from "@/components/search-palette"
import { LanguageSwitcher } from "@/components/language-switcher"
import { useState, type ReactNode } from "react"

const NAV = [
  { href: "/projects", labelKey: "nav.projects", icon: FolderKanban, module: "projects" },
  { href: "/resources", labelKey: "nav.resources", icon: Library, module: "resource-library" },
  { href: "/quotes", labelKey: "nav.quotes", icon: Receipt, module: "resource-library" },
  { href: "/subcontractor-quotes", labelKey: "nav.subQuotes", icon: HardHat, module: "projects" },
  { href: "/assemblies", labelKey: "nav.assemblies", icon: Boxes, module: "assemblies" },
  { href: "/benchmarks", labelKey: "nav.benchmarks", icon: BarChart3, module: "reports" },
  { href: "/analytics", labelKey: "nav.analytics", icon: Trophy, module: "reports" },
]

/** Authenticated layout: sidebar + topbar. Redirects to /login if signed out. */
export function AppShell({ children, title }: { children: ReactNode; title: string }) {
  const { isLoading, isAuthenticated } = useRequireAuth()
  const { user, logout } = useAuth()
  const { can, isAdmin } = usePermissions()
  const t = useT()
  const pathname = usePathname()
  // Below md the sidebar is an off-canvas drawer toggled by the header hamburger.
  const [navOpen, setNavOpen] = useState(false)

  if (isLoading || !isAuthenticated) {
    return <div className="grid min-h-screen place-items-center text-muted">{t("shell.loading")}</div>
  }

  // Only show nav entries for modules the user may view (admins see all);
  // Settings is a tenant-admin area.
  const nav = NAV.filter((n) => can(n.module, "view"))
  if (isAdmin) {
    nav.push({ href: "/admin", labelKey: "nav.users", icon: Users, module: "admin" })
    nav.push({ href: "/audit", labelKey: "nav.audit", icon: ScrollText, module: "audit" })
    nav.push({ href: "/settings", labelKey: "nav.settings", icon: Settings, module: "settings" })
  }

  return (
    <ProjectsTreeProvider>
    <div className="grid min-h-screen md:grid-cols-[280px_1fr]">
      {/* Backdrop behind the mobile drawer. */}
      {navOpen && (
        <div className="fixed inset-0 z-30 bg-black/40 md:hidden" aria-hidden="true" onClick={() => setNavOpen(false)} />
      )}
      <aside
        className={cn(
          "fixed inset-y-0 start-0 z-40 flex max-h-screen w-[280px] transform flex-col border-e border-[var(--border)] bg-white transition-transform",
          "md:static md:z-auto md:w-auto md:translate-x-0",
          // In RTL the drawer lives on the right, so it hides by sliding the other way.
          navOpen ? "translate-x-0" : "-translate-x-full rtl:translate-x-full",
        )}
      >
        <div className="flex items-center justify-between px-5 py-4 text-lg font-bold">
          <span className="flex items-center gap-2">
            <LayoutGrid className="h-5 w-5 text-[var(--brand)]" />
            BidBuilder
          </span>
          <button onClick={() => setNavOpen(false)} aria-label={t("shell.closeMenu")} className="rounded p-1 text-muted hover:bg-slate-100 md:hidden">
            <X className="h-5 w-5" />
          </button>
        </div>
        <nav className="flex-1 space-y-1 overflow-auto px-3">
          {nav.map(({ href, labelKey, icon: Icon }) => {
            const active = pathname.startsWith(href)
            return (
              <div key={href}>
                <Link
                  href={href}
                  onClick={() => setNavOpen(false)}
                  className={cn(
                    "flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition",
                    active ? "bg-[var(--brand)]/10 text-[var(--brand)]" : "text-slate-600 hover:bg-slate-100",
                  )}
                >
                  <Icon className="h-4 w-4" />
                  {t(labelKey)}
                </Link>
                {href === "/projects" && pathname.startsWith("/projects") && <ProjectsSidebarTree />}
              </div>
            )
          })}
        </nav>
        <div className="border-t border-[var(--border)] p-3">
          <div className="px-2 pb-2 text-sm">
            <div className="font-medium text-slate-800">{user?.name}</div>
            <div className="text-xs text-slate-500">{user?.role}</div>
          </div>
          <Link
            href="/account"
            onClick={() => setNavOpen(false)}
            className={cn(
              "flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm transition",
              pathname.startsWith("/account") ? "bg-[var(--brand)]/10 text-[var(--brand)]" : "text-slate-600 hover:bg-slate-100",
            )}
          >
            <ShieldCheck className="h-4 w-4" /> {t("shell.account")}
          </Link>
          <button
            onClick={logout}
            className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm text-slate-600 hover:bg-slate-100"
          >
            <LogOut className="h-4 w-4" /> {t("shell.signOut")}
          </button>
        </div>
      </aside>

      <div className="flex min-w-0 flex-col">
        <header className="flex h-14 items-center gap-3 border-b border-[var(--border)] bg-white px-4 md:px-6">
          <button onClick={() => setNavOpen(true)} aria-label={t("shell.openMenu")} className="rounded p-1 text-slate-600 hover:bg-slate-100 md:hidden">
            <Menu className="h-5 w-5" />
          </button>
          <h1 className="truncate text-lg font-semibold">{title}</h1>
          <div className="ms-auto flex items-center gap-2">
            <LanguageSwitcher />
            <SearchPalette />
            <NotificationsBell />
          </div>
        </header>
        <main className="flex-1 overflow-auto p-4 sm:p-6">{children}</main>
      </div>
    </div>
    </ProjectsTreeProvider>
  )
}
