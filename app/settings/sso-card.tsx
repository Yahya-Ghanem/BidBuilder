"use client"

import { useState, useEffect } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { KeyRound, Check, Copy } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { SamlConfig } from "@/lib/types"
import { Card, Button, Input } from "@/components/ui"
import { Field } from "@/components/form"
import { useT } from "@/lib/i18n"

/**
 * 20.8b — Configure SAML 2.0 single sign-on for this workspace. The admin registers
 * their IdP (entity ID, SSO URL, signing certificate) and we expose the SP-side URLs
 * (EntityID, ACS, metadata) to register on the IdP. The signing certificate is
 * write-only — it's never returned, only its presence is shown. Tenant admin only.
 */
export function SsoCard({ isAdmin }: { isAdmin: boolean }) {
  const t = useT()
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ["sso-config"], queryFn: () => fetchApi<SamlConfig>("/api/sso/config"), enabled: isAdmin })

  const [enabled, setEnabled] = useState(false)
  const [idpEntityId, setIdpEntityId] = useState("")
  const [idpSsoUrl, setIdpSsoUrl] = useState("")
  const [cert, setCert] = useState("")               // write-only; blank leaves the stored cert alone
  const [emailAttribute, setEmailAttribute] = useState("")
  const [nameAttribute, setNameAttribute] = useState("")
  const [allowJit, setAllowJit] = useState(true)

  useEffect(() => {
    if (!data) return
    setEnabled(data.enabled)
    setIdpEntityId(data.idpEntityId)
    setIdpSsoUrl(data.idpSsoUrl)
    setEmailAttribute(data.emailAttribute ?? "")
    setNameAttribute(data.nameAttribute ?? "")
    setAllowJit(data.allowJitProvisioning)
  }, [data])

  const save = useMutation({
    mutationFn: () => fetchApi<SamlConfig>("/api/sso/config", {
      method: "PUT",
      body: JSON.stringify({
        enabled, idpEntityId, idpSsoUrl,
        idpCertificatePem: cert.trim() === "" ? null : cert,
        emailAttribute: emailAttribute.trim() === "" ? null : emailAttribute,
        nameAttribute: nameAttribute.trim() === "" ? null : nameAttribute,
        allowJitProvisioning: allowJit,
      }),
    }),
    onSuccess: (d) => { qc.setQueryData(["sso-config"], d); setCert(""); toast.success("SSO settings saved") },
    onError: (e) => toast.error((e as Error).message),
  })

  if (!isAdmin) return null

  const copy = (v: string) => navigator.clipboard.writeText(v).then(() => toast.success("Copied"))

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="flex items-center gap-2 text-sm font-semibold text-muted">
          <KeyRound className="h-4 w-4 text-[var(--brand)]" /> {t("adm.sso.heading")}
          {data?.enabled && <span className="rounded bg-emerald-100 px-1.5 py-0.5 text-[10px] font-medium uppercase text-emerald-700">on</span>}
        </h3>
        <p className="text-xs text-muted">
          Let members sign in through your identity provider (Okta, Azure AD, Google Workspace, ADFS…).
        </p>
      </div>

      {/* SP-side values to register with the IdP. */}
      {data && (
        <div className="space-y-2 rounded-md border border-[var(--border)] bg-[color-mix(in_oklab,var(--text)_6%,transparent)] p-3 text-xs">
          <p className="font-medium text-muted">Register these with your IdP:</p>
          {([["SP Entity ID", data.spEntityId], ["ACS (Reply) URL", data.acsUrl], ["Metadata URL", data.metadataUrl]] as const).map(([label, val]) => (
            <div key={label} className="flex items-center gap-2">
              <span className="w-28 shrink-0 text-muted">{label}</span>
              <code className="flex-1 break-all rounded bg-[var(--card)] px-2 py-1 font-mono text-[var(--text)]">{val}</code>
              <button type="button" title="Copy" onClick={() => copy(val)}
                className="rounded p-1 text-muted hover:bg-[color-mix(in_oklab,var(--text)_6%,transparent)] hover:text-[var(--brand)]"><Copy className="h-3.5 w-3.5" /></button>
            </div>
          ))}
        </div>
      )}

      <Field label="IdP Entity ID (Issuer)">
        <Input value={idpEntityId} onChange={(e) => setIdpEntityId(e.target.value)} placeholder="https://idp.example.com/entity" />
      </Field>
      <Field label="IdP SSO URL">
        <Input value={idpSsoUrl} onChange={(e) => setIdpSsoUrl(e.target.value)} placeholder="https://idp.example.com/sso" />
      </Field>
      <Field label={data?.hasCertificate ? "IdP signing certificate (PEM) — leave blank to keep the current one" : "IdP signing certificate (PEM)"}>
        <textarea
          value={cert} onChange={(e) => setCert(e.target.value)}
          placeholder={data?.hasCertificate ? "•••••••• (a certificate is stored)" : "-----BEGIN CERTIFICATE-----\n…\n-----END CERTIFICATE-----"}
          rows={4}
          className="w-full rounded-md border border-[var(--border)] bg-[var(--card)] px-3 py-2 font-mono text-xs text-[var(--text)] focus:border-[var(--brand)] focus:outline-none"
        />
      </Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Email attribute (optional)">
          <Input value={emailAttribute} onChange={(e) => setEmailAttribute(e.target.value)} placeholder="(NameID by default)" />
        </Field>
        <Field label="Name attribute (optional)">
          <Input value={nameAttribute} onChange={(e) => setNameAttribute(e.target.value)} placeholder="display name claim" />
        </Field>
      </div>

      <label className="flex items-center gap-2 text-sm text-muted">
        <input type="checkbox" checked={allowJit} onChange={(e) => setAllowJit(e.target.checked)} className="h-4 w-4" />
        Auto-create accounts on first SSO sign-in (JIT provisioning)
      </label>
      <label className="flex items-center gap-2 text-sm text-muted">
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} className="h-4 w-4" />
        Enable single sign-on for this workspace
      </label>

      <Button disabled={save.isPending} onClick={() => save.mutate()}>
        <Check className="h-4 w-4" /> {save.isPending ? "Saving…" : "Save SSO settings"}
      </Button>
    </Card>
  )
}
