"use client"

import Link from "next/link"
import { usePathname } from "next/navigation"
import { LayoutGrid, FolderKanban, Library, Boxes, LogOut, Settings, ScrollText, Users, BarChart3 } from "lucide-react"
import { useAuth, useRequireAuth } from "@/lib/auth"
import { usePermissions } from "@/lib/permissions"
import { cn } from "@/lib/utils"
import { ProjectsTreeProvider, ProjectsSidebarTree } from "@/components/projects-tree"
import type { ReactNode } from "react"

const NAV = [
  { href: "/projects", label: "BidBuilder", icon: FolderKanban, module: "projects" },
  { href: "/resources", label: "Resource Library", icon: Library, module: "resource-library" },
  { href: "/assemblies", label: "Assemblies", icon: Boxes, module: "assemblies" },
  { href: "/benchmarks", label: "Benchmarks", icon: BarChart3, module: "reports" },
]

/** Authenticated layout: sidebar + topbar. Redirects to /login if signed out. */
export function AppShell({ children, title }: { children: ReactNode; title: string }) {
  const { isLoading, isAuthenticated } = useRequireAuth()
  const { user, logout } = useAuth()
  const { can, isAdmin } = usePermissions()
  const pathname = usePathname()

  if (isLoading || !isAuthenticated) {
    return <div className="grid min-h-screen place-items-center text-slate-400">Loading…</div>
  }

  // Only show nav entries for modules the user may view (admins see all);
  // Settings is a tenant-admin area.
  const nav = NAV.filter((n) => can(n.module, "view"))
  if (isAdmin) {
    nav.push({ href: "/admin", label: "Users & Teams", icon: Users, module: "admin" })
    nav.push({ href: "/audit", label: "Audit log", icon: ScrollText, module: "audit" })
    nav.push({ href: "/settings", label: "Settings", icon: Settings, module: "settings" })
  }

  return (
    <ProjectsTreeProvider>
    <div className="grid min-h-screen grid-cols-[280px_1fr]">
      <aside className="flex max-h-screen flex-col border-r border-[var(--border)] bg-white">
        <div className="flex items-center gap-2 px-5 py-4 text-lg font-bold">
          <LayoutGrid className="h-5 w-5 text-[var(--brand)]" />
          BidBuilder
        </div>
        <nav className="flex-1 space-y-1 overflow-auto px-3">
          {nav.map(({ href, label, icon: Icon }) => {
            const active = pathname.startsWith(href)
            return (
              <div key={href}>
                <Link
                  href={href}
                  className={cn(
                    "flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition",
                    active ? "bg-[var(--brand)]/10 text-[var(--brand)]" : "text-slate-600 hover:bg-slate-100",
                  )}
                >
                  <Icon className="h-4 w-4" />
                  {label}
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
          <button
            onClick={logout}
            className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm text-slate-600 hover:bg-slate-100"
          >
            <LogOut className="h-4 w-4" /> Sign out
          </button>
        </div>
      </aside>

      <div className="flex flex-col">
        <header className="flex h-14 items-center border-b border-[var(--border)] bg-white px-6">
          <h1 className="text-lg font-semibold">{title}</h1>
        </header>
        <main className="flex-1 overflow-auto p-6">{children}</main>
      </div>
    </div>
    </ProjectsTreeProvider>
  )
}
