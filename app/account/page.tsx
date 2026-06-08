"use client"

import { useState } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { ShieldCheck, ShieldOff, Copy, KeyRound } from "lucide-react"
import { toast } from "sonner"
import { fetchApi } from "@/lib/api"
import type { TwoFactorStatus, TotpSetupResponse, RecoveryCodesResponse } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { useAuth } from "@/lib/auth"
import { Card, Button, Input } from "@/components/ui"

/**
 * 20.8 — Account security. Self-service TOTP two-factor enrolment: any signed-in
 * user can turn on an authenticator app, capture one-time recovery codes, and
 * later disable it (with proof). Per-user, so it lives outside the admin Settings.
 */
export default function AccountPage() {
  return (
    <AppShell title="Account & security">
      <div className="mx-auto max-w-xl space-y-4">
        <ProfileCard />
        <TwoFactorCard />
      </div>
    </AppShell>
  )
}

function ProfileCard() {
  const { user } = useAuth()
  return (
    <Card className="space-y-1 p-5">
      <h3 className="text-sm font-semibold text-slate-600">Profile</h3>
      <p className="text-sm text-slate-700">{user?.name}</p>
      <p className="text-xs text-muted">{user?.email} · {user?.role}</p>
    </Card>
  )
}

function TwoFactorCard() {
  const qc = useQueryClient()
  const { data: status, isLoading } = useQuery({
    queryKey: ["2fa-status"],
    queryFn: () => fetchApi<TwoFactorStatus>("/api/auth/2fa/status"),
  })

  // Enrolment-in-progress state, held client-side between setup → enable.
  const [setup, setSetup] = useState<TotpSetupResponse | null>(null)
  const [enrollCode, setEnrollCode] = useState("")
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const [disableCode, setDisableCode] = useState("")

  const begin = useMutation({
    mutationFn: () => fetchApi<TotpSetupResponse>("/api/auth/2fa/setup", { method: "POST" }),
    onSuccess: (d) => { setSetup(d); setRecoveryCodes(null) },
    onError: (e) => toast.error((e as Error).message),
  })

  const enable = useMutation({
    mutationFn: () => fetchApi<RecoveryCodesResponse>("/api/auth/2fa/enable", { method: "POST", body: JSON.stringify({ code: enrollCode.trim() }) }),
    onSuccess: (d) => {
      setRecoveryCodes(d.recoveryCodes)
      setSetup(null); setEnrollCode("")
      qc.invalidateQueries({ queryKey: ["2fa-status"] })
      toast.success("Two-factor authentication enabled")
    },
    onError: (e) => toast.error((e as Error).message),
  })

  const disable = useMutation({
    mutationFn: () => fetchApi("/api/auth/2fa/disable", { method: "POST", body: JSON.stringify({ code: disableCode.trim() }) }),
    onSuccess: () => {
      setDisableCode(""); setRecoveryCodes(null)
      qc.invalidateQueries({ queryKey: ["2fa-status"] })
      toast.success("Two-factor authentication disabled")
    },
    onError: (e) => toast.error((e as Error).message),
  })

  if (isLoading) return <Card className="p-5 text-sm text-muted">Loading…</Card>

  const enabled = status?.enabled ?? false

  return (
    <Card className="space-y-3 p-5">
      <div className="flex items-center gap-2">
        {enabled ? <ShieldCheck className="h-4 w-4 text-emerald-600" /> : <ShieldOff className="h-4 w-4 text-muted" />}
        <h3 className="text-sm font-semibold text-slate-600">Two-factor authentication</h3>
        {enabled && <span className="rounded bg-emerald-100 px-1.5 py-0.5 text-[10px] font-medium uppercase text-emerald-700">on</span>}
      </div>
      <p className="text-xs text-muted">
        Protect sign-in with a time-based code from an authenticator app (Google Authenticator, 1Password, Authy…).
      </p>

      {/* One-time recovery codes, shown immediately after enabling. */}
      {recoveryCodes && <RecoveryCodesPanel codes={recoveryCodes} onDone={() => setRecoveryCodes(null)} />}

      {enabled ? (
        !recoveryCodes && (
          <div className="space-y-2">
            <p className="text-xs text-slate-500">{status?.recoveryCodesRemaining ?? 0} recovery code(s) remaining.</p>
            <div className="flex items-end gap-2">
              <div className="flex-1">
                <label className="mb-1 block text-xs font-medium text-slate-600">Enter a code to turn off</label>
                <Input value={disableCode} onChange={(e) => setDisableCode(e.target.value)} placeholder="123456 or recovery code" />
              </div>
              <Button variant="outline" className="text-rose-600 hover:bg-rose-50"
                disabled={!disableCode.trim() || disable.isPending} onClick={() => disable.mutate()}>
                {disable.isPending ? "Disabling…" : "Disable"}
              </Button>
            </div>
          </div>
        )
      ) : setup ? (
        <div className="space-y-3">
          <div className="rounded-md border border-[var(--border)] bg-slate-50 p-3">
            <p className="mb-1 text-xs font-medium text-slate-600">1. Add this key to your authenticator app</p>
            <div className="flex items-center gap-2">
              <code className="break-all rounded bg-white px-2 py-1 font-mono text-sm text-slate-700">{setup.secret}</code>
              <button title="Copy" onClick={() => navigator.clipboard.writeText(setup.secret).then(() => toast.success("Key copied"))}
                className="rounded p-1 text-muted hover:bg-slate-100 hover:text-[var(--brand)]"><Copy className="h-3.5 w-3.5" /></button>
            </div>
            <p className="mt-1 break-all text-[11px] text-muted">{setup.otpauthUri}</p>
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600">2. Enter the 6-digit code it shows</label>
            <div className="flex items-end gap-2">
              <Input value={enrollCode} onChange={(e) => setEnrollCode(e.target.value)} placeholder="123456" inputMode="numeric" className="flex-1" />
              <Button disabled={enrollCode.trim().length < 6 || enable.isPending} onClick={() => enable.mutate()}>
                {enable.isPending ? "Verifying…" : "Enable"}
              </Button>
              <Button variant="ghost" onClick={() => { setSetup(null); setEnrollCode("") }}>Cancel</Button>
            </div>
          </div>
        </div>
      ) : (
        <Button disabled={begin.isPending} onClick={() => begin.mutate()}>
          <KeyRound className="h-4 w-4" /> {begin.isPending ? "Starting…" : "Enable two-factor"}
        </Button>
      )}
    </Card>
  )
}

function RecoveryCodesPanel({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  return (
    <div className="space-y-2 rounded-md border border-amber-200 bg-warning-soft p-3">
      <p className="text-xs font-semibold text-amber-800">Save your recovery codes</p>
      <p className="text-[11px] text-warning">
        Each can be used once if you lose your device. They won&apos;t be shown again.
      </p>
      <div className="grid grid-cols-2 gap-1 font-mono text-sm text-slate-700">
        {codes.map((c) => <span key={c} className="rounded bg-white px-2 py-1">{c}</span>)}
      </div>
      <div className="flex gap-2">
        <Button variant="outline" className="h-7 text-xs"
          onClick={() => navigator.clipboard.writeText(codes.join("\n")).then(() => toast.success("Recovery codes copied"))}>
          <Copy className="h-3.5 w-3.5" /> Copy all
        </Button>
        <Button variant="ghost" className="h-7 text-xs" onClick={onDone}>I&apos;ve saved them</Button>
      </div>
    </div>
  )
}
