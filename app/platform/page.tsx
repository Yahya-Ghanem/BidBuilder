"use client"

import { useEffect, useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { ShieldCheck, Plus, LogOut, Building2 } from "lucide-react"
import { toast } from "sonner"
import { platformApi, platformSession, type PlatformTenant, type PlatformLoginResponse } from "@/lib/platform"
import { Button, Input, Card, Badge, TableScroll } from "@/components/ui"
import { Modal, Field, Select } from "@/components/form"

export default function PlatformPage() {
  // Resolve auth state after mount so server and first client render agree.
  const [ready, setReady] = useState(false)
  const [authed, setAuthed] = useState(false)
  useEffect(() => {
    setAuthed(!!platformSession.get())
    setReady(true)
  }, [])

  if (!ready) return null
  return (
    <div className="min-h-screen bg-slate-50">
      {authed ? <Console onSignOut={() => setAuthed(false)} /> : <PlatformLogin onSignedIn={() => setAuthed(true)} />}
    </div>
  )
}

// ── Login ───────────────────────────────────────────────────────────────────────
function PlatformLogin({ onSignedIn }: { onSignedIn: () => void }) {
  const [email, setEmail] = useState("")
  const [password, setPassword] = useState("")
  const [busy, setBusy] = useState(false)

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      const res = await platformApi<PlatformLoginResponse>("/api/auth/platform-login", {
        method: "POST",
        body: JSON.stringify({ email, password }),
      })
      platformSession.set(res.token)
      onSignedIn()
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Login failed")
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="grid min-h-screen place-items-center p-4">
      <Card className="w-full max-w-sm p-6">
        <div className="mb-6 flex items-center gap-2 text-xl font-bold">
          <ShieldCheck className="h-6 w-6 text-[var(--brand)]" /> Platform Console
        </div>
        <p className="mb-4 text-sm text-slate-500">Sign in as a platform administrator to manage tenants.</p>
        <form onSubmit={onSubmit} className="space-y-3">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600">Email</label>
            <Input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600">Password</label>
            <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required />
          </div>
          <Button type="submit" className="w-full" disabled={busy}>
            {busy ? "Signing in…" : "Sign in"}
          </Button>
        </form>
      </Card>
    </div>
  )
}

// ── Console ─────────────────────────────────────────────────────────────────────
function Console({ onSignOut }: { onSignOut: () => void }) {
  const qc = useQueryClient()
  const [creating, setCreating] = useState(false)

  const { data: tenants, isLoading, error } = useQuery({
    queryKey: ["platform-tenants"],
    queryFn: () => platformApi<PlatformTenant[]>("/api/platform/tenants"),
  })

  const setSuspended = useMutation({
    mutationFn: ({ id, suspend }: { id: string; suspend: boolean }) =>
      platformApi(`/api/platform/tenants/${id}/${suspend ? "suspend" : "activate"}`, { method: "POST" }),
    onSuccess: (_d, v) => {
      qc.invalidateQueries({ queryKey: ["platform-tenants"] })
      toast.success(v.suspend ? "Tenant suspended" : "Tenant re-activated")
    },
    onError: (e) => toast.error((e as Error).message),
  })

  function signOut() {
    platformSession.clear()
    qc.clear()
    onSignOut()
  }

  return (
    <div className="mx-auto max-w-5xl p-6">
      <header className="mb-6 flex items-center justify-between">
        <div className="flex items-center gap-2 text-xl font-bold">
          <ShieldCheck className="h-6 w-6 text-[var(--brand)]" /> Platform Console
        </div>
        <div className="flex items-center gap-2">
          <Button variant="primary" onClick={() => setCreating(true)}>
            <Plus className="h-4 w-4" /> New tenant
          </Button>
          <Button variant="outline" onClick={signOut}>
            <LogOut className="h-4 w-4" /> Sign out
          </Button>
        </div>
      </header>

      <Card className="p-5">
        <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-slate-600">
          <Building2 className="h-4 w-4" /> Tenants
        </div>
        {isLoading && <p className="text-slate-400">Loading…</p>}
        {error && <p className="text-rose-600">{(error as Error).message}</p>}
        {tenants && tenants.length === 0 && <p className="text-slate-400">No tenants yet.</p>}
        {tenants && tenants.length > 0 && (
          <TableScroll>
          <table className="w-full min-w-[44rem] text-sm">
            <thead>
              <tr className="text-left text-xs uppercase tracking-wide text-slate-400">
                <th className="py-2">Name</th>
                <th className="py-2">Slug</th>
                <th className="py-2">Locale</th>
                <th className="py-2 text-right">Users</th>
                <th className="py-2 text-right">Projects</th>
                <th className="py-2">Status</th>
                <th className="py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {tenants.map((t) => (
                <tr key={t.id} className="border-t border-[var(--border)]">
                  <td className="py-2 font-medium text-slate-800">{t.name}</td>
                  <td className="py-2 text-slate-500">{t.slug}</td>
                  <td className="py-2 text-slate-500">{t.defaultLocale}</td>
                  <td className="py-2 text-right tabular-nums">{t.userCount}</td>
                  <td className="py-2 text-right tabular-nums">{t.projectCount}</td>
                  <td className="py-2">
                    <Badge className={t.isSuspended ? "bg-rose-100 text-rose-700" : "bg-emerald-100 text-emerald-800"}>
                      {t.isSuspended ? "Suspended" : "Active"}
                    </Badge>
                  </td>
                  <td className="py-2 text-right">
                    <Button
                      variant="outline"
                      className="px-2.5 py-1 text-xs"
                      disabled={setSuspended.isPending}
                      onClick={() => setSuspended.mutate({ id: t.id, suspend: !t.isSuspended })}
                    >
                      {t.isSuspended ? "Re-activate" : "Suspend"}
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </TableScroll>
        )}
      </Card>

      {creating && <CreateTenantModal onClose={() => setCreating(false)} />}
    </div>
  )
}

// ── Create-tenant modal ──────────────────────────────────────────────────────────
function CreateTenantModal({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient()
  const [slug, setSlug] = useState("")
  const [name, setName] = useState("")
  const [locale, setLocale] = useState("en")
  const [adminName, setAdminName] = useState("")
  const [adminEmail, setAdminEmail] = useState("")
  const [adminPassword, setAdminPassword] = useState("")

  const create = useMutation({
    mutationFn: () =>
      platformApi<PlatformTenant>("/api/platform/tenants", {
        method: "POST",
        body: JSON.stringify({
          slug, name, defaultLocale: locale,
          adminName: adminName || undefined,
          adminEmail: adminEmail || undefined,
          adminPassword: adminPassword || undefined,
        }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["platform-tenants"] })
      toast.success("Tenant created")
      onClose()
    },
    onError: (e) => toast.error((e as Error).message),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!/^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/.test(slug))
      return toast.error("Slug must be lowercase letters, digits or hyphens.")
    if (!name.trim()) return toast.error("Name is required.")
    create.mutate()
  }

  return (
    <Modal open onClose={onClose} title="New tenant">
      <form id="create-tenant-form" onSubmit={submit} className="space-y-3">
        <Field label="Slug (workspace id, e.g. acme-build)">
          <Input value={slug} onChange={(e) => setSlug(e.target.value.toLowerCase())} placeholder="acme-build" />
        </Field>
        <Field label="Name">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="ACME Build" />
        </Field>
        <Field label="Default locale">
          <Select value={locale} onChange={(e) => setLocale(e.target.value)}>
            <option value="en">English (en)</option>
            <option value="ar">Arabic (ar)</option>
          </Select>
        </Field>

        <div className="mt-2 border-t border-[var(--border)] pt-3">
          <p className="mb-2 text-xs font-medium text-slate-500">First administrator (optional — leave blank to add later)</p>
          <div className="space-y-3">
            <Field label="Admin name"><Input value={adminName} onChange={(e) => setAdminName(e.target.value)} /></Field>
            <Field label="Admin email"><Input type="email" value={adminEmail} onChange={(e) => setAdminEmail(e.target.value)} /></Field>
            <Field label="Admin password (min 8 chars)"><Input type="password" value={adminPassword} onChange={(e) => setAdminPassword(e.target.value)} /></Field>
          </div>
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" disabled={create.isPending}>{create.isPending ? "Creating…" : "Create"}</Button>
        </div>
      </form>
    </Modal>
  )
}
