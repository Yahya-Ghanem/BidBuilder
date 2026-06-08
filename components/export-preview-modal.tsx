"use client"

import { useEffect, useId, useRef, useState } from "react"
import { X } from "lucide-react"
import { Button } from "@/components/ui"
import { downloadFile, fetchObjectUrl } from "@/lib/api"
import { useT } from "@/lib/i18n"

/** Which export lane is being previewed — controls the mime/iframe behaviour
 *  and the download filename. The endpoint path is provided by the caller so a
 *  single modal can preview any export the editor supports (the editor passes
 *  `/api/estimates/{id}/export.{kind}`, but this component never has to know
 *  the id shape). */
export type ExportPreviewKind = "pdf" | "xlsx" | "csv"

interface Props {
  open: boolean
  /** Display name for the format ("PDF", "Excel", "CSV") shown in the dialog title. */
  kindLabel: string
  /** Which lane — affects how the URL gets built (PDF stays application/pdf in
   *  preview, xlsx/csv switch to text/html) and the download filename extension. */
  kind: ExportPreviewKind
  /** Path WITHOUT query string — e.g. `/api/estimates/42/export.pdf`. The
   *  modal appends `?preview=1` to fetch the preview and re-uses the same path
   *  bare for the Download click. */
  basePath: string
  /** Filename suggested for the download. */
  downloadName: string
  onClose: () => void
}

/**
 * 28.3 — Pre-download preview modal. The acceptance bar is "user spots a wrong
 * figure before sending a broken document to the client", and the design crux
 * is that the preview is a SEPARATE fetch from the download — clicking Close
 * dismisses the modal without ever calling the download path. Clicking
 * Download triggers the normal {@link downloadFile} helper.
 *
 * The preview body is loaded via {@link fetchObjectUrl} (auth header + tenant
 * header attached) and rendered in an `<iframe>` — PDFs render in the browser's
 * built-in viewer, the HTML preview lane (xlsx/csv) renders as a styled page.
 * Object URLs are revoked on unmount so a flurry of preview-and-close events
 * doesn't accumulate ~50 KB blobs per click.
 */
export function ExportPreviewModal({ open, kindLabel, kind, basePath, downloadName, onClose }: Props) {
  const t = useT()
  const titleId = useId()
  const [objectUrl, setObjectUrl] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [downloading, setDownloading] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  // Hold onClose in a ref so the focus-trap effect doesn't re-run when the
  // parent passes a fresh closure — matches the Modal primitive's pattern.
  const onCloseRef = useRef(onClose)
  useEffect(() => { onCloseRef.current = onClose })

  // ── Fetch the preview body when the modal opens ───────────────────────────
  // Each open spawns a single request; we revoke the object URL on close /
  // unmount so blobs don't leak. The early `return` (no setState in body when
  // closed) keeps the eslint react-hooks/set-state-in-effect rule happy —
  // re-opening resets `objectUrl`/`error` because the inner DOM was never
  // re-rendered with stale values (the wrapper returns null when !open).
  useEffect(() => {
    if (!open) return
    let cancelled = false
    let createdUrl: string | null = null
    void (async () => {
      try {
        const url = await fetchObjectUrl(`${basePath}?preview=1`)
        if (cancelled) { URL.revokeObjectURL(url); return }
        createdUrl = url
        setObjectUrl(url)
      } catch (err) {
        if (!cancelled) setError((err as Error).message || "Preview failed")
      }
    })()
    return () => {
      cancelled = true
      if (createdUrl) URL.revokeObjectURL(createdUrl)
      // Reset the local state so the next open shows the loading state again
      // rather than a stale URL/error from a previous run.
      setObjectUrl(null)
      setError(null)
    }
  }, [open, basePath])

  // ── Focus trap + Escape + outside-click ───────────────────────────────────
  useEffect(() => {
    if (!open) return
    const previouslyFocused = document.activeElement as HTMLElement | null
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") { e.stopPropagation(); onCloseRef.current(); return }
      if (e.key !== "Tab") return
      const items = ref.current?.querySelectorAll<HTMLElement>('a[href],button:not([disabled]),iframe,[tabindex]:not([tabindex="-1"])')
      if (!items || items.length === 0) return
      const first = items[0], last = items[items.length - 1]
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus() }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus() }
    }
    document.addEventListener("keydown", onKey)
    // Focus the Close button on open so Escape is mouseless-friendly.
    const t = window.setTimeout(() => ref.current?.querySelector<HTMLElement>('[data-testid="export-preview-close"]')?.focus(), 0)
    return () => {
      document.removeEventListener("keydown", onKey)
      window.clearTimeout(t)
      previouslyFocused?.focus?.()
    }
  }, [open])

  if (!open) return null

  async function handleDownload() {
    setDownloading(true)
    try {
      await downloadFile(basePath, downloadName)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setDownloading(false)
    }
  }

  return (
    <div
      className="fixed inset-0 z-50 grid place-items-center bg-black/40 p-4"
      onClick={onClose}
      data-testid="export-preview-modal"
    >
      <div
        ref={ref}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        // Wider than the form Modal — a preview needs room. Caps at ~5xl so
        // the bid summary table reads at a normal page width on a laptop.
        className="flex h-[85vh] w-full max-w-5xl flex-col rounded-lg bg-[var(--card)] text-[var(--text)] shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3">
          <h3 id={titleId} className="font-semibold">
            {t("ed.preview.title", { kind: kindLabel })}
          </h3>
          <button
            onClick={onClose}
            aria-label={t("ed.preview.close")}
            className="rounded p-1 text-muted hover:bg-[color-mix(in_oklab,var(--text)_8%,transparent)]"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
        <div className="relative flex-1 overflow-hidden bg-[color-mix(in_oklab,var(--text)_4%,transparent)]">
          {!objectUrl && !error && (
            <p className="grid h-full place-items-center text-sm text-muted" data-testid="export-preview-loading">
              {t("ed.preview.loading")}
            </p>
          )}
          {error && (
            <p className="grid h-full place-items-center px-4 text-center text-sm text-rose-600" data-testid="export-preview-error">
              {t("ed.preview.failed", { error })}
            </p>
          )}
          {objectUrl && (
            <iframe
              // The browser renders PDFs natively in iframes (Chrome/Edge/Firefox/
              // Safari) and HTML pages always; the preview endpoint returns
              // application/pdf or text/html depending on lane, so the same
              // iframe element handles both.
              src={objectUrl}
              title={t("ed.preview.frameLabel")}
              className="h-full w-full border-0 bg-white"
              data-testid="export-preview-iframe"
              data-kind={kind}
            />
          )}
        </div>
        <div className="flex justify-end gap-2 border-t border-[var(--border)] px-5 py-3">
          <Button type="button" variant="outline" onClick={onClose} data-testid="export-preview-close">
            {t("ed.preview.close")}
          </Button>
          <Button
            type="button"
            onClick={handleDownload}
            disabled={downloading || !!error || !objectUrl}
            data-testid="export-preview-download"
          >
            {t("ed.preview.download")}
          </Button>
        </div>
      </div>
    </div>
  )
}
