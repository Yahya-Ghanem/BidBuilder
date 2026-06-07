"use client"

import { cn } from "@/lib/utils"
import { X } from "lucide-react"
import { useEffect, useId, useRef, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react"
import { Button } from "@/components/ui"

const FOCUSABLE = 'a[href],button:not([disabled]),textarea,input,select,[tabindex]:not([tabindex="-1"])'

/** Lightweight modal dialog (no external dep). Accessible: role=dialog + aria-modal,
 *  labelled by its title, closes on Escape, traps Tab focus inside, and restores focus
 *  to whatever was focused before it opened. */
export function Modal({
  open, onClose, title, children, footer,
}: { open: boolean; onClose: () => void; title: string; children: ReactNode; footer?: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  const titleId = useId()
  // Keep the latest onClose without making it an effect dependency (it's often a fresh
  // closure each render, which would otherwise re-run the effect and steal focus).
  const onCloseRef = useRef(onClose)
  useEffect(() => { onCloseRef.current = onClose })

  useEffect(() => {
    if (!open) return
    const previouslyFocused = document.activeElement as HTMLElement | null

    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") { e.stopPropagation(); onCloseRef.current(); return }
      if (e.key !== "Tab") return
      const items = ref.current?.querySelectorAll<HTMLElement>(FOCUSABLE)
      if (!items || items.length === 0) return
      const first = items[0], last = items[items.length - 1]
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus() }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus() }
    }
    document.addEventListener("keydown", onKey)
    const t = window.setTimeout(() => ref.current?.querySelector<HTMLElement>(FOCUSABLE)?.focus(), 0)

    return () => {
      document.removeEventListener("keydown", onKey)
      window.clearTimeout(t)
      previouslyFocused?.focus?.()
    }
  }, [open])

  if (!open) return null
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/40 p-4" onClick={onClose}>
      <div ref={ref} role="dialog" aria-modal="true" aria-labelledby={titleId}
           className="w-full max-w-lg rounded-lg bg-white shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3">
          <h3 id={titleId} className="font-semibold">{title}</h3>
          <button onClick={onClose} aria-label="Close dialog" className="rounded p-1 text-muted hover:bg-slate-100"><X className="h-4 w-4" /></button>
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
