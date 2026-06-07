"use client"
import { useState } from "react"
import { toast } from "sonner"
import { downloadFile } from "@/lib/api"
import { Button, Input } from "@/components/ui"
import { Field, Modal, Textarea } from "@/components/form"
import { Money } from "@/components/money"

/** Bid submission letter: a small form over the auto-filled tender cover letter PDF.
 *  Blank fields fall back to server defaults (recipient = client, signatory = you, 90 days). */
export function BidLetterModal({ estimateId, bidPrice, currency, onClose }: { estimateId: number; bidPrice: number; currency: string; onClose: () => void }) {
  const [to, setTo] = useState("")
  const [toTitle, setToTitle] = useState("")
  const [from, setFrom] = useState("")
  const [fromTitle, setFromTitle] = useState("")
  const [validity, setValidity] = useState("90")
  const [note, setNote] = useState("")
  const [busy, setBusy] = useState(false)

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
