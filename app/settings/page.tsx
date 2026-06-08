"use client"

/**
 * Settings page (25.2 tab restructure).
 *
 * Was: 14 cards stacked in a 6,000-px column with no visual grouping — finding
 * a specific setting meant scrolling past everything else on the way.
 *
 * Now: four logical tabs below the page title.
 *   • Company       — company profile, logo, document branding, custom domain
 *   • Workspace     — estimating defaults, approvals, currency FX, email, digest
 *   • Catalogues    — cost-component types, templates, activities, project types
 *   • Integrations  — SSO, webhooks, API keys
 *
 * The shared form-state (`f`) — used by Company and Workspace tabs — and the
 * single "Save settings" mutation stay lifted into `SettingsForm` so the user
 * can edit fields across either tab and click Save from either. The Save
 * button renders inside the tabs that own inline fields so it sits close to
 * what was edited.
 *
 * URL deep-link: `?tab=integrations` (etc.) — the Tabs primitive handles it.
 */

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
import { Tabs } from "@/components/tabs"
import { useT } from "@/lib/i18n"
import { WebhooksCard } from "./webhooks-card"
import { DigestCard } from "./digest-card"
import { ApiKeysCard } from "./api-keys-card"
import { TemplatesCard } from "./templates-card"
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
  const t = useT()
  const qc = useQueryClient()
  const { isAdmin } = usePermissions()
  const { data, isLoading, error } = useQuery({ queryKey: ["settings"], queryFn: () => fetchApi<TenantSettings>("/api/settings") })
  const [f, setF] = useState<TenantSettings | null>(null)
  useEffect(() => { if (data) setF(data) }, [data])

  const save = useMutation({
    mutationFn: (v: TenantSettings) => fetchApi<TenantSettings>("/api/settings", { method: "PUT", body: JSON.stringify(v) }),
    onSuccess: (d) => { qc.setQueryData(["settings"], d); setF(d); toast.success(t("adm.saved")) },
    onError: (e) => toast.error((e as Error).message),
  })

  if (isLoading || !f) return <p className="text-muted">{t("adm.loading")}</p>
  if (error) return <p className="text-danger">{(error as Error).message}</p>

  const ro = !isAdmin
  // The "Save settings" affordance is shared between the Company and Workspace
  // tabs (the two that drive `f`). Computed once as a JSX value so both tabs
  // render the SAME button — saving from either persists the whole `f` object,
  // including unsaved edits made in the other tab. Kept as a value (not a
  // nested component) so React doesn't reset it on every parent render.
  const saveBar = isAdmin
    ? <Button disabled={save.isPending} onClick={() => save.mutate(f)}>{save.isPending ? t("adm.saving") : t("adm.save")}</Button>
    : <p className="text-xs text-muted">{t("adm.adminOnly")}</p>

  return (
    <div className="max-w-2xl">
      <Tabs
        ariaLabel={t("stab.aria")}
        defaultId="company"
        tabs={[
          {
            id: "company",
            label: t("stab.company"),
            content: (
              <div className="space-y-6">
                <CompanyProfileCard f={f} setF={setF} ro={ro} />
                <LogoCard hasLogo={f.hasLogo} isAdmin={isAdmin} />
                {/* 24.5 — Branding text rendered on the bid letter PDF. */}
                <BrandingCard f={f} setF={setF} ro={ro} />
                <CustomDomainCard isAdmin={isAdmin} />
                {saveBar}
              </div>
            ),
          },
          {
            id: "workspace",
            label: t("stab.workspace"),
            content: (
              <div className="space-y-6">
                <EstimatingDefaultsCard f={f} setF={setF} ro={ro} />
                <ApprovalWorkflowCard f={f} setF={setF} ro={ro} />
                <CurrencyRatesCard isAdmin={isAdmin} />
                {/* 21.1 — Email notification toggle + test send. */}
                <EmailCard f={f} setF={setF} ro={ro} isAdmin={isAdmin} />
                {/* 22.1 — Per-user email digest (auto-saves its own field). */}
                <DigestCard isAdmin={isAdmin} />
                {saveBar}
              </div>
            ),
          },
          {
            id: "catalogues",
            label: t("stab.catalogues"),
            content: (
              <div className="space-y-6">
                <CostTypesCard isAdmin={isAdmin} />
                <TemplatesCard />
                <ActivitiesCard isAdmin={isAdmin} />
                <ProjectTypesCard isAdmin={isAdmin} />
              </div>
            ),
          },
          {
            id: "integrations",
            label: t("stab.integrations"),
            content: (
              <div className="space-y-6">
                <SsoCard isAdmin={isAdmin} />
                <WebhooksCard isAdmin={isAdmin} />
                <ApiKeysCard isAdmin={isAdmin} />
              </div>
            ),
          },
        ]}
      />
    </div>
  )
}

// 25.2 — Extracted from the previous inline JSX so each tab can render the card
// without leaking the parent's onChange wiring. State still lives in the parent
// (`f`) so the single Save button can persist the whole object.
function CompanyProfileCard({ f, setF, ro }: { f: TenantSettings; setF: (s: TenantSettings) => void; ro: boolean }) {
  const set = (k: keyof TenantSettings) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value })
  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-muted">Company profile</h3>
        <p className="text-xs text-muted">Appears on the header of exported bid documents.</p>
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
  )
}

function EstimatingDefaultsCard({ f, setF, ro }: { f: TenantSettings; setF: (s: TenantSettings) => void; ro: boolean }) {
  const set = (k: keyof TenantSettings) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value })
  const setNum = (k: keyof TenantSettings) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: Number(e.target.value || 0) })
  return (
    <Card className="space-y-4 p-5">
      <h3 className="text-sm font-semibold text-muted">Estimating defaults</h3>
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
  )
}

// 20.2 — Approval workflow. RequiredApprovals=0 keeps the legacy "anyone with
// permission can publish" behavior; any positive value enforces N sign-offs.
function ApprovalWorkflowCard({ f, setF, ro }: { f: TenantSettings; setF: (s: TenantSettings) => void; ro: boolean }) {
  const setNum = (k: keyof TenantSettings) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: Number(e.target.value || 0) })
  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-muted">Approval workflow</h3>
        <p className="text-xs text-muted">
          Number of TenantAdmin sign-offs required before an estimate can be Published.
          Zero disables the workflow. Any BOQ / preliminaries / markups / risks edit invalidates existing sign-offs.
        </p>
      </div>
      <div className="grid grid-cols-4 gap-3">
        <Field label="Required approvals"><Input type="number" min={0} value={f.requiredApprovalsToPublish} onChange={setNum("requiredApprovalsToPublish")} disabled={ro} /></Field>
      </div>
    </Card>
  )
}

/** 24.5 — Branding text rendered on the bid-letter PDF. Three multi-line plain-text fields:
 *  header (above the body, e.g. "Licensed General Contractor"), footer (replaces the
 *  default page-count footer), signature block (overrides the boilerplate sign-off).
 *  Stays inline in the page state; saved with the page's "Save settings" button.
 *  An empty textarea clears the field on save; the 2 KB cap is enforced server-side. */
function BrandingCard({ f, setF, ro }: { f: TenantSettings; setF: (s: TenantSettings) => void; ro: boolean }) {
  // Live character-count surfaces the 2 KB cap before the user hits Save and 400s.
  const counter = (s: string | null) => `${(s ?? "").length} / 2048`
  const update = (k: "brandHeaderText" | "brandFooterText" | "brandSignatureText") =>
    (e: React.ChangeEvent<HTMLTextAreaElement>) => setF({ ...f, [k]: e.target.value === "" ? null : e.target.value })
  const ta = "min-h-[72px] w-full rounded-md border border-[var(--border)] bg-[var(--card)] px-3 py-2 text-sm outline-none focus:border-[var(--brand)] focus:ring-2 focus:ring-[var(--brand)]/20 disabled:bg-[color-mix(in_oklab,var(--text)_6%,transparent)]"

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-muted">Document branding</h3>
        <p className="text-xs text-muted">
          Plain text rendered on the bid-letter PDF. Newlines preserved. 2 KB max per field.
        </p>
      </div>

      <div>
        <label htmlFor="brand-header" className="mb-1 block text-sm font-medium text-muted">
          Header (above the body)
        </label>
        <textarea
          id="brand-header" className={ta} disabled={ro}
          placeholder="e.g. Licensed General Contractor — Lic. #AB-12345"
          value={f.brandHeaderText ?? ""} onChange={update("brandHeaderText")} maxLength={2048}
        />
        <p className="mt-0.5 text-right text-xs text-muted">{counter(f.brandHeaderText)}</p>
      </div>

      <div>
        <label htmlFor="brand-footer" className="mb-1 block text-sm font-medium text-muted">
          Footer (replaces page-count footer)
        </label>
        <textarea
          id="brand-footer" className={ta} disabled={ro}
          placeholder="e.g. © ACME Construction LLC · Confidential"
          value={f.brandFooterText ?? ""} onChange={update("brandFooterText")} maxLength={2048}
        />
        <p className="mt-0.5 text-right text-xs text-muted">{counter(f.brandFooterText)}</p>
      </div>

      <div>
        <label htmlFor="brand-signature" className="mb-1 block text-sm font-medium text-muted">
          Signature block (overrides the bid-letter sign-off)
        </label>
        <textarea
          id="brand-signature" className={ta} disabled={ro}
          placeholder={"Yours faithfully,\nACME Construction LLC\nTender Office"}
          value={f.brandSignatureText ?? ""} onChange={update("brandSignatureText")} maxLength={2048}
        />
        <p className="mt-0.5 text-right text-xs text-muted">{counter(f.brandSignatureText)}</p>
      </div>
    </Card>
  )
}

/** 21.1 — Email-notification toggle + a "send test email" action. The toggle edits
 *  the shared settings state so it saves with the page's "Save settings" button; the
 *  test send is independent and only enabled when the platform transport is configured. */
function EmailCard({ f, setF, ro, isAdmin }: { f: TenantSettings; setF: (s: TenantSettings) => void; ro: boolean; isAdmin: boolean }) {
  const t = useT()
  const [testing, setTesting] = useState(false)

  async function sendTest() {
    setTesting(true)
    try {
      const r = await fetchApi<{ configured: boolean; sent: boolean }>("/api/settings/email/test", { method: "POST" })
      if (!r.configured) toast.error(t("adm.email.notConfiguredToast"))
      else if (r.sent) toast.success(t("adm.email.sent"))
      else toast.error(t("adm.email.testFailed"))
    } catch (e) { toast.error((e as Error).message) } finally { setTesting(false) }
  }

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="text-sm font-semibold text-muted">{t("adm.email.heading")}</h3>
        <p className="text-xs text-muted">{t("adm.email.sub")}</p>
      </div>

      <label className="flex items-center gap-2 text-sm text-[var(--text)]">
        <input
          type="checkbox"
          checked={f.notificationEmailsEnabled}
          disabled={ro}
          onChange={(e) => setF({ ...f, notificationEmailsEnabled: e.target.checked })}
        />
        {t("adm.email.toggle")}
      </label>

      <p className="text-xs">
        {f.emailConfigured
          ? <span className="text-success">{t("adm.email.configured")}</span>
          : <span className="text-warning">{t("adm.email.notConfigured")}</span>}
      </p>

      {isAdmin && (
        <div className="flex items-center gap-3">
          <Button variant="outline" className="h-8 text-sm" disabled={testing || !f.emailConfigured} onClick={sendTest}>
            <Mail className="h-4 w-4" /> {testing ? t("adm.email.sending") : t("adm.email.test")}
          </Button>
          <span className="text-xs text-muted">{t("adm.email.toggleHint")}</span>
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
        <h3 className="text-sm font-semibold text-muted">Project types</h3>
        <p className="text-xs text-muted">
          The dropdown of project types (Civil, Mechanical, Electrical…) shown when creating or editing a
          project. Built-in types can be deactivated but not deleted; add your own below.
        </p>
      </div>

      {data && (
        <table className="w-full text-sm">
          <thead className="text-left text-sm text-muted"><tr><th className="py-1">Name</th><th className="py-1">Active</th><th /></tr></thead>
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
        {isAdmin && !type.builtin ? <Input value={name} onChange={(e) => setName(e.target.value)} aria-label={`Project type name (${type.name})`} className="w-48" /> : name}
        {type.builtin && <span className="ml-1 text-xs text-muted">built-in</span>}
      </td>
      <td className="py-2"><input type="checkbox" checked={active} disabled={!isAdmin} onChange={(e) => setActive(e.target.checked)} aria-label={`${type.name} active`} /></td>
      <td className="py-2 text-right">
        {isAdmin && (
          <div className="flex justify-end gap-1">
            <Button variant="outline" aria-label={`Save ${type.name}`} className="h-7 px-2 text-sm" disabled={busy || !dirty} onClick={save}><Check className="h-3.5 w-3.5" /></Button>
            {!type.builtin && <Button variant="outline" aria-label={`Remove ${type.name}`} className="h-7 px-2 text-sm text-danger" disabled={busy} onClick={remove}><Trash2 className="h-3.5 w-3.5" /></Button>}
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
        <h3 className="text-sm font-semibold text-muted">Activities</h3>
        <p className="text-xs text-muted">
          The dropdown of construction activities shown when adding work under a unit. Built-in activities
          can be deactivated but not deleted; add your own below.
        </p>
      </div>

      {data && (
        <table className="w-full text-sm">
          <thead className="text-left text-sm text-muted"><tr><th className="py-1">Name</th><th className="py-1">Active</th><th /></tr></thead>
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
        {isAdmin && !activity.builtin ? <Input value={name} onChange={(e) => setName(e.target.value)} aria-label={`Activity name (${activity.name})`} className="w-48" /> : name}
        {activity.builtin && <span className="ml-1 text-xs text-muted">built-in</span>}
      </td>
      <td className="py-2"><input type="checkbox" checked={active} disabled={!isAdmin} onChange={(e) => setActive(e.target.checked)} aria-label={`${activity.name} active`} /></td>
      <td className="py-2 text-right">
        {isAdmin && (
          <div className="flex justify-end gap-1">
            <Button variant="outline" aria-label={`Save ${activity.name}`} className="h-7 px-2 text-sm" disabled={busy || !dirty} onClick={save}><Check className="h-3.5 w-3.5" /></Button>
            {!activity.builtin && <Button variant="outline" aria-label={`Remove ${activity.name}`} className="h-7 px-2 text-sm text-danger" disabled={busy} onClick={remove}><Trash2 className="h-3.5 w-3.5" /></Button>}
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
        <h3 className="text-sm font-semibold text-muted">Cost-component types</h3>
        <p className="text-xs text-muted">
          Categories used to build up each BOQ item's unit rate. <b>Amount</b> types are money per unit;
          <b> Percent</b> types apply to the amount subtotal (e.g. Waste, Overheads). Built-in types can be deactivated but not deleted.
        </p>
      </div>

      {data && (
        <table className="w-full text-sm">
          <thead className="text-left text-sm text-muted">
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
            <select value={kind} onChange={(e) => setKind(e.target.value)} className="h-9 rounded-md border border-[var(--border)] bg-[var(--card)] px-2 text-sm">
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
        {isAdmin ? <Input value={name} onChange={(e) => setName(e.target.value)} aria-label={`Cost type name (${type.code})`} className="w-40" /> : name}
        {type.builtin && <span className="ml-1 text-xs text-muted">built-in</span>}
      </td>
      <td className="py-2 font-mono text-sm">{type.code}</td>
      <td className="py-2">
        {isAdmin && !type.builtin
          ? <select value={kind} onChange={(e) => setKind(e.target.value)} aria-label={`Cost type kind (${type.code})`} className="rounded-md border border-[var(--border)] bg-[var(--card)] px-2 py-1 text-xs"><option value="Amount">Amount</option><option value="Percent">Percent</option></select>
          : <span className="text-xs text-muted">{kind}</span>}
      </td>
      <td className="py-2">
        <input type="checkbox" checked={active} disabled={!isAdmin} onChange={(e) => setActive(e.target.checked)} aria-label={`${type.code} active`} />
      </td>
      <td className="py-2 text-right">
        {isAdmin && (
          <div className="flex justify-end gap-1">
            <Button variant="outline" aria-label={`Save ${type.code}`} className="h-7 px-2 text-sm" disabled={busy || !dirty} onClick={save}><Check className="h-3.5 w-3.5" /></Button>
            {!type.builtin && <Button variant="outline" aria-label={`Remove ${type.code}`} className="h-7 px-2 text-sm text-danger" disabled={busy} onClick={remove}><Trash2 className="h-3.5 w-3.5" /></Button>}
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
        <h3 className="text-sm font-semibold text-muted">Currency rates (FX)</h3>
        <p className="text-xs text-muted">
          Base currency is <b>{base}</b>. Enter the value of 1 unit of each currency in {base} (e.g. 1 USD = 3.6725 {base}).
          Estimates can show their bid converted into any listed currency.
        </p>
      </div>

      {data && (
        <table className="w-full text-sm">
          <thead className="text-left text-sm text-muted">
            <tr><th className="py-1">Currency</th><th className="py-1">Rate (1 unit in {base})</th><th className="py-1">Updated</th><th /></tr>
          </thead>
          <tbody>
            {data.rates.length === 0 && <tr><td colSpan={4} className="py-2 text-muted">No currencies yet.</td></tr>}
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
      <td className="py-2 font-mono text-sm">{rate.code}</td>
      <td className="py-2">
        {isAdmin
          ? <Input type="number" step="0.000001" min={0} value={v} onChange={(e) => setV(e.target.value)} aria-label={`Rate for ${rate.code}`} className="w-36" />
          : <span>{rate.rateToBase} {base}</span>}
      </td>
      <td className="py-2 text-xs text-muted">{rate.updatedAt.replace("T", " ").slice(0, 16)}</td>
      <td className="py-2 text-right">
        {isAdmin && (
          <div className="flex justify-end gap-1">
            <Button variant="outline" aria-label={`Save ${rate.code} rate`} className="h-7 px-2 text-sm" disabled={busy || !dirty} onClick={() => onSave(Number(v))}><Check className="h-3.5 w-3.5" /></Button>
            <Button variant="outline" aria-label={`Remove ${rate.code} rate`} className="h-7 px-2 text-sm text-danger" disabled={busy} onClick={onRemove}><Trash2 className="h-3.5 w-3.5" /></Button>
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
        <h3 className="text-sm font-semibold text-muted">Company logo</h3>
        <p className="text-xs text-muted">PNG or JPEG, under 1 MB. Appears on exported Excel &amp; PDF bid documents.</p>
      </div>
      <div className="flex items-center gap-4">
        <div className="grid h-20 w-40 place-items-center overflow-hidden rounded-md border border-dashed border-[var(--border)] bg-[color-mix(in_oklab,var(--text)_6%,transparent)]">
          {url
            ? <img src={url} alt="Company logo" className="max-h-full max-w-full object-contain" />
            : <span className="flex items-center gap-1 text-xs text-muted"><ImageIcon className="h-4 w-4" /> No logo</span>}
        </div>
        {isAdmin && (
          <div className="flex flex-col gap-2">
            <input ref={ref} type="file" accept="image/png,image/jpeg" className="hidden" onChange={onPick} />
            <Button variant="outline" className="h-8 text-sm" disabled={busy} onClick={() => ref.current?.click()}>
              <Upload className="h-4 w-4" /> {hasLogo ? "Replace" : "Upload"}
            </Button>
            {hasLogo && (
              <Button variant="outline" className="h-8 text-sm text-danger" disabled={busy} onClick={remove}>
                <Trash2 className="h-4 w-4" /> Remove
              </Button>
            )}
          </div>
        )}
      </div>
    </Card>
  )
}
