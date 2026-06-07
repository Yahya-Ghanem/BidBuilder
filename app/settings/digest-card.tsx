"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Mail, Eye, Send } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { DigestPreference, DigestPreviewDto } from "@/lib/types"
import { Card, Button } from "@/components/ui"
import { Field, Modal } from "@/components/form"
import { useT } from "@/lib/i18n"

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
  const t = useT()
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
    onSuccess: (d) => { qc.setQueryData(["digest-preferences"], d); toast.success(t("adm.dig.saved")) },
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
      if (!r.configured) toast.error(t("adm.email.notConfiguredToast"))
      else toast.success(t("adm.dig.queued", { n: r.sent, plural: r.sent === 1 ? "" : "s" }))
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
      if (!r.configured) toast.error(t("adm.email.notConfiguredToast"))
      else if (!r.sent) toast.error(t("adm.dig.testFailed"))
      else toast.success(t("adm.dig.testSent"))
    } catch (e) { toast.error((e as Error).message) } finally { setTesting(false) }
  }

  const freq = data?.frequency ?? "Off"
  const day  = data?.dayOfWeek ?? "Monday"
  const configured = data?.emailConfigured ?? false
  const optedIn = freq !== "Off"

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">{t("adm.dig.heading")}</h3>
        <p className="text-xs text-slate-400">{t("adm.dig.sub")}</p>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <Field label={t("adm.dig.freq")}>
          <select
            value={freq}
            disabled={busy}
            onChange={(e) => persist({ frequency: e.target.value })}
            className="h-9 rounded-md border border-[var(--border)] bg-white px-2 text-sm"
          >
            <option value="Off">{t("adm.dig.off")}</option>
            <option value="Daily">{t("adm.dig.daily")}</option>
            <option value="Weekly">{t("adm.dig.weekly")}</option>
          </select>
        </Field>
        {freq === "Weekly" && (
          <Field label={t("adm.dig.dow")}>
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
          <span className="pb-2 text-xs text-slate-400">{t("adm.dig.lastSent", { when: data.lastSentAt.replace("T", " ").slice(0, 16) })}</span>
        )}
      </div>

      <p className="text-xs">
        {configured
          ? <span className="text-emerald-600">{t("adm.email.configured")}</span>
          : <span className="text-amber-600">{t("adm.email.notConfigured")}</span>}
      </p>

      {/* 23.1 — Per-user preview + test buttons (available regardless of opt-in: a user can
          preview what they'd get if they opted in). */}
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="outline" className="h-8 text-xs" disabled={previewing} onClick={runPreview}>
          <Eye className="h-4 w-4" /> {previewing ? t("adm.dig.previewing") : t("adm.dig.preview")}
        </Button>
        <Button variant="outline" className="h-8 text-xs" disabled={testing || !configured} onClick={sendTest}>
          <Send className="h-4 w-4" /> {testing ? t("adm.email.sending") : t("adm.dig.test")}
        </Button>
        {!optedIn && (
          <span className="text-xs text-slate-400">{t("adm.dig.optedOut")}</span>
        )}
      </div>

      {isAdmin && (
        <div className="flex items-center gap-3 border-t border-[var(--border)] pt-3">
          <Button variant="outline" className="h-8 text-xs" disabled={sending || !configured} onClick={sendNow}>
            <Mail className="h-4 w-4" /> {sending ? t("adm.email.sending") : t("adm.dig.sendNow")}
          </Button>
          <span className="text-xs text-slate-400">{t("adm.dig.sendNowSub")}</span>
        </div>
      )}

      {preview && (
        <Modal open={!!preview} onClose={() => setPreview(null)} title={t("adm.dig.previewTitle")}>
          <div className="space-y-3 text-sm">
            <div className="grid grid-cols-[auto,1fr] gap-x-3 gap-y-1 text-xs text-slate-500">
              <span>{t("adm.dig.previewFreq")}</span><span className="text-slate-700">{preview.frequency}</span>
              <span>{t("adm.dig.previewItems")}</span><span className="text-slate-700">{preview.itemCount}</span>
              <span>{t("adm.dig.previewSubject")}</span><span className="text-slate-700">{preview.subject}</span>
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
