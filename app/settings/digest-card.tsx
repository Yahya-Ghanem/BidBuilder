"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Mail, Eye, Send } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { DigestPreference, DigestPreviewDto } from "@/lib/types"
import { Card, Button } from "@/components/ui"
import { Field, Modal } from "@/components/form"

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"] as const

/** 22.1 — Per-user email-digest opt-in. Each user picks how often their in-app
 *  notifications are batched into an email (Off / Daily / Weekly).
 *  23.1 — Weekly now exposes a day-of-week picker, and the card adds two side-by-side
 *  actions any user can run on themselves:
 *    • Preview — fetches the rendered subject+body the next digest would carry, without
 *      sending or advancing the watermark.
 *    • Send test — emails a single one-off digest to the caller's own address.
 *  The admin "Send digests now" path (tenant-wide) is kept for sysadmins. */
export function DigestCard({ isAdmin }: { isAdmin: boolean }) {
  const qc = useQueryClient()
  const { data } = useQuery({
    queryKey: ["digest-preferences"],
    queryFn: () => fetchApi<DigestPreference>("/api/digests/preferences"),
  })
  const [busy, setBusy] = useState(false)
  const [sending, setSending] = useState(false)
  const [previewing, setPreviewing] = useState(false)
  const [testing, setTesting] = useState(false)
  const [preview, setPreview] = useState<DigestPreviewDto | null>(null)

  const save = useMutation({
    mutationFn: (input: { frequency: string; dayOfWeek: string }) =>
      fetchApi<DigestPreference>("/api/digests/preferences", { method: "PUT", body: JSON.stringify(input) }),
    onSuccess: (d) => { qc.setQueryData(["digest-preferences"], d); toast.success("Digest preference saved") },
    onError: (e) => toast.error((e as Error).message),
  })

  async function persist(next: Partial<{ frequency: string; dayOfWeek: string }>) {
    setBusy(true)
    try {
      await save.mutateAsync({
        frequency: next.frequency ?? data?.frequency ?? "Off",
        dayOfWeek: next.dayOfWeek ?? data?.dayOfWeek ?? "Monday",
      })
    } finally { setBusy(false) }
  }

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

  async function runPreview() {
    setPreviewing(true)
    try {
      const p = await fetchApi<DigestPreviewDto>("/api/digests/preview", { method: "POST", body: JSON.stringify({}) })
      setPreview(p)
    } catch (e) { toast.error((e as Error).message) } finally { setPreviewing(false) }
  }

  async function sendTest() {
    setTesting(true)
    try {
      const r = await fetchApi<{ configured: boolean; sent: boolean }>("/api/digests/test", {
        method: "POST", body: JSON.stringify({}),
      })
      if (!r.configured) toast.error("Platform email transport is not configured.")
      else if (!r.sent) toast.error("Test send failed — check the platform email logs.")
      else toast.success("Test digest sent to your email.")
    } catch (e) { toast.error((e as Error).message) } finally { setTesting(false) }
  }

  const freq = data?.frequency ?? "Off"
  const day  = data?.dayOfWeek ?? "Monday"
  const configured = data?.emailConfigured ?? false
  const optedIn = freq !== "Off"

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">Email digests</h3>
        <p className="text-xs text-slate-400">
          Batch your in-app notifications into a periodic email instead of (or as well as) seeing them
          in the bell. Choose how often you'd like the digest. Requires the platform SMTP transport.
        </p>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <Field label="Frequency">
          <select
            value={freq}
            disabled={busy}
            onChange={(e) => persist({ frequency: e.target.value })}
            className="h-9 rounded-md border border-[var(--border)] bg-white px-2 text-sm"
          >
            <option value="Off">Off</option>
            <option value="Daily">Daily</option>
            <option value="Weekly">Weekly</option>
          </select>
        </Field>
        {freq === "Weekly" && (
          <Field label="Day of week">
            <select
              value={day}
              disabled={busy}
              onChange={(e) => persist({ dayOfWeek: e.target.value })}
              className="h-9 rounded-md border border-[var(--border)] bg-white px-2 text-sm"
            >
              {DAYS.map((d) => <option key={d} value={d}>{d}</option>)}
            </select>
          </Field>
        )}
        {data?.lastSentAt && (
          <span className="pb-2 text-xs text-slate-400">Last sent {data.lastSentAt.replace("T", " ").slice(0, 16)}</span>
        )}
      </div>

      <p className="text-xs">
        {configured
          ? <span className="text-emerald-600">Platform email transport is configured.</span>
          : <span className="text-amber-600">Platform email transport is not configured — digests can't be sent until SMTP is set up.</span>}
      </p>

      {/* 23.1 — Per-user preview + test buttons (available regardless of opt-in: a user can
          preview what they'd get if they opted in). */}
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="outline" className="h-8 text-xs" disabled={previewing} onClick={runPreview}>
          <Eye className="h-4 w-4" /> {previewing ? "Loading…" : "Preview next digest"}
        </Button>
        <Button variant="outline" className="h-8 text-xs" disabled={testing || !configured} onClick={sendTest}>
          <Send className="h-4 w-4" /> {testing ? "Sending…" : "Send test to me"}
        </Button>
        {!optedIn && (
          <span className="text-xs text-slate-400">You're opted out — Preview shows what you'd get if you opted in.</span>
        )}
      </div>

      {isAdmin && (
        <div className="flex items-center gap-3 border-t border-[var(--border)] pt-3">
          <Button variant="outline" className="h-8 text-xs" disabled={sending || !configured} onClick={sendNow}>
            <Mail className="h-4 w-4" /> {sending ? "Sending…" : "Send digests now"}
          </Button>
          <span className="text-xs text-slate-400">Tenant-admin only — immediately emails every opted-in user in this workspace.</span>
        </div>
      )}

      {preview && (
        <Modal open={!!preview} onClose={() => setPreview(null)} title="Digest preview">
          <div className="space-y-3 text-sm">
            <div className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-xs text-slate-500">
              <span>Frequency</span><span className="text-slate-700">{preview.frequency}</span>
              <span>Items</span><span className="text-slate-700">{preview.itemCount}</span>
              <span>Subject</span><span className="text-slate-700">{preview.subject}</span>
            </div>
            <pre className="max-h-[60vh] overflow-auto whitespace-pre-wrap rounded bg-slate-50 p-3 text-xs leading-relaxed text-slate-700">
              {preview.body}
            </pre>
          </div>
        </Modal>
      )}
    </Card>
  )
}
