"use client"

import { useEffect, useState } from "react"
import { useRouter } from "next/navigation"
import { LayoutGrid, ShieldCheck, KeyRound } from "lucide-react"
import { toast } from "sonner"
import { useAuth } from "@/lib/auth"
import { useT } from "@/lib/i18n"
import { API_URL } from "@/lib/api"
import { Button, Input, Card } from "@/components/ui"
import { LanguageSwitcher } from "@/components/language-switcher"

export default function LoginPage() {
  const { login, isAuthenticated, isLoading } = useAuth()
  const t = useT()
  const router = useRouter()
  const [email, setEmail] = useState("admin@bidbuilder.local")
  const [password, setPassword] = useState("")
  const [tenant, setTenant] = useState("default")
  const [busy, setBusy] = useState(false)

  // MFA step (20.8): shown after a password is accepted for a 2FA-protected account.
  const [mfa, setMfa] = useState(false)
  const [useRecovery, setUseRecovery] = useState(false)
  const [code, setCode] = useState("")

  useEffect(() => {
    if (!isLoading && isAuthenticated) router.replace("/projects")
  }, [isLoading, isAuthenticated, router])

  // Surface an SSO failure handed back by the ACS endpoint (?ssoError=…), then
  // strip it from the URL so a refresh doesn't re-toast it.
  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    const err = params.get("ssoError")
    if (err) {
      toast.error(err)
      window.history.replaceState(null, "", "/login")
    }
  }, [])

  // SP-initiated SSO: hand the browser to the API, which redirects to the tenant's
  // IdP and (after auth) back to /login/sso with a session.
  function signInWithSso() {
    const slug = (tenant || "default").trim()
    window.location.href = `${API_URL}/api/auth/sso/${encodeURIComponent(slug)}/login`
  }

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      const second = mfa
        ? (useRecovery ? { recoveryCode: code.trim() } : { totpCode: code.trim() })
        : undefined
      const result = await login(email, password, tenant, second)
      if (result === "mfa") {
        setMfa(true)
        toast.message("Enter the code from your authenticator app")
        return
      }
      toast.success("Welcome back")
      router.replace("/projects")
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Login failed")
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="grid min-h-screen place-items-center p-4">
      <Card className="w-full max-w-sm p-6">
        <div className="mb-6 flex items-center justify-between">
          <div className="flex items-center gap-2 text-xl font-bold">
            <LayoutGrid className="h-6 w-6 text-[var(--brand)]" /> BidBuilder
          </div>
          <LanguageSwitcher />
        </div>

        {!mfa ? (
          <>
            <p className="mb-4 text-sm text-muted">{t("login.subtitle")}</p>
            <form onSubmit={onSubmit} className="space-y-3">
              <div>
                <label htmlFor="login-tenant" className="mb-1 block text-xs font-medium text-muted">{t("login.tenant")}</label>
                <Input id="login-tenant" value={tenant} onChange={(e) => setTenant(e.target.value)} placeholder="default" />
              </div>
              <div>
                <label htmlFor="login-email" className="mb-1 block text-xs font-medium text-muted">{t("login.email")}</label>
                <Input id="login-email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required />
              </div>
              <div>
                <label htmlFor="login-password" className="mb-1 block text-xs font-medium text-muted">{t("login.password")}</label>
                <Input id="login-password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
              </div>
              <Button type="submit" className="w-full" disabled={busy}>
                {busy ? t("login.signingIn") : t("login.signIn")}
              </Button>
            </form>

            <div className="my-4 flex items-center gap-3 text-xs text-muted">
              <span className="h-px flex-1 bg-[var(--border)]" /> {t("login.or")} <span className="h-px flex-1 bg-[var(--border)]" />
            </div>
            <Button type="button" variant="outline" className="w-full" onClick={signInWithSso}>
              <KeyRound className="h-4 w-4" /> {t("login.sso")}
            </Button>
          </>
        ) : (
          <>
            <div className="mb-4 flex items-center gap-2 text-sm text-muted">
              <ShieldCheck className="h-4 w-4 text-[var(--brand)]" />
              {useRecovery ? "Enter a recovery code" : "Two-factor authentication"}
            </div>
            <form onSubmit={onSubmit} className="space-y-3">
              <div>
                <label htmlFor="login-mfa-code" className="mb-1 block text-xs font-medium text-muted">
                  {useRecovery ? "Recovery code" : "6-digit code"}
                </label>
                <Input
                  id="login-mfa-code"
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  placeholder={useRecovery ? "abcde-fghij" : "123456"}
                  inputMode={useRecovery ? "text" : "numeric"}
                  autoFocus
                  required
                />
              </div>
              <Button type="submit" className="w-full" disabled={busy}>
                {busy ? "Verifying…" : "Verify & sign in"}
              </Button>
              <div className="flex items-center justify-between text-xs">
                <button type="button" className="text-muted hover:text-[var(--brand)]"
                  onClick={() => { setUseRecovery((v) => !v); setCode("") }}>
                  {useRecovery ? "Use authenticator code" : "Use a recovery code"}
                </button>
                <button type="button" className="text-muted hover:text-muted"
                  onClick={() => { setMfa(false); setCode(""); setPassword("") }}>
                  Back
                </button>
              </div>
            </form>
          </>
        )}

        <p className="mt-4 text-center text-xs text-muted">
          <a href="/platform" className="hover:text-[var(--brand)] hover:underline">{t("login.platformAdmin")}</a>
        </p>
      </Card>
    </div>
  )
}
