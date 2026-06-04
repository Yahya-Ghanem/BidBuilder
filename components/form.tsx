"use client"

import { cn } from "@/lib/utils"
import { X } from "lucide-react"
import type { ReactNode, SelectHTMLAttributes, TextareaHTMLAttributes } from "react"
import { Button } from "@/components/ui"

/** Lightweight modal dialog (no external dep). */
export function Modal({
  open, onClose, title, children, footer,
}: { open: boolean; onClose: () => void; title: string; children: ReactNode; footer?: ReactNode }) {
  if (!open) return null
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/40 p-4" onClick={onClose}>
      <div className="w-full max-w-lg rounded-lg bg-white shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3">
          <h3 className="font-semibold">{title}</h3>
          <button onClick={onClose} className="rounded p-1 text-slate-400 hover:bg-slate-100"><X className="h-4 w-4" /></button>
        </div>
        <div className="max-h-[70vh] overflow-auto px-5 py-4">{children}</div>
        {footer && <div className="flex justify-end gap-2 border-t border-[var(--border)] px-5 py-3">{footer}</div>}
      </div>
    </div>
  )
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-slate-600">{label}</span>
      {children}
    </label>
  )
}

export function Select({ className, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select
      className={cn(
        "w-full rounded-md border border-[var(--border)] bg-white px-3 py-2 text-sm outline-none",
        "focus:border-[var(--brand)] focus:ring-2 focus:ring-[var(--brand)]/20",
        className,
      )}
      {...props}
    />
  )
}

export function Textarea({ className, ...props }: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return (
    <textarea
      className={cn(
        "w-full rounded-md border border-[var(--border)] bg-white px-3 py-2 text-sm outline-none",
        "focus:border-[var(--brand)] focus:ring-2 focus:ring-[var(--brand)]/20",
        className,
      )}
      {...props}
    />
  )
}

/** Standard confirm/cancel footer for a modal form. */
export function ModalActions({ onCancel, busy, submitLabel = "Save" }: { onCancel: () => void; busy?: boolean; submitLabel?: string }) {
  return (
    <>
      <Button type="button" variant="outline" onClick={onCancel}>Cancel</Button>
      <Button type="submit" disabled={busy}>{busy ? "Saving…" : submitLabel}</Button>
    </>
  )
}
