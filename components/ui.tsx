"use client"

import { cn } from "@/lib/utils"
import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode } from "react"

export function Button({
  className, variant = "primary", ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "primary" | "ghost" | "outline" }) {
  const styles = {
    primary: "bg-[var(--brand)] text-white hover:opacity-90",
    ghost: "bg-transparent hover:bg-slate-100 text-slate-700",
    outline: "border border-[var(--border)] bg-white hover:bg-slate-50 text-slate-700",
  }[variant]
  return (
    <button
      className={cn(
        "inline-flex items-center justify-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition disabled:opacity-50 disabled:pointer-events-none",
        styles, className,
      )}
      {...props}
    />
  )
}

export function Input({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      className={cn(
        "w-full rounded-md border border-[var(--border)] bg-white px-3 py-2 text-sm outline-none",
        "focus:border-[var(--brand)] focus:ring-2 focus:ring-[var(--brand)]/20",
        className,
      )}
      {...props}
    />
  )
}

export function Card({ className, children }: { className?: string; children: ReactNode }) {
  return (
    <div className={cn("rounded-lg border border-[var(--border)] bg-white shadow-sm", className)}>
      {children}
    </div>
  )
}

export function Badge({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <span className={cn("inline-flex rounded-full px-2.5 py-0.5 text-xs font-medium", className)}>
      {children}
    </span>
  )
}

export function statusColor(status: string) {
  switch (status.toLowerCase()) {
    case "bidding": return "bg-amber-100 text-amber-800"
    case "won": return "bg-emerald-100 text-emerald-800"
    case "lost": return "bg-rose-100 text-rose-700"
    case "submitted": return "bg-sky-100 text-sky-800"
    case "archived": return "bg-slate-100 text-slate-600"
    default: return "bg-slate-100 text-slate-700"   // draft
  }
}
