"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Mail } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { DigestPreference } from "@/lib/types"
import { Card, Button } from "@/components/ui"
import { Field } from "@/components/form"

/** 22.1 — Per-user email-digest opt-in. Each user picks how often their in-app
 *  notifications are batched into an email (Off / Daily / Weekly). A tenant admin can
 *  also trigger a digest to themselves immediately to preview it. The actual sending is
 *  done by a background scheduler; this only edits the preference + previews. */
export function DigestCard({ isAdmin }: { isAdmin: boolean }) {
  const qc = useQueryClient()
  const { data } = useQuery({
    queryKey: ["digest-preferences"],
    queryFn: () => fetchApi<DigestPreference>("/api/digests/preferences"),
  })
  const [busy, setBusy] = useState(false)
  const [sending, setSending] = useState(false)

  const save = useMutation({
    mutationFn: (frequency: string) =>
      fetchApi<DigestPreference>("/api/digests/preferences", { method: "PUT", body: JSON.stringify({ frequency }) }),
    onSuccess: (d) => { qc.setQueryData(["digest-preferences"], d); toast.success("Digest preference saved") },
    onError: (e) => toast.error((e as Error).message),
  })

  async function sendNow() {
    setSending(true)
    try {
      // No userId → the API runs the tenant-wide path: send a digest now to every
      // opted-in user in this workspace.
      const r = await fetchApi<{ configured: boolean; sent: number }>("/api/digests/send-now", {
        method: "POST",
        body: JSON.stringify({}),
      })
      if (!r.configured) toast.error("Platform email transport is not configured.")
      else toast.success(`Queued ${r.sent} digest${r.sent === 1 ? "" : "s"}.`)
    } catch (e) { toast.error((e as Error).message) } finally { setSending(false) }
  }

  const freq = data?.frequency ?? "Off"
  const configured = data?.emailConfigured ?? false

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">Email digests</h3>
        <p className="text-xs text-slate-400">
          Batch your in-app notifications into a periodic email instead of (or as well as) seeing them
          in the bell. Choose how often you'd like the digest. Requires the platform SMTP transport.
        </p>
      </div>

      <div className="flex items-end gap-3">
        <Field label="Frequency">
          <select
            value={freq}
            disabled={busy}
            onChange={async (e) => { setBusy(true); try { await save.mutateAsync(e.target.value) } finally { setBusy(false) } }}
            className="h-9 rounded-md border border-[var(--border)] bg-white px-2 text-sm"
          >
            <option value="Off">Off</option>
            <option value="Daily">Daily</option>
            <option value="Weekly">Weekly</option>
          </select>
        </Field>
        {data?.lastSentAt && (
          <span className="pb-2 text-xs text-slate-400">Last sent {data.lastSentAt.replace("T", " ").slice(0, 16)}</span>
        )}
      </div>

      <p className="text-xs">
        {configured
          ? <span className="text-emerald-600">Platform email transport is configured.</span>
          : <span className="text-amber-600">Platform email transport is not configured — digests can't be sent until SMTP is set up.</span>}
      </p>

      {isAdmin && (
        <div className="flex items-center gap-3">
          <Button variant="outline" className="h-8 text-xs" disabled={sending || !configured} onClick={sendNow}>
            <Mail className="h-4 w-4" /> {sending ? "Sending…" : "Send digests now"}
          </Button>
          <span className="text-xs text-slate-400">Immediately emails every opted-in user in this workspace.</span>
        </div>
      )}
    </Card>
  )
}
