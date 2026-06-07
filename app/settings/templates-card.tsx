"use client"

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { FileStack, Trash2 } from "lucide-react"
import { fetchApi } from "@/lib/api"
import type { EstimateTemplate } from "@/lib/types"
import { usePermissions } from "@/lib/permissions"
import { Card, Button } from "@/components/ui"

/**
 * 21.3 — Manage saved estimate templates. Templates are created from an estimate
 * ("Save as template" on the project page); this card lists them and lets an
 * estimate-admin delete ones that are no longer needed.
 */
export function TemplatesCard() {
  const qc = useQueryClient()
  const { can } = usePermissions()
  const canView = can("estimate-admin", "view")
  const canDelete = can("estimate-admin", "delete")
  const list = useQuery({ queryKey: ["estimate-templates"], queryFn: () => fetchApi<EstimateTemplate[]>("/api/estimate-templates"), enabled: canView })

  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/estimate-templates/${id}`, { method: "DELETE" }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["estimate-templates"] }); toast.success("Template deleted") },
    onError: (e) => toast.error((e as Error).message),
  })

  if (!canView) return null

  return (
    <Card className="space-y-4 p-5">
      <div>
        <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600">
          <FileStack className="h-4 w-4 text-[var(--brand)]" /> Estimate templates
        </h3>
        <p className="text-xs text-slate-400">
          Reusable estimate structures. Create one with <b>Save as template</b> on a project&apos;s
          estimate, then start new estimates from it. Deleting a template does not affect estimates already created from it.
        </p>
      </div>

      {list.isLoading ? (
        <p className="text-sm text-slate-400">Loading…</p>
      ) : !list.data?.length ? (
        <p className="text-sm text-slate-400">No templates yet.</p>
      ) : (
        <ul className="space-y-2">
          {list.data.map((t) => (
            <li key={t.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-[var(--border)] px-3 py-2">
              <div className="min-w-0">
                <div className="truncate text-sm text-slate-700">{t.name}</div>
                <div className="mt-0.5 text-xs text-slate-400">
                  {t.sectionCount} sections · {t.itemCount} items{t.description ? ` · ${t.description}` : ""}
                </div>
              </div>
              {canDelete && (
                <Button variant="outline" className="h-7 text-xs text-rose-600 hover:bg-rose-50" disabled={del.isPending}
                  onClick={() => { if (confirm(`Delete template "${t.name}"?`)) del.mutate(t.id) }}>
                  <Trash2 className="h-3.5 w-3.5" /> Delete
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}
