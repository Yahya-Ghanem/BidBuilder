"use client"

import { useState } from "react"
import { useParams, useSearchParams } from "next/navigation"
import { useQuery, useMutation } from "@tanstack/react-query"
import { HardHat, CheckCircle2, AlertTriangle } from "lucide-react"
import { toast } from "sonner"
import { fetchApi, ApiError } from "@/lib/api"
import type { SubcontractorPortalView } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"
import { Field, Textarea } from "@/components/form"
import { Money } from "@/components/money"

/**
 * 20.6 — Public subcontractor quote portal. NO login: the unguessable token in
 * the URL is the only credential, and the backend resolves the owning tenant
 * from it. A subcontractor reads the scope and submits a single price. Renders
 * outside the authenticated AppShell.
 */
export default function PortalPage() {
  const params = useParams<{ token: string }>()
  const token = params?.token ?? ""
  // 23.3 — forward the optional signed-URL query params (exp + sig) so the API can
  // validate the HMAC before serving the RFQ.
  const searchParams = useSearchParams()
  const exp = searchParams?.get("exp") ?? ""
  const sig = searchParams?.get("sig") ?? ""
  const qs = exp && sig ? `?exp=${encodeURIComponent(exp)}&sig=${encodeURIComponent(sig)}` : ""

  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ["portal", token, exp, sig],
    queryFn: () => fetchApi<SubcontractorPortalView>(`/api/portal/${token}${qs}`),
    retry: false,
  })

  return (
    <div className="min-h-screen bg-slate-50">
      <header className="border-b border-[var(--border)] bg-white">
        <div className="mx-auto flex max-w-2xl items-center gap-2 px-4 py-4 text-lg font-bold">
          <HardHat className="h-5 w-5 text-[var(--brand)]" />
          {data?.companyName ?? "BidBuilder"}
        </div>
      </header>

      <main className="mx-auto max-w-2xl p-4">
        {isLoading ? (
          <p className="p-8 text-center text-sm text-muted">Loading…</p>
        ) : error ? (
          <Card className="p-6 text-center">
            <AlertTriangle className="mx-auto mb-2 h-8 w-8 text-rose-500" />
            <p className="text-sm text-slate-600">{error instanceof ApiError ? error.message : "This link is not valid."}</p>
          </Card>
        ) : data ? (
          <Body view={data} token={token} qs={qs} onSubmitted={() => refetch()} />
        ) : null}
      </main>
    </div>
  )
}

function Body({ view, token, qs, onSubmitted }: { view: SubcontractorPortalView; token: string; qs: string; onSubmitted: () => void }) {
  const canSubmit = view.status === "Pending" && !view.expired

  return (
    <div className="space-y-4">
      <Card className="space-y-3 p-5">
        <div className="flex items-start justify-between gap-2">
          <div>
            <p className="text-xs uppercase tracking-wide text-muted">Request for quote</p>
            <h2 className="text-lg font-semibold text-slate-800">{view.trade}</h2>
          </div>
          <span className="text-xs text-muted">to {view.contractorName}</span>
        </div>
        <div>
          <p className="mb-1 text-xs font-medium text-slate-500">Scope</p>
          <p className="whitespace-pre-wrap text-sm text-slate-700">{view.scope}</p>
        </div>
        <div className="flex flex-wrap gap-x-6 gap-y-1 text-xs text-slate-500">
          <span>Currency: <span className="font-medium text-slate-700">{view.currency}</span></span>
          <span>Responds by: <span className="font-medium text-slate-700">{view.expiresAt.slice(0, 10)}</span></span>
        </div>
      </Card>

      {view.status === "Submitted" || view.status === "Accepted" || view.status === "Declined" ? (
        <Card className="space-y-1 p-5">
          <div className="flex items-center gap-2 text-emerald-700">
            <CheckCircle2 className="h-5 w-5" />
            <span className="text-sm font-semibold">Quote received — thank you.</span>
          </div>
          <p className="text-sm text-slate-600">
            {/* 25.1 — Was "AED 187,500.50 USD" or similar dup: `money(...)` defaulted
                the currency to AED and then `{view.currency}` was appended as a
                trailing suffix. <Money> takes the currency as a required prop. */}
            You quoted <Money className="font-semibold" value={view.quotedAmount ?? 0} currency={view.currency} />
            {view.respondentName ? <> as {view.respondentName}</> : null}.
          </p>
          {view.submissionNotes && <p className="text-sm text-slate-500">“{view.submissionNotes}”</p>}
        </Card>
      ) : view.status === "Revoked" ? (
        <Card className="p-5 text-sm text-slate-600">This request has been withdrawn by the sender.</Card>
      ) : view.expired ? (
        <Card className="flex items-center gap-2 p-5 text-sm text-rose-700">
          <AlertTriangle className="h-4 w-4" /> This request has expired and is no longer accepting quotes.
        </Card>
      ) : (
        <SubmitForm token={token} qs={qs} currency={view.currency} onSubmitted={onSubmitted} disabled={!canSubmit} />
      )}
    </div>
  )
}

function SubmitForm({ token, qs, currency, onSubmitted, disabled }: { token: string; qs: string; currency: string; onSubmitted: () => void; disabled: boolean }) {
  const [amount, setAmount] = useState("")
  const [respondentName, setRespondentName] = useState("")
  const [notes, setNotes] = useState("")

  const submit = useMutation({
    mutationFn: () => fetchApi<SubcontractorPortalView>(`/api/portal/${token}${qs}`, {
      method: "POST",
      body: JSON.stringify({ amount: Number(amount || 0), respondentName: respondentName.trim(), notes: notes.trim() || null }),
    }),
    onSuccess: () => { toast.success("Your quote has been submitted"); onSubmitted() },
    onError: (e) => toast.error((e as Error).message),
  })

  const valid = Number(amount) > 0 && respondentName.trim().length > 0

  return (
    <Card className="space-y-3 p-5">
      <p className="text-sm font-semibold text-slate-700">Submit your quote</p>
      <form onSubmit={(e) => { e.preventDefault(); if (valid) submit.mutate() }} className="grid gap-3">
        <div className="grid grid-cols-2 gap-3">
          <Field label={`Total price (${currency})`}>
            <Input type="number" step="0.01" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} required />
          </Field>
          <Field label="Your name"><Input value={respondentName} onChange={(e) => setRespondentName(e.target.value)} required /></Field>
        </div>
        <Field label="Notes (optional)">
          <Textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={3} placeholder="Inclusions, exclusions, lead time…" />
        </Field>
        <Button type="submit" disabled={disabled || !valid || submit.isPending}>
          {submit.isPending ? "Submitting…" : "Submit quote"}
        </Button>
      </form>
    </Card>
  )
}
