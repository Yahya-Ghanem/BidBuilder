"use client"

import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Globe, Check, Trash2 } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { TenantSettings } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"

/**
 * 20.11 — Register a vanity host for this workspace (e.g. bids.acme.com). Once the
 * host's DNS points at BidBuilder and TLS is issued, the app resolves the tenant
 * automatically from the request Host — users reach their workspace without the
 * X-Tenant-Id header. Tenant admin only; the host must be globally unique.
 */
export function CustomDomainCard({ isAdmin }: { isAdmin: boolean }) {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ["settings"], queryFn: () => fetchApi<TenantSettings>("/api/settings") })
  const current = data?.customDomain ?? null
  const [draft, setDraft] = useState("")

  const save = useMutation({
    mutationFn: (domain: string | null) =>
      fetchApi<TenantSettings>("/api/settings/custom-domain", { method: "PUT", body: JSON.stringify({ domain }) }),
    onSuccess: (d) => { qc.setQueryData(["settings"], d); setDraft(""); toast.success(d.customDomain ? "Custom domain saved" : "Custom domain cleared") },
    onError: (e) => toast.error((e as Error).message),
  })

  if (!isAdmin) return null

  return (
    <Card className="space-y-3 p-5">
      <div>
        <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600">
          <Globe className="h-4 w-4 text-[var(--brand)]" /> Custom domain
        </h3>
        <p className="text-xs text-slate-400">
          Reach this workspace at your own host (e.g. <code className="rounded bg-slate-100 px-1">bids.acme.com</code>).
          Point a CNAME at BidBuilder and we&apos;ll resolve the workspace from the address — no tenant code needed.
        </p>
      </div>

      {current ? (
        <div className="flex flex-wrap items-center gap-2 rounded-md border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm">
          <Check className="h-4 w-4 text-emerald-600" />
          <span className="font-mono text-slate-700">{current}</span>
          <span className="text-xs text-slate-400">— ensure DNS + TLS are configured for this host.</span>
          <Button variant="outline" className="ml-auto h-7 text-xs text-rose-600 hover:bg-rose-50"
            disabled={save.isPending} onClick={() => { if (confirm(`Remove ${current}?`)) save.mutate(null) }}>
            <Trash2 className="h-3.5 w-3.5" /> Remove
          </Button>
        </div>
      ) : (
        <p className="text-sm text-slate-400">No custom domain set.</p>
      )}

      <div className="flex flex-wrap items-end gap-2">
        <div className="min-w-[16rem] flex-1">
          <Input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="bids.acme.com"
            onKeyDown={(e) => { if (e.key === "Enter" && draft.trim()) save.mutate(draft.trim()) }} />
        </div>
        <Button disabled={!draft.trim() || save.isPending} onClick={() => save.mutate(draft.trim())}>
          {save.isPending ? "Saving…" : current ? "Replace" : "Add domain"}
        </Button>
      </div>
    </Card>
  )
}
