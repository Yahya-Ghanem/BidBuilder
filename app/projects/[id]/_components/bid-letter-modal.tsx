"use client"
import { useEffect, useState } from "react"
import { toast } from "sonner"
import { downloadFile, fetchApi } from "@/lib/api"
import { Button, Input } from "@/components/ui"
import { Field, Modal, Select, Textarea } from "@/components/form"
import { Money } from "@/components/money"
import type { TenantSettings } from "@/lib/types"
import { useT } from "@/lib/i18n"

/** 28.4 — The three bid-letter styles the backend supports. Kept in sync with
 *  ExportService.BidLetterStyles on the API; an unknown value would render as
 *  Formal server-side, but the picker only ever offers the canonical set so the
 *  UI doesn't have to know about that fallback. */
const STYLES = ["Formal", "Concise", "International"] as const
type Style = (typeof STYLES)[number]

/** Bid submission letter: a small form over the auto-filled tender cover letter PDF.
 *  Blank fields fall back to server defaults (recipient = client, signatory = you, 90 days).
 *  28.4 — Style picker (Formal / Concise / International). Defaults to the tenant
 *  default (TenantSettings.defaultBidLetterStyle), but the user can override per-letter. */
export function BidLetterModal({ estimateId, bidPrice, currency, onClose }: { estimateId: number; bidPrice: number; currency: string; onClose: () => void }) {
  const t = useT()
  const [to, setTo] = useState("")
  const [toTitle, setToTitle] = useState("")
  const [from, setFrom] = useState("")
  const [fromTitle, setFromTitle] = useState("")
  const [validity, setValidity] = useState("90")
  const [note, setNote] = useState("")
  const [style, setStyle] = useState<Style>("Formal")
  const [busy, setBusy] = useState(false)

  // Pull the tenant default once when the modal opens. We fetch directly rather
  // than reaching into React Query because the modal is rendered transiently and
  // bridging the query cache here would be more code than the one-shot GET. The
  // fetch is fire-and-forget — if it fails, the picker stays on "Formal" (the
  // safest default), which the server also falls back to.
  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const s = await fetchApi<TenantSettings>("/api/settings")
        if (cancelled) return
        const def = s.defaultBidLetterStyle as Style
        if (STYLES.includes(def)) setStyle(def)
      } catch { /* silent — Formal is fine */ }
    })()
    return () => { cancelled = true }
  }, [])

  async function submit(ev: React.FormEvent) {
    ev.preventDefault()
    setBusy(true)
    const qs = new URLSearchParams()
    if (to.trim()) qs.set("to", to.trim())
    if (toTitle.trim()) qs.set("toTitle", toTitle.trim())
    if (from.trim()) qs.set("from", from.trim())
    if (fromTitle.trim()) qs.set("fromTitle", fromTitle.trim())
    const v = parseInt(validity, 10)
    if (!Number.isNaN(v) && v > 0) qs.set("validityDays", String(v))
    if (note.trim()) qs.set("note", note.trim())
    qs.set("style", style)
    const q = qs.toString()
    try {
      await downloadFile(`/api/estimates/${estimateId}/bid-letter.pdf${q ? `?${q}` : ""}`, "BidLetter.pdf")
      onClose()
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Failed to generate the letter")
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal open onClose={onClose} title="Bid submission letter">
      <form id="bid-letter-form" onSubmit={submit} className="space-y-3">
        <p className="text-xs text-slate-500">Tender sum <b><Money value={bidPrice} currency={currency} /></b>. Leave a field blank to use the default (recipient = client, signatory = you, validity = 90 days).</p>
        {/* 28.4 — Style picker. Sits at the top because changing the style changes
            the whole letter layout (not just one field), and we want that decision
            visible before the user spends time customizing the recipient/signatory. */}
        <Field label={t("bidLetter.style")}>
          <Select
            value={style}
            onChange={(e) => setStyle(e.target.value as Style)}
            data-testid="bid-letter-style"
          >
            <option value="Formal">{t("bidLetter.style.formal")}</option>
            <option value="Concise">{t("bidLetter.style.concise")}</option>
            <option value="International">{t("bidLetter.style.international")}</option>
          </Select>
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Recipient name"><Input value={to} onChange={(e) => setTo(e.target.value)} placeholder="(client)" /></Field>
          <Field label="Recipient title"><Input value={toTitle} onChange={(e) => setToTitle(e.target.value)} placeholder="e.g. Tender Committee" /></Field>
          <Field label="Signatory name"><Input value={from} onChange={(e) => setFrom(e.target.value)} placeholder="(you)" /></Field>
          <Field label="Signatory title"><Input value={fromTitle} onChange={(e) => setFromTitle(e.target.value)} placeholder="e.g. Estimation Manager" /></Field>
          <Field label="Validity (days)"><Input type="number" value={validity} onChange={(e) => setValidity(e.target.value)} /></Field>
        </div>
        <Field label="Custom note (optional)"><Textarea rows={3} value={note} onChange={(e) => setNote(e.target.value)} placeholder="An extra paragraph to include in the letter…" /></Field>
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" disabled={busy}>{busy ? "Generating…" : "Download PDF"}</Button>
        </div>
      </form>
    </Modal>
  )
}
