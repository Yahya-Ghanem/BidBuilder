"use client"
import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { CheckCircle2, ShieldCheck, Trash2 } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { useAuth } from "@/lib/auth"
import type { ApprovalsView } from "@/lib/types"
import { Card, Button } from "@/components/ui"

/**
 * 20.2 — Approval workflow panel for an estimate revision.
 *
 * Shows the current approvals (N of M required), lets a TenantAdmin record or
 * revoke their own sign-off, and highlights when the publish is now unblocked.
 *
 * The panel renders ONLY when the tenant has RequiredApprovalsToPublish > 0
 * (the parent gates this). When required is zero this is dead weight.
 */
export function ApprovalPanel({ estimateId, status }: { estimateId: number; status: string }) {
  const qc = useQueryClient()
  const { user } = useAuth()
  const isAdmin = user?.role === "TenantAdmin"
  const isFinalised = status === "Published" || status === "Superseded"

  const { data, isLoading } = useQuery<ApprovalsView>({
    queryKey: ["estimate-approvals", estimateId],
    queryFn: () => fetchApi<ApprovalsView>(`/api/estimates/${estimateId}/approvals`),
  })

  const [note, setNote] = useState("")
  const approve = useMutation({
    mutationFn: () => fetchApi<ApprovalsView>(`/api/estimates/${estimateId}/approvals`, {
      method: "POST", body: JSON.stringify({ note: note.trim() || null }),
    }),
    onSuccess: (v) => {
      qc.setQueryData(["estimate-approvals", estimateId], v)
      setNote("")
      toast.success(`Approval recorded (${v.currentApprovals}/${v.requiredApprovals})`)
    },
    onError: (e) => toast.error((e as Error).message),
  })
  const revoke = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/estimates/${estimateId}/approvals/${id}`, { method: "DELETE" }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["estimate-approvals", estimateId] })
      toast.success("Approval revoked")
    },
    onError: (e) => toast.error((e as Error).message),
  })

  if (isLoading || !data) return null
  // Hide the panel entirely if the tenant doesn't require approvals — there's
  // nothing to do and showing "0/0 approvals" would be confusing.
  if (data.requiredApprovals <= 0) return null

  const myApproval = user ? data.approvals.find((a) => a.approverUserId === user.id) : undefined
  const enough = data.currentApprovals >= data.requiredApprovals

  return (
    <Card className="p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <div className="flex items-center gap-2 text-sm font-semibold text-slate-700">
          <ShieldCheck className="h-4 w-4 text-[var(--brand)]" /> Approvals
          <span className={enough ? "ml-2 rounded bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700" : "ml-2 rounded bg-amber-50 px-2 py-0.5 text-xs text-amber-700"}>
            {data.currentApprovals} / {data.requiredApprovals}
          </span>
        </div>
        {enough
          ? <span className="text-xs text-emerald-700">Ready to publish</span>
          : <span className="text-xs text-slate-500">{data.requiredApprovals - data.currentApprovals} more needed</span>}
      </div>

      {data.approvals.length === 0 ? (
        <p className="text-xs text-slate-400">No sign-offs recorded yet.</p>
      ) : (
        <ul className="space-y-1 text-sm">
          {data.approvals.map((a) => (
            <li key={a.id} className="flex items-center justify-between rounded border border-[var(--border)] px-3 py-1.5">
              <span className="flex min-w-0 items-center gap-2">
                <CheckCircle2 className="h-3.5 w-3.5 shrink-0 text-emerald-500" />
                <span className="truncate text-slate-700">{a.approverName || a.approverEmail}</span>
                <span className="truncate text-xs text-slate-400">{new Date(a.approvedAt).toLocaleString()}</span>
                {a.note && <span className="truncate text-xs text-slate-500" title={a.note}>· {a.note}</span>}
              </span>
              {(user?.id === a.approverUserId || isAdmin) && (
                <button onClick={() => { if (confirm("Revoke this sign-off?")) revoke.mutate(a.id) }}
                        className="rounded p-1 text-slate-400 hover:bg-rose-50 hover:text-rose-600" title="Revoke">
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {/* Sign-off input. Only TenantAdmins can approve, and only Draft/UnderReview
          revisions accept new approvals (approving a finalised revision is a no-op). */}
      {isAdmin && !isFinalised && !myApproval && (
        <div className="mt-3 flex items-end gap-2 border-t border-[var(--border)] pt-3">
          <label className="flex-1 text-xs text-slate-500">
            Optional note
            <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000}
                   placeholder='e.g. "Reviewed prelims & margins, OK to submit"'
                   className="mt-1 w-full rounded-md border border-[var(--border)] px-2 py-1 text-sm outline-none focus:border-[var(--brand)]" />
          </label>
          <Button disabled={approve.isPending} onClick={() => approve.mutate()}>
            <CheckCircle2 className="h-4 w-4" /> {approve.isPending ? "Approving…" : "Approve"}
          </Button>
        </div>
      )}
      {isAdmin && !isFinalised && myApproval && (
        <p className="mt-2 text-xs text-slate-500">You&apos;ve already signed off — others must add their approval.</p>
      )}
      {!isAdmin && <p className="mt-2 text-xs text-slate-400">Only Tenant Admins can record approvals.</p>}
    </Card>
  )
}
