"use client"

import { useEffect } from "react"
import { useRouter } from "next/navigation"
import { useAuth } from "@/lib/auth"

/** Root: bounce to /projects when signed in, else /login. */
export default function Home() {
  const { isLoading, isAuthenticated } = useAuth()
  const router = useRouter()
  useEffect(() => {
    if (isLoading) return
    router.replace(isAuthenticated ? "/projects" : "/login")
  }, [isLoading, isAuthenticated, router])
  return <div className="grid min-h-screen place-items-center text-slate-400">Loading…</div>
}
