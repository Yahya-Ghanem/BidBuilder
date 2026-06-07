"use client"

import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { KeyRound, Trash2, Plus, Copy } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { ApiKey, ApiKeyCreated } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"
import { Field } from "@/components/form"

/**
 * 21.2 — Manage programmatic API keys. A key authenticates a headless caller via the
 * `X-Api-Key` header, acting as the admin who created it. The secret is shown exactly
 * once at creation; only its hash is stored.
 */
export function ApiKeysCard({ isAdmin }: { isAdmin: boolean }) {
  const qc = useQueryClient()
  const list = useQuery({ queryKey: ["api-keys"], queryFn: () => fetchApi<ApiKey[]>("/api/admin/api-keys"), enabled: isAdmin })

  const [name, setName] = useState("")
  const [days, setDays] = useState("")
  const [newSecret, setNewSecret] = useState<string | null>(null)

  const refresh = () => qc.invalidateQueries({ queryKey: ["api-keys"] })

  const create = useMutation({
    mutationFn: () => fetchApi<ApiKeyCreated>("/api/admin/api-keys", {
      method: "POST",
      body: JSON.stringify({ name: name.trim(), expiresInDays: days.trim() ? Number(days) : null }),
    }),
    onSuccess: (k) => { setNewSecret(k.secret); setName(""); setDays(""); refresh(); toast.success("API key created") },
    onError: (e) => toast.error((e as Error).message),
  })
  const revoke = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/admin/api-keys/${id}`, { method: "DELETE" }),
    onSuccess: () => { refresh(); toast.success("API key revoked") },
    onError: (e) => toast.error((e as Error).message),
  })

  if (!isAdmin) return null

  const fmt = (s: string | null) => (s ? s.replace("T", " ").slice(0, 16) : "—")

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600">
          <KeyRound className="h-4 w-4 text-[var(--brand)]" /> API keys
        </h3>
        <p className="text-xs text-slate-400">
          Call the API from your own systems. Send the key in an
          <code className="mx-1 rounded bg-slate-100 px-1">X-Api-Key</code> header. A key acts as you
          (inherits your permissions) and stops working if your account is deactivated.
        </p>
      </div>

      {/* One-time secret reveal after create. */}
      {newSecret && (
        <div className="rounded-md border border-amber-300 bg-amber-50 p-3 text-xs">
          <div className="mb-1 font-semibold text-amber-800">API key — copy it now, it won&apos;t be shown again.</div>
          <div className="flex items-center gap-2">
            <code className="flex-1 truncate rounded bg-white px-2 py-1 text-slate-700">{newSecret}</code>
            <button onClick={() => { navigator.clipboard?.writeText(newSecret); toast.success("Key copied") }}
              className="rounded p-1 text-slate-500 hover:bg-amber-100"><Copy className="h-4 w-4" /></button>
            <button onClick={() => setNewSecret(null)} className="text-amber-700 hover:underline">Dismiss</button>
          </div>
        </div>
      )}

      {/* Existing keys. */}
      {list.isLoading ? (
        <p className="text-sm text-slate-400">Loading…</p>
      ) : !list.data?.length ? (
        <p className="text-sm text-slate-400">No API keys yet.</p>
      ) : (
        <ul className="space-y-2">
          {list.data.map((k) => (
            <li key={k.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-[var(--border)] px-3 py-2">
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span className="truncate text-sm text-slate-700">{k.name}</span>
                  <code className="rounded bg-slate-100 px-1 text-xs text-slate-500">{k.prefix}…</code>
                  {k.revoked && <span className="rounded bg-rose-100 px-1.5 text-xs text-rose-700">revoked</span>}
                  {!k.revoked && k.expiresAt && new Date(k.expiresAt) < new Date() && (
                    <span className="rounded bg-amber-100 px-1.5 text-xs text-amber-700">expired</span>
                  )}
                </div>
                <div className="mt-0.5 flex flex-wrap items-center gap-2 text-xs text-slate-400">
                  <span>created {fmt(k.createdAt)}</span>
                  <span>· last used {fmt(k.lastUsedAt)}</span>
                  {k.expiresAt && <span>· expires {fmt(k.expiresAt)}</span>}
                </div>
              </div>
              {!k.revoked && (
                <Button variant="outline" className="h-7 text-xs text-rose-600 hover:bg-rose-50" disabled={revoke.isPending}
                  onClick={() => { if (confirm(`Revoke "${k.name}"? Any system using it will stop working.`)) revoke.mutate(k.id) }}>
                  <Trash2 className="h-3.5 w-3.5" /> Revoke
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}

      {/* Mint a new key. */}
      <div className="flex flex-wrap items-end gap-2 border-t border-[var(--border)] pt-3">
        <Field label="Name"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="CI pipeline" className="w-48" /></Field>
        <Field label="Expires in days (optional)"><Input type="number" min={1} value={days} onChange={(e) => setDays(e.target.value)} placeholder="never" className="w-40" /></Field>
        <Button disabled={!name.trim() || create.isPending} onClick={() => create.mutate()}>
          <Plus className="h-4 w-4" /> {create.isPending ? "Creating…" : "Create key"}
        </Button>
      </div>
    </Card>
  )
}
