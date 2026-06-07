"use client"

import { useState, useEffect, useRef } from "react"
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { ImageIcon, Trash2, Upload, Plus, Check, Mail } from "lucide-react"
import { fetchApi, uploadFile, fetchObjectUrl } from "@/lib/api"
import type { TenantSettings, CurrencyRate, CurrencyRates, CostComponentType, ActivityType, ProjectType } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Button, Input } from "@/components/ui"
import { Field } from "@/components/form"
import { WebhooksCard } from "./webhooks-card"
import { ApiKeysCard } from "./api-keys-card"
import { CustomDomainCard } from "./custom-domain-card"
import { SsoCard } from "./sso-card"

export default function SettingsPage() {
  return (
    <AppShell title="Settings">
      <SettingsForm />
    </AppShell>
  )
}

function SettingsForm() {
  const qc = useQueryClient()
  const { isAdmin } = usePermissions()
  const { data, isLoading, error } = useQuery({ queryKey: ["settings"], queryFn: () => fetchApi<TenantSettings>("/api/settings") })
  const [f, setF] = useState<TenantSettings | null>(null)
  useEffect(() => { if (data) setF(data) }, [data])

  const save = useMutation({
    mutationFn: (v: TenantSettings) => fetchApi<TenantSettings>("/api/settings", { method: "PUT", body: JSON.stringify(v) }),
    onSuccess: (d) => { qc.setQueryData(["settings"], d); setF(d); toast.success("Settings saved") },
    onError: (e) => toast.error((e as Error).message),
  })

  if (isLoading || !f) return <p className="text-slate-400">Loading…</p>
  if (error) return <p className="text-rose-600">{(error as Error).message}</p>

  const ro = !isAdmin
  const set = (k: keyof TenantSettings) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value })
  const setNum = (k: keyof TenantSettings) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: Number(e.target.value || 0) })

  return (
    <div className="max-w-2xl space-y-6">
      <Card className="space-y-4 p-5">
        <div>
          <h3 className="text-sm font-semibold text-slate-600">Company profile</h3>
          <p className="text-xs text-slate-400">Appears on the header of exported bid documents.</p>
        </div>
        <Field label="Company name"><Input value={f.companyName} disabled /></Field>
        <Field label="Address"><Input value={f.address ?? ""} onChange={set("address")} disabled={ro} /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="City"><Input value={f.city ?? ""} onChange={set("city")} disabled={ro} /></Field>
          <Field label="Country (ISO-2)"><Input value={f.country ?? ""} onChange={set("country")} disabled={ro} maxLength={2} /></Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Phone"><Input value={f.phone ?? ""} onChange={set("phone")} disabled={ro} /></Field>
          <Field label="Email"><Input value={f.contactEmail ?? ""} onChange={set("contactEmail")} disabled={ro} /></Field>
          <Field label="Website"><Input value={f.website ?? ""} onChange={set("website")} disabled={ro} /></Field>
        </div>
      </Card>

      <LogoCard hasLogo={f.hasLogo} isAdmin={isAdmin} />

      <Card className="space-y-4 p-5">
        <h3 className="text-sm font-semibold text-slate-600">Estimating defaults</h3>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Base currency"><Input value={f.baseCurrency} onChange={set("baseCurrency")} disabled={ro} maxLength={3} /></Field>
          <Field label="Timezone"><Input value={f.timezone} onChange={set("timezone")} disabled={ro} /></Field>
        </div>
        <div className="grid grid-cols-4 gap-3">
          <Field label="Overhead %"><Input type="number" step="0.01" min={0} value={f.defaultOverheadPct} onChange={setNum("defaultOverheadPct")} disabled={ro} /></Field>
          <Field label="Profit %"><Input type="number" step="0.01" min={0} value={f.defaultProfitPct} onChange={setNum("defaultProfitPct")} disabled={ro} /></Field>
          <Field label="Contingency %"><Input type="number" step="0.01" min={0} value={f.defaultContingencyPct} onChange={setNum("defaultContingencyPct")} disabled={ro} /></Field>
          <Field label="VAT / tax %"><Input type="number" step="0.01" min={0} max={100} value={f.defaultTaxRatePct} onChange={setNum("defaultTaxRatePct")} disabled={ro} /></Field>
        </div>
      </Card>

      {/* 20.2 — Approval workflow. Required-approvals=0 keeps the legacy
          "anyone with permission can publish" behavior; any positive value
          enforces N sign-offs before Draft→Published. */}
      <Card className="space-y-4 p-5">
        <div>
          <h3 className="text-sm font-semibold text-slate-600">Approval workflow</h3>
          <p className="text-xs text-slate-400">
            Number of TenantAdmin sign-offs required before an estimate can be Published.
            Zero disables the workflow. Any BOQ / preliminaries / markups / risks edit invalidates existing sign-offs.
          </p>
        </div>
        <div className="grid grid-cols-4 gap-3">
          <Field label="Required approvals"><Input type="number" min={0} value={f.requiredApprovalsToPublish} onChange={setNum("requiredApprovalsToPublish")} disabled={ro} /></Field>
        </div>
      </Card>

      {/* 21.1 — Email notifications. The toggle persists with "Save settings"; the
          test button verifies the platform SMTP transport end-to-end. */}
      <EmailCard f={f} setF={setF} ro={ro} isAdmin={isAdmin} />

      {isAdmin
        ? <Button disabled={save.isPending} onClick={() => save.mutate(f)}>{save.isPending ? "Saving…" : "Save settings"}</Button>
        : <p className="text-xs text-slate-400">Only a tenant admin can edit settings.</p>}

      <CurrencyRatesCard isAdmin={isAdmin} />
      <CostTypesCard isAdmin={isAdmin} />
      <ActivitiesCard isAdmin={isAdmin} />
      <ProjectTypesCard isAdmin={isAdmin} />
      <CustomDomainCard isAdmin={isAdmin} />
      <SsoCard isAdmin={isAdmin} />
      <WebhooksCard isAdmin={isAdmin} />
      <ApiKeysCard isAdmin={isAdmin} />
    </div>
  )
}

/** 21.1 — Email-notification toggle + a "send test email" action. The toggle edits
 *  the shared settings state so it saves with the page's "Save settings" button; the
 *  test send is independent and only enabled when the platform transport is configured. */
function EmailCard({ f, setF, ro, isAdmin }: { f: TenantSettings; setF: (s: TenantSettings) => void; ro: boolean; isAdmin: boolean }) {
  const [testing, setTesting] = useState(false)

  async function sendTest() {
    setTesting(true)
    try {
      const r = await fetchApi<{ configured: boolean; sent: boolean }>("/api/settings/email/test", { method: "POST" })
      if (!r.configured) toast.error("Platform email transport is not configured.")
      else if (r.sent) toast.success("Test email sent — check your inbox.")
      else toast.error("Email is configured but the test send failed. Check the server logs.")
    } catch (e) { toast.error((e as Error).message) } finally { setTesting(false) }
  }

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">Email notifications</h3>
        <p className="text-xs text-slate-400">
          Email a copy of in-app notifications (estimate publish / approvals / subcontractor quotes) to your
          workspace's users, and email subcontractor RFQ invites. Requires the platform SMTP transport to be configured.
        </p>
      </div>

      <label className="flex items-center gap-2 text-sm text-slate-700">
        <input
          type="checkbox"
          checked={f.notificationEmailsEnabled}
          disabled={ro}
          onChange={(e) => setF({ ...f, notificationEmailsEnabled: e.target.checked })}
        />
        Email notification copies to this workspace's users
      </label>

      <p className="text-xs">
        {f.emailConfigured
          ? <span className="text-emerald-600">Platform email transport is configured.</span>
          : <span className="text-amber-600">Platform email transport is not configured — set the SMTP environment variables to enable sending.</span>}
      </p>

      {isAdmin && (
        <div className="flex items-center gap-3">
          <Button variant="outline" className="h-8 text-xs" disabled={testing || !f.emailConfigured} onClick={sendTest}>
            <Mail className="h-4 w-4" /> {testing ? "Sending…" : "Send test email"}
          </Button>
          <span className="text-xs text-slate-400">The toggle is saved with “Save settings”.</span>
        </div>
      )}
    </Card>
  )
}

/** Catalog of project types (Civil, Mechanical…) offered when creating a project. */
function ProjectTypesCard({ isAdmin }: { isAdmin: boolean }) {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ["project-types"], queryFn: () => fetchApi<ProjectType[]>("/api/project-types") })
  const [name, setName] = useState("")
  const [busy, setBusy] = useState(false)

  async function add() {
    if (!name.trim()) { toast.error("Name is required."); return }
    setBusy(true)
    try {
      await fetchApi("/api/project-types", { method: "POST", body: JSON.stringify({ name: name.trim(), sortOrder: data?.length ?? 0, isActive: true }) })
      await qc.invalidateQueries({ queryKey: ["project-types"] })
      setName(""); toast.success("Project type added")
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">Project types</h3>
        <p className="text-xs text-slate-400">
          The dropdown of project types (Civil, Mechanical, Electrical…) shown when creating or editing a
          project. Built-in types can be deactivated but not deleted; add your own below.
        </p>
      </div>

      {data && (
        <table className="w-full text-sm">
          <thead className="text-left text-xs text-slate-500"><tr><th className="py-1">Name</th><th className="py-1">Active</th><th /></tr></thead>
          <tbody>{data.map((a) => <ProjectTypeRow key={a.id} type={a} isAdmin={isAdmin} busy={busy} setBusy={setBusy} />)}</tbody>
        </table>
      )}

      {isAdmin && (
        <div className="flex items-end gap-2 border-t border-[var(--border)] pt-3">
          <Field label="New project type"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Marine works" className="w-48" /></Field>
          <Button variant="outline" className="h-9" disabled={busy} onClick={add}><Plus className="h-4 w-4" /> Add</Button>
        </div>
      )}
    </Card>
  )
}

function ProjectTypeRow({ type, isAdmin, busy, setBusy }: { type: ProjectType; isAdmin: boolean; busy: boolean; setBusy: (b: boolean) => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(type.name)
  const [active, setActive] = useState(type.isActive)
  useEffect(() => { setName(type.name); setActive(type.isActive) }, [type.name, type.isActive])
  const dirty = name !== type.name || active !== type.isActive

  async function save() {
    setBusy(true)
    try {
      await fetchApi(`/api/project-types/${type.id}`, { method: "PUT", body: JSON.stringify({ name, sortOrder: type.sortOrder, isActive: active }) })
      await qc.invalidateQueries({ queryKey: ["project-types"] }); toast.success(`${type.name} saved`)
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }
  async function remove() {
    setBusy(true)
    try {
      await fetchApi(`/api/project-types/${type.id}`, { method: "DELETE" })
      await qc.invalidateQueries({ queryKey: ["project-types"] }); toast.success(`${type.name} removed`)
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <tr className="border-t border-[var(--border)]">
      <td className="py-2">
        {isAdmin && !type.builtin ? <Input value={name} onChange={(e) => setName(e.target.value)} className="w-48" /> : name}
        {type.builtin && <span className="ml-1 text-xs text-slate-400">built-in</span>}
      </td>
      <td className="py-2"><input type="checkbox" checked={active} disabled={!isAdmin} onChange={(e) => setActive(e.target.checked)} /></td>
      <td className="py-2 text-right">
        {isAdmin && (
          <div className="flex justify-end gap-1">
            <Button variant="outline" className="h-7 px-2 text-xs" disabled={busy || !dirty} onClick={save}><Check className="h-3.5 w-3.5" /></Button>
            {!type.builtin && <Button variant="outline" className="h-7 px-2 text-xs text-rose-600" disabled={busy} onClick={remove}><Trash2 className="h-3.5 w-3.5" /></Button>}
          </div>
        )}
      </td>
    </tr>
  )
}

/** Catalog of construction activities offered in the unit "add activity" dropdown. */
function ActivitiesCard({ isAdmin }: { isAdmin: boolean }) {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ["activities"], queryFn: () => fetchApi<ActivityType[]>("/api/activities") })
  const [name, setName] = useState("")
  const [busy, setBusy] = useState(false)

  async function add() {
    if (!name.trim()) { toast.error("Name is required."); return }
    setBusy(true)
    try {
      await fetchApi("/api/activities", { method: "POST", body: JSON.stringify({ name: name.trim(), sortOrder: data?.length ?? 0, isActive: true }) })
      await qc.invalidateQueries({ queryKey: ["activities"] })
      setName(""); toast.success("Activity added")
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">Activities</h3>
        <p className="text-xs text-slate-400">
          The dropdown of construction activities shown when adding work under a unit. Built-in activities
          can be deactivated but not deleted; add your own below.
        </p>
      </div>

      {data && (
        <table className="w-full text-sm">
          <thead className="text-left text-xs text-slate-500"><tr><th className="py-1">Name</th><th className="py-1">Active</th><th /></tr></thead>
          <tbody>{data.map((a) => <ActivityRow key={a.id} activity={a} isAdmin={isAdmin} busy={busy} setBusy={setBusy} />)}</tbody>
        </table>
      )}

      {isAdmin && (
        <div className="flex items-end gap-2 border-t border-[var(--border)] pt-3">
          <Field label="New activity"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Roofing" className="w-48" /></Field>
          <Button variant="outline" className="h-9" disabled={busy} onClick={add}><Plus className="h-4 w-4" /> Add</Button>
        </div>
      )}
    </Card>
  )
}

function ActivityRow({ activity, isAdmin, busy, setBusy }: { activity: ActivityType; isAdmin: boolean; busy: boolean; setBusy: (b: boolean) => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(activity.name)
  const [active, setActive] = useState(activity.isActive)
  useEffect(() => { setName(activity.name); setActive(activity.isActive) }, [activity.name, activity.isActive])
  const dirty = name !== activity.name || active !== activity.isActive

  async function save() {
    setBusy(true)
    try {
      await fetchApi(`/api/activities/${activity.id}`, { method: "PUT", body: JSON.stringify({ name, sortOrder: activity.sortOrder, isActive: active }) })
      await qc.invalidateQueries({ queryKey: ["activities"] }); toast.success(`${activity.name} saved`)
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }
  async function remove() {
    setBusy(true)
    try {
      await fetchApi(`/api/activities/${activity.id}`, { method: "DELETE" })
      await qc.invalidateQueries({ queryKey: ["activities"] }); toast.success(`${activity.name} removed`)
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <tr className="border-t border-[var(--border)]">
      <td className="py-2">
        {isAdmin && !activity.builtin ? <Input value={name} onChange={(e) => setName(e.target.value)} className="w-48" /> : name}
        {activity.builtin && <span className="ml-1 text-xs text-slate-400">built-in</span>}
      </td>
      <td className="py-2"><input type="checkbox" checked={active} disabled={!isAdmin} onChange={(e) => setActive(e.target.checked)} /></td>
      <td className="py-2 text-right">
        {isAdmin && (
          <div className="flex justify-end gap-1">
            <Button variant="outline" className="h-7 px-2 text-xs" disabled={busy || !dirty} onClick={save}><Check className="h-3.5 w-3.5" /></Button>
            {!activity.builtin && <Button variant="outline" className="h-7 px-2 text-xs text-rose-600" disabled={busy} onClick={remove}><Trash2 className="h-3.5 w-3.5" /></Button>}
          </div>
        )}
      </td>
    </tr>
  )
}

/** Catalog of cost-component types used to build up BOQ item rates. */
function CostTypesCard({ isAdmin }: { isAdmin: boolean }) {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ["cost-types"], queryFn: () => fetchApi<CostComponentType[]>("/api/cost-components") })
  const [name, setName] = useState("")
  const [code, setCode] = useState("")
  const [kind, setKind] = useState("Amount")
  const [busy, setBusy] = useState(false)

  async function add() {
    const c = code.trim().toUpperCase()
    if (!name.trim()) { toast.error("Name is required."); return }
    if (c.length < 1) { toast.error("Code is required."); return }
    setBusy(true)
    try {
      await fetchApi("/api/cost-components", { method: "POST", body: JSON.stringify({ code: c, name: name.trim(), calcKind: kind, sortOrder: (data?.length ?? 0), isActive: true }) })
      await qc.invalidateQueries({ queryKey: ["cost-types"] })
      setName(""); setCode(""); setKind("Amount")
      toast.success("Cost type added")
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">Cost-component types</h3>
        <p className="text-xs text-slate-400">
          Categories used to build up each BOQ item's unit rate. <b>Amount</b> types are money per unit;
          <b> Percent</b> types apply to the amount subtotal (e.g. Waste, Overheads). Built-in types can be deactivated but not deleted.
        </p>
      </div>

      {data && (
        <table className="w-full text-sm">
          <thead className="text-left text-xs text-slate-500">
            <tr><th className="py-1">Name</th><th className="py-1">Code</th><th className="py-1">Kind</th><th className="py-1">Active</th><th /></tr>
          </thead>
          <tbody>
            {data.map((t) => <CostTypeRow key={t.id} type={t} isAdmin={isAdmin} busy={busy} setBusy={setBusy} />)}
          </tbody>
        </table>
      )}

      {isAdmin && (
        <div className="flex items-end gap-2 border-t border-[var(--border)] pt-3">
          <Field label="Name"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Transport" className="w-36" /></Field>
          <Field label="Code"><Input value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} placeholder="TRN" maxLength={16} className="w-24" /></Field>
          <Field label="Kind">
            <select value={kind} onChange={(e) => setKind(e.target.value)} className="h-9 rounded-md border border-[var(--border)] bg-white px-2 text-sm">
              <option value="Amount">Amount</option>
              <option value="Percent">Percent</option>
            </select>
          </Field>
          <Button variant="outline" className="h-9" disabled={busy} onClick={add}><Plus className="h-4 w-4" /> Add</Button>
        </div>
      )}
    </Card>
  )
}

function CostTypeRow({ type, isAdmin, busy, setBusy }: { type: CostComponentType; isAdmin: boolean; busy: boolean; setBusy: (b: boolean) => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(type.name)
  const [kind, setKind] = useState(type.calcKind)
  const [active, setActive] = useState(type.isActive)
  useEffect(() => { setName(type.name); setKind(type.calcKind); setActive(type.isActive) }, [type.name, type.calcKind, type.isActive])
  const dirty = name !== type.name || kind !== type.calcKind || active !== type.isActive

  async function save() {
    setBusy(true)
    try {
      await fetchApi(`/api/cost-components/${type.id}`, { method: "PUT", body: JSON.stringify({ code: type.code, name, calcKind: kind, sortOrder: type.sortOrder, isActive: active }) })
      await qc.invalidateQueries({ queryKey: ["cost-types"] })
      toast.success(`${type.code} saved`)
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }
  async function remove() {
    setBusy(true)
    try {
      await fetchApi(`/api/cost-components/${type.id}`, { method: "DELETE" })
      await qc.invalidateQueries({ queryKey: ["cost-types"] })
      toast.success(`${type.code} removed`)
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <tr className="border-t border-[var(--border)]">
      <td className="py-2">
        {isAdmin ? <Input value={name} onChange={(e) => setName(e.target.value)} className="w-40" /> : name}
        {type.builtin && <span className="ml-1 text-xs text-slate-400">built-in</span>}
      </td>
      <td className="py-2 font-mono text-xs">{type.code}</td>
      <td className="py-2">
        {isAdmin && !type.builtin
          ? <select value={kind} onChange={(e) => setKind(e.target.value)} className="rounded-md border border-[var(--border)] bg-white px-2 py-1 text-xs"><option value="Amount">Amount</option><option value="Percent">Percent</option></select>
          : <span className="text-xs text-slate-500">{kind}</span>}
      </td>
      <td className="py-2">
        <input type="checkbox" checked={active} disabled={!isAdmin} onChange={(e) => setActive(e.target.checked)} />
      </td>
      <td className="py-2 text-right">
        {isAdmin && (
          <div className="flex justify-end gap-1">
            <Button variant="outline" className="h-7 px-2 text-xs" disabled={busy || !dirty} onClick={save}><Check className="h-3.5 w-3.5" /></Button>
            {!type.builtin && <Button variant="outline" className="h-7 px-2 text-xs text-rose-600" disabled={busy} onClick={remove}><Trash2 className="h-3.5 w-3.5" /></Button>}
          </div>
        )}
      </td>
    </tr>
  )
}

/** Manual FX rates: value of 1 unit of each currency in the tenant base currency.
 *  Estimates can present their bid converted into any currency listed here. */
function CurrencyRatesCard({ isAdmin }: { isAdmin: boolean }) {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ["currencies"], queryFn: () => fetchApi<CurrencyRates>("/api/settings/currencies") })
  const [newCode, setNewCode] = useState("")
  const [newRate, setNewRate] = useState("")
  const [busy, setBusy] = useState(false)
  const base = data?.baseCurrency ?? "—"

  async function upsert(code: string, rate: number) {
    setBusy(true)
    try {
      await fetchApi(`/api/settings/currencies/${encodeURIComponent(code)}`, { method: "PUT", body: JSON.stringify({ rateToBase: rate }) })
      await qc.invalidateQueries({ queryKey: ["currencies"] })
      toast.success(`Rate for ${code.toUpperCase()} saved`)
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }
  async function remove(code: string) {
    setBusy(true)
    try {
      await fetchApi(`/api/settings/currencies/${encodeURIComponent(code)}`, { method: "DELETE" })
      await qc.invalidateQueries({ queryKey: ["currencies"] })
      toast.success(`${code} removed`)
    } catch (e) { toast.error((e as Error).message) } finally { setBusy(false) }
  }
  async function addNew() {
    const code = newCode.trim().toUpperCase()
    const rate = Number(newRate)
    if (code.length !== 3) { toast.error("Currency code must be 3 letters."); return }
    if (!(rate > 0)) { toast.error("Rate must be greater than zero."); return }
    await upsert(code, rate)
    setNewCode(""); setNewRate("")
  }

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">Currency rates (FX)</h3>
        <p className="text-xs text-slate-400">
          Base currency is <b>{base}</b>. Enter the value of 1 unit of each currency in {base} (e.g. 1 USD = 3.6725 {base}).
          Estimates can show their bid converted into any listed currency.
        </p>
      </div>

      {data && (
        <table className="w-full text-sm">
          <thead className="text-left text-xs text-slate-500">
            <tr><th className="py-1">Currency</th><th className="py-1">Rate (1 unit in {base})</th><th className="py-1">Updated</th><th /></tr>
          </thead>
          <tbody>
            {data.rates.length === 0 && <tr><td colSpan={4} className="py-2 text-slate-400">No currencies yet.</td></tr>}
            {data.rates.map((r) => (
              <RateRow key={r.code} rate={r} base={base} isAdmin={isAdmin} busy={busy} onSave={(v) => upsert(r.code, v)} onRemove={() => remove(r.code)} />
            ))}
          </tbody>
        </table>
      )}

      {isAdmin && (
        <div className="flex items-end gap-2 border-t border-[var(--border)] pt-3">
          <Field label="Add currency"><Input value={newCode} onChange={(e) => setNewCode(e.target.value.toUpperCase())} placeholder="USD" maxLength={3} className="w-24" /></Field>
          <Field label={`Rate in ${base}`}><Input type="number" step="0.000001" min={0} value={newRate} onChange={(e) => setNewRate(e.target.value)} placeholder="3.6725" className="w-36" /></Field>
          <Button variant="outline" className="h-9" disabled={busy} onClick={addNew}><Plus className="h-4 w-4" /> Add</Button>
        </div>
      )}
    </Card>
  )
}

function RateRow({ rate, base, isAdmin, busy, onSave, onRemove }: {
  rate: CurrencyRate; base: string; isAdmin: boolean; busy: boolean; onSave: (v: number) => void; onRemove: () => void
}) {
  const [v, setV] = useState(String(rate.rateToBase))
  useEffect(() => { setV(String(rate.rateToBase)) }, [rate.rateToBase])
  const dirty = Number(v) !== rate.rateToBase && Number(v) > 0

  return (
    <tr className="border-t border-[var(--border)]">
      <td className="py-2 font-mono text-xs">{rate.code}</td>
      <td className="py-2">
        {isAdmin
          ? <Input type="number" step="0.000001" min={0} value={v} onChange={(e) => setV(e.target.value)} className="w-36" />
          : <span>{rate.rateToBase} {base}</span>}
      </td>
      <td className="py-2 text-xs text-slate-400">{rate.updatedAt.replace("T", " ").slice(0, 16)}</td>
      <td className="py-2 text-right">
        {isAdmin && (
          <div className="flex justify-end gap-1">
            <Button variant="outline" className="h-7 px-2 text-xs" disabled={busy || !dirty} onClick={() => onSave(Number(v))}><Check className="h-3.5 w-3.5" /></Button>
            <Button variant="outline" className="h-7 px-2 text-xs text-rose-600" disabled={busy} onClick={onRemove}><Trash2 className="h-3.5 w-3.5" /></Button>
          </div>
        )}
      </td>
    </tr>
  )
}

/** Company logo upload/preview/remove. The logo brands exported bid documents. */
function LogoCard({ hasLogo, isAdmin }: { hasLogo: boolean; isAdmin: boolean }) {
  const qc = useQueryClient()
  const ref = useRef<HTMLInputElement>(null)
  const [url, setUrl] = useState<string | null>(null)
  const [ver, setVer] = useState(0)
  const [busy, setBusy] = useState(false)

  // (Re)fetch the logo as an authed blob whenever it exists or changes.
  useEffect(() => {
    if (!hasLogo) { setUrl(null); return }
    let active = true
    let current: string | null = null
    fetchObjectUrl(`/api/settings/logo?v=${ver}`)
      .then((u) => { if (active) { current = u; setUrl(u) } else URL.revokeObjectURL(u) })
      .catch(() => {})
    return () => { active = false; if (current) URL.revokeObjectURL(current) }
  }, [hasLogo, ver])

  async function onPick(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (!file) return
    setBusy(true)
    try {
      await uploadFile("/api/settings/logo", file)
      await qc.invalidateQueries({ queryKey: ["settings"] })
      setVer((v) => v + 1)
      toast.success("Logo updated")
    } catch (err) {
      toast.error((err as Error).message)
    } finally {
      setBusy(false)
      if (ref.current) ref.current.value = ""
    }
  }

  async function remove() {
    setBusy(true)
    try {
      await fetchApi("/api/settings/logo", { method: "DELETE" })
      await qc.invalidateQueries({ queryKey: ["settings"] })
      setUrl(null)
      setVer((v) => v + 1)
      toast.success("Logo removed")
    } catch (err) {
      toast.error((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-slate-600">Company logo</h3>
        <p className="text-xs text-slate-400">PNG or JPEG, under 1 MB. Appears on exported Excel &amp; PDF bid documents.</p>
      </div>
      <div className="flex items-center gap-4">
        <div className="grid h-20 w-40 place-items-center overflow-hidden rounded-md border border-dashed border-[var(--border)] bg-slate-50">
          {url
            ? <img src={url} alt="Company logo" className="max-h-full max-w-full object-contain" />
            : <span className="flex items-center gap-1 text-xs text-slate-400"><ImageIcon className="h-4 w-4" /> No logo</span>}
        </div>
        {isAdmin && (
          <div className="flex flex-col gap-2">
            <input ref={ref} type="file" accept="image/png,image/jpeg" className="hidden" onChange={onPick} />
            <Button variant="outline" className="h-8 text-xs" disabled={busy} onClick={() => ref.current?.click()}>
              <Upload className="h-4 w-4" /> {hasLogo ? "Replace" : "Upload"}
            </Button>
            {hasLogo && (
              <Button variant="outline" className="h-8 text-xs text-rose-600" disabled={busy} onClick={remove}>
                <Trash2 className="h-4 w-4" /> Remove
              </Button>
            )}
          </div>
        )}
      </div>
    </Card>
  )
}
