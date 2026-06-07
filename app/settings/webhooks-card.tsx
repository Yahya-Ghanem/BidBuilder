"use client"

import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Webhook, Send, Trash2, Plus, Copy, Power } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { WebhookSubscription, WebhookCreated } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"
import { Field } from "@/components/form"
import { useT } from "@/lib/i18n"

/**
 * 20.9 — Manage outbound webhooks. Subscribe a URL to bid-lifecycle events; the
 * API POSTs a signed JSON envelope on each event. The signing secret is shown
 * once at creation. "Ping" sends a test delivery and reports the HTTP status.
 */
export function WebhooksCard({ isAdmin }: { isAdmin: boolean }) {
  const t = useT()
  const qc = useQueryClient()
  const list = useQuery({ queryKey: ["webhooks"], queryFn: () => fetchApi<WebhookSubscription[]>("/api/webhooks"), enabled: isAdmin })
  const events = useQuery({ queryKey: ["webhook-events"], queryFn: () => fetchApi<string[]>("/api/webhooks/events"), enabled: isAdmin })

  const [url, setUrl] = useState("")
  const [picked, setPicked] = useState<string[]>([])
  const [newSecret, setNewSecret] = useState<string | null>(null)

  const refresh = () => qc.invalidateQueries({ queryKey: ["webhooks"] })

  const create = useMutation({
    mutationFn: () => fetchApi<WebhookCreated>("/api/webhooks", {
      method: "POST", body: JSON.stringify({ url: url.trim(), events: picked }),
    }),
    onSuccess: (w) => { setNewSecret(w.secret); setUrl(""); setPicked([]); refresh(); toast.success("Webhook created") },
    onError: (e) => toast.error((e as Error).message),
  })
  const toggle = useMutation({
    mutationFn: (w: WebhookSubscription) => fetchApi(`/api/webhooks/${w.id}`, { method: "PUT", body: JSON.stringify({ isActive: !w.isActive }) }),
    onSuccess: refresh, onError: (e) => toast.error((e as Error).message),
  })
  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/webhooks/${id}`, { method: "DELETE" }),
    onSuccess: refresh, onError: (e) => toast.error((e as Error).message),
  })
  const ping = useMutation({
    mutationFn: (id: number) => fetchApi<{ ok: boolean; status: string }>(`/api/webhooks/${id}/ping`, { method: "POST" }),
    onSuccess: (r) => { refresh(); if (r.ok) toast.success(`Ping delivered (${r.status})`); else toast.error(`Ping failed (${r.status})`) },
    onError: (e) => toast.error((e as Error).message),
  })

  if (!isAdmin) return null

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600">
          <Webhook className="h-4 w-4 text-[var(--brand)]" /> {t("adm.webhooks.heading")}
        </h3>
        <p className="text-xs text-slate-400">
          POST a signed JSON payload to your systems when bid events occur. Verify the
          <code className="mx-1 rounded bg-slate-100 px-1">X-BidBuilder-Signature</code> header (HMAC-SHA256 of the body, keyed by your secret).
        </p>
      </div>

      {/* One-time secret reveal after create. */}
      {newSecret && (
        <div className="rounded-md border border-amber-300 bg-amber-50 p-3 text-xs">
          <div className="mb-1 font-semibold text-amber-800">Signing secret — copy it now, it won&apos;t be shown again.</div>
          <div className="flex items-center gap-2">
            <code className="flex-1 truncate rounded bg-white px-2 py-1 text-slate-700">{newSecret}</code>
            <button onClick={() => { navigator.clipboard?.writeText(newSecret); toast.success("Secret copied") }}
              className="rounded p-1 text-slate-500 hover:bg-amber-100"><Copy className="h-4 w-4" /></button>
            <button onClick={() => setNewSecret(null)} className="text-amber-700 hover:underline">Dismiss</button>
          </div>
        </div>
      )}

      {/* Existing subscriptions. */}
      {list.isLoading ? (
        <p className="text-sm text-slate-400">Loading…</p>
      ) : !list.data?.length ? (
        <p className="text-sm text-slate-400">No webhooks yet.</p>
      ) : (
        <ul className="space-y-2">
          {list.data.map((w) => (
            <li key={w.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-[var(--border)] px-3 py-2">
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span className={`h-1.5 w-1.5 rounded-full ${w.isActive ? "bg-emerald-500" : "bg-slate-300"}`} />
                  <span className="truncate text-sm text-slate-700">{w.url}</span>
                </div>
                <div className="mt-0.5 flex flex-wrap items-center gap-2 text-xs text-slate-400">
                  <span>{w.events === "*" ? "all events" : w.events}</span>
                  {w.lastStatus && (
                    <span className={w.failureCount > 0 ? "text-rose-600" : "text-emerald-600"}>
                      last: {w.lastStatus}{w.failureCount > 0 ? ` · ${w.failureCount} fails` : ""}
                    </span>
                  )}
                </div>
              </div>
              <div className="flex gap-1">
                <Button variant="outline" className="h-7 text-xs" disabled={ping.isPending} onClick={() => ping.mutate(w.id)}><Send className="h-3.5 w-3.5" /> Ping</Button>
                <Button variant="outline" className="h-7 text-xs" disabled={toggle.isPending} onClick={() => toggle.mutate(w)}><Power className="h-3.5 w-3.5" /> {w.isActive ? "Disable" : "Enable"}</Button>
                <Button variant="outline" className="h-7 text-xs text-rose-600 hover:bg-rose-50" disabled={del.isPending}
                  onClick={() => { if (confirm("Delete this webhook?")) del.mutate(w.id) }}><Trash2 className="h-3.5 w-3.5" /></Button>
              </div>
            </li>
          ))}
        </ul>
      )}

      {/* Add new. */}
      <div className="space-y-2 border-t border-[var(--border)] pt-3">
        <Field label="Endpoint URL">
          <Input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://example.com/bidbuilder-hook" />
        </Field>
        <div>
          <span className="text-xs text-slate-500">Events <span className="text-slate-400">(none = all)</span></span>
          <div className="mt-1 flex flex-wrap gap-2">
            {(events.data ?? []).map((ev) => {
              const on = picked.includes(ev)
              return (
                <button key={ev} type="button"
                  onClick={() => setPicked((p) => on ? p.filter((x) => x !== ev) : [...p, ev])}
                  className={`rounded-full border px-3 py-1 text-xs ${on ? "border-[var(--brand)] bg-[var(--brand)]/10 text-slate-800" : "border-[var(--border)] text-slate-600 hover:border-slate-400"}`}>
                  {ev}
                </button>
              )
            })}
          </div>
        </div>
        <Button disabled={!url.trim() || create.isPending} onClick={() => create.mutate()}>
          <Plus className="h-4 w-4" /> {create.isPending ? "Adding…" : "Add webhook"}
        </Button>
      </div>
    </Card>
  )
}
