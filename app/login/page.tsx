"use client"

import { useEffect, useState } from "react"
import { useRouter } from "next/navigation"
import { LayoutGrid } from "lucide-react"
import { toast } from "sonner"
import { useAuth } from "@/lib/auth"
import { Button, Input, Card } from "@/components/ui"

export default function LoginPage() {
  const { login, isAuthenticated, isLoading } = useAuth()
  const router = useRouter()
  const [email, setEmail] = useState("admin@bidbuilder.local")
  const [password, setPassword] = useState("")
  const [tenant, setTenant] = useState("default")
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!isLoading && isAuthenticated) router.replace("/projects")
  }, [isLoading, isAuthenticated, router])

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      await login(email, password, tenant)
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
        <div className="mb-6 flex items-center gap-2 text-xl font-bold">
          <LayoutGrid className="h-6 w-6 text-[var(--brand)]" /> BidBuilder
        </div>
        <p className="mb-4 text-sm text-slate-500">Sign in to your estimating workspace.</p>
        <form onSubmit={onSubmit} className="space-y-3">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600">Company (tenant)</label>
            <Input value={tenant} onChange={(e) => setTenant(e.target.value)} placeholder="default" />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600">Email</label>
            <Input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600">Password</label>
            <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required />
          </div>
          <Button type="submit" className="w-full" disabled={busy}>
            {busy ? "Signing in…" : "Sign in"}
          </Button>
        </form>
        <p className="mt-4 text-center text-xs text-slate-400">
          <a href="/platform" className="hover:text-[var(--brand)] hover:underline">Platform administration</a>
        </p>
      </Card>
    </div>
  )
}
