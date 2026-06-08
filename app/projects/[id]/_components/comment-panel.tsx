"use client"

import { useEffect, useId, useRef, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { Check, X, Trash2, MessageSquare, RotateCcw } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { useT } from "@/lib/i18n"
import { useAuth } from "@/lib/auth"
import { Button } from "@/components/ui"
import type { BoqLineComment } from "@/lib/types"

/** Compact relative time — same idiom as the notifications bell. */
function ago(iso: string) {
  const s = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000))
  if (s < 60) return "just now"
  if (s < 3600) return `${Math.floor(s / 60)}m`
  if (s < 86400) return `${Math.floor(s / 3600)}h`
  if (s < 604800) return `${Math.floor(s / 86400)}d`
  return new Date(iso).toLocaleDateString()
}

/**
 * 27.1 — Per-BOQ-line comment thread (slide-out side panel). Opens from the
 * comment-icon column in the BOQ DataTable. Anyone with `boq.view` can read +
 * post; only the comment author OR a tenant admin can delete (the endpoint
 * enforces this and returns 404 to avoid leaking existence).
 *
 * Resolving a comment drops it out of the row's open-count badge but keeps
 * the audit trail visible — the spec wants reviewers to see the conversation
 * after the issue has been addressed.
 *
 * @mention selection UI is intentionally NOT included in this first cut: the
 * endpoint accepts a `mentionedUserIds` array (filtered server-side against
 * the project audience) and is fully covered by BoqLineCommentTests; the
 * selector UI lands as a follow-up enhancement.
 */
export function CommentPanel({
  estimateId, itemId, itemDescription, locked, onClose,
}: {
  estimateId: number
  itemId: number
  itemDescription: string
  locked: boolean
  onClose: () => void
}) {
  const t = useT()
  const qc = useQueryClient()
  const { user } = useAuth()
  const isAdmin = user?.role === "TenantAdmin" || user?.role === "SuperAdmin"
  const [body, setBody] = useState("")

  // 27.x — focus management for a screen-reader-usable dialog. On open we move
  // focus into the drawer (close button — least destructive landing spot); on
  // unmount we restore focus to the element that opened the drawer (the BOQ row
  // comment icon). Escape closes the drawer. aria-modal advertises the trap.
  const closeBtnRef = useRef<HTMLButtonElement | null>(null)
  const composerId = useId()
  useEffect(() => {
    const opener = (typeof document !== "undefined" ? document.activeElement : null) as HTMLElement | null
    closeBtnRef.current?.focus()
    const onKey = (ev: KeyboardEvent) => { if (ev.key === "Escape") { ev.stopPropagation(); onClose() } }
    window.addEventListener("keydown", onKey)
    return () => { window.removeEventListener("keydown", onKey); opener?.focus?.() }
  }, [onClose])

  const threadKey = ["estimate", estimateId, "comments", itemId]
  const countsKey = ["estimate", estimateId, "comment-counts"]

  const thread = useQuery({
    queryKey: threadKey,
    queryFn: () => fetchApi<BoqLineComment[]>(`/api/estimates/${estimateId}/items/${itemId}/comments`),
  })

  const refreshAll = () => {
    qc.invalidateQueries({ queryKey: threadKey })
    qc.invalidateQueries({ queryKey: countsKey })
  }

  const add = useMutation({
    mutationFn: () => fetchApi<BoqLineComment>(`/api/estimates/${estimateId}/items/${itemId}/comments`, {
      method: "POST", body: JSON.stringify({ body, parentCommentId: null, mentionedUserIds: [] }),
    }),
    onSuccess: () => { setBody(""); refreshAll() },
    onError: (e) => toast.error((e as Error).message),
  })

  const resolve = useMutation({
    mutationFn: ({ cid, becomeResolved }: { cid: number; becomeResolved: boolean }) =>
      fetchApi<BoqLineComment>(`/api/estimates/${estimateId}/items/${itemId}/comments/${cid}/resolve?resolved=${becomeResolved}`,
        { method: "POST", body: "{}" }),
    onSuccess: refreshAll,
    onError: (e) => toast.error((e as Error).message),
  })

  const del = useMutation({
    mutationFn: (cid: number) => fetchApi(`/api/estimates/${estimateId}/items/${itemId}/comments/${cid}`, { method: "DELETE" }),
    onSuccess: refreshAll,
    onError: (e) => toast.error((e as Error).message),
  })

  function canDelete(c: BoqLineComment) {
    return isAdmin || c.authorUserId === user?.id
  }

  const list = thread.data ?? []
  const composerDisabled = locked || !body.trim() || body.length > 4000 || add.isPending

  return (
    <>
      {/* Backdrop (click to close, screen-reader hidden) */}
      <div className="fixed inset-0 z-40 bg-black/20" onClick={onClose} aria-hidden />
      {/* Drawer — logical `end-0` so it docks on the right in LTR and left in RTL. */}
      <aside
        role="dialog"
        aria-modal="true"
        aria-label={t("ed.comments.panelLabel")}
        className="fixed inset-y-0 end-0 z-50 flex w-[28rem] max-w-[calc(100vw-1rem)] flex-col border-s border-[var(--border)] bg-white shadow-xl"
      >
        <header className="flex items-start justify-between gap-2 border-b border-[var(--border)] px-4 py-3">
          <div className="min-w-0">
            <h2 className="flex items-center gap-2 text-sm font-semibold text-slate-700">
              <MessageSquare className="h-4 w-4 text-[var(--brand)]" />
              {t("ed.comments.title")}
            </h2>
            <p className="truncate text-xs text-muted" title={itemDescription}>{itemDescription}</p>
          </div>
          <button ref={closeBtnRef} onClick={onClose} aria-label={t("common.close")} className="rounded p-1 text-muted hover:bg-slate-100">
            <X className="h-4 w-4" />
          </button>
        </header>

        <div className="flex-1 overflow-auto px-4 py-3">
          {thread.isLoading ? (
            <p className="text-center text-sm text-muted">{t("common.loading")}</p>
          ) : !list.length ? (
            <p className="text-center text-sm text-muted">{t("ed.comments.empty")}</p>
          ) : (
            <ul className="space-y-3">
              {list.map((c) => {
                const resolved = c.resolvedAt != null
                return (
                  <li key={c.id} className={`rounded-md border border-[var(--border)] p-2.5 text-sm ${resolved ? "bg-slate-50 opacity-75" : "bg-white"}`}>
                    <div className="flex items-center justify-between gap-2 text-xs">
                      <span className="font-medium text-slate-700">{c.authorName || c.authorEmail}</span>
                      <span className="text-muted">{ago(c.createdAt)}</span>
                    </div>
                    <p className={`mt-1 whitespace-pre-wrap text-sm ${resolved ? "text-slate-500 line-through decoration-slate-400" : "text-slate-800"}`}>{c.body}</p>
                    <div className="mt-2 flex items-center gap-1">
                      {resolved ? (
                        <button
                          onClick={() => resolve.mutate({ cid: c.id, becomeResolved: false })}
                          disabled={resolve.isPending}
                          className="flex items-center gap-1 rounded px-1.5 py-0.5 text-xs text-muted hover:bg-slate-100"
                        >
                          <RotateCcw className="h-3 w-3" /> {t("ed.comments.reopen")}
                        </button>
                      ) : (
                        <button
                          onClick={() => resolve.mutate({ cid: c.id, becomeResolved: true })}
                          disabled={resolve.isPending}
                          className="flex items-center gap-1 rounded px-1.5 py-0.5 text-xs text-[var(--brand)] hover:bg-[var(--brand)]/10"
                        >
                          <Check className="h-3 w-3" /> {t("ed.comments.resolve")}
                        </button>
                      )}
                      {canDelete(c) && (
                        <button
                          onClick={() => { if (confirm(t("ed.comments.deleteConfirm"))) del.mutate(c.id) }}
                          disabled={del.isPending}
                          aria-label={t("common.delete")}
                          className="ms-auto rounded p-1 text-muted hover:bg-danger-soft hover:text-danger"
                        >
                          <Trash2 className="h-3 w-3" />
                        </button>
                      )}
                    </div>
                  </li>
                )
              })}
            </ul>
          )}
        </div>

        <footer className="border-t border-[var(--border)] px-4 py-3">
          <label htmlFor={composerId} className="block text-xs font-medium text-slate-600">{t("ed.comments.composerLabel")}</label>
          <textarea
            id={composerId}
            value={body}
            onChange={(ev) => setBody(ev.target.value)}
            rows={3}
            maxLength={4000}
            placeholder={t("ed.comments.placeholder")}
            className="mt-1 block w-full rounded-md border border-[var(--border)] px-2 py-1.5 text-sm outline-none focus:border-[var(--brand)]"
            disabled={locked}
          />
          <div className="mt-2 flex items-center justify-between text-xs text-muted">
            <span>{body.length} / 4000</span>
            <Button onClick={() => add.mutate()} disabled={composerDisabled} className="h-8 px-3 text-sm">
              {add.isPending ? t("common.loading") : t("ed.comments.post")}
            </Button>
          </div>
        </footer>
      </aside>
    </>
  )
}
