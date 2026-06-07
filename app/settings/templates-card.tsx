"use client"

import { useRef } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { FileStack, Trash2, Star, Download, Upload } from "lucide-react"
import { fetchApi, downloadFile } from "@/lib/api"
import type { EstimateTemplate } from "@/lib/types"
import { usePermissions } from "@/lib/permissions"
import { Card, Button } from "@/components/ui"
import { cn } from "@/lib/utils"
import { useT } from "@/lib/i18n"

/**
 * 21.3 → 22.3 — Manage saved estimate templates as a library. Templates are created from
 * an estimate ("Save as template" on the project page); this card lists them with their
 * category/tags, lets a tenant admin pin a template as "featured" for the org (sorts to the
 * top of the picker), and lets an estimate-admin delete unwanted ones.
 */
export function TemplatesCard() {
  const t = useT()
  const qc = useQueryClient()
  const { can, isAdmin } = usePermissions()
  const canView = can("estimate-admin", "view")
  const canAdd = can("estimate-admin", "add")
  const canDelete = can("estimate-admin", "delete")
  const fileRef = useRef<HTMLInputElement | null>(null)
  const list = useQuery({ queryKey: ["estimate-templates"], queryFn: () => fetchApi<EstimateTemplate[]>("/api/estimate-templates"), enabled: canView })

  // 24.3 — Import a previously-exported envelope. We read the file as text in the
  // browser, parse it (to give a clean error on bad JSON), and POST as application/json.
  // No multipart upload — the envelope is small and structured.
  const importMut = useMutation({
    mutationFn: async (file: File) => {
      const text = await file.text()
      let envelope: unknown
      try { envelope = JSON.parse(text) }
      catch { throw new Error("Not a valid JSON file.") }
      return fetchApi<EstimateTemplate>("/api/estimate-templates/import", {
        method: "POST", body: JSON.stringify(envelope),
      })
    },
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["estimate-templates"] }); toast.success("Template imported") },
    onError: (e) => toast.error((e as Error).message),
  })

  async function exportOne(id: number, name: string) {
    const safe = name.replace(/[^A-Za-z0-9_\- ]/g, "").trim().replace(/\s+/g, "-")
    try { await downloadFile(`/api/estimate-templates/${id}/export.json`, `${safe || `template-${id}`}.json`) }
    catch (e) { toast.error((e as Error).message) }
  }

  const del = useMutation({
    mutationFn: (id: number) => fetchApi(`/api/estimate-templates/${id}`, { method: "DELETE" }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["estimate-templates"] }); toast.success("Template deleted") },
    onError: (e) => toast.error((e as Error).message),
  })
  const feature = useMutation({
    mutationFn: ({ id, featured }: { id: number; featured: boolean }) =>
      fetchApi(`/api/estimate-templates/${id}/featured`, { method: "PUT", body: JSON.stringify({ featured }) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["estimate-templates"] }),
    onError: (e) => toast.error((e as Error).message),
  })

  if (!canView) return null

  return (
    <Card className="space-y-4 p-5">
      <div className="flex items-start justify-between gap-2">
        <div>
          <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600">
            <FileStack className="h-4 w-4 text-[var(--brand)]" /> {t("adm.tpl.heading")}
          </h3>
          <p className="text-xs text-slate-400">
            Reusable estimate structures. Create one with <b>Save as template</b> on a project&apos;s
            estimate, then start new estimates from it. Deleting a template does not affect estimates already created from it.
          </p>
        </div>
        {canAdd && (
          <>
            <input ref={fileRef} type="file" accept="application/json,.json" className="hidden"
              onChange={(ev) => { const f = ev.target.files?.[0]; if (f) importMut.mutate(f); ev.target.value = "" }} />
            <Button variant="outline" className="h-8 shrink-0 text-xs" disabled={importMut.isPending}
              onClick={() => fileRef.current?.click()} title="Import a template exported by another workspace">
              <Upload className="h-3.5 w-3.5" /> {importMut.isPending ? "Importing…" : "Import"}
            </Button>
          </>
        )}
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
                <div className="flex items-center gap-2">
                  {t.isFeatured && <Star className="h-3.5 w-3.5 fill-amber-400 text-amber-400" />}
                  <span className="truncate text-sm text-slate-700">{t.name}</span>
                  {t.category && <span className="rounded bg-sky-100 px-1.5 text-xs font-medium text-sky-700">{t.category}</span>}
                </div>
                <div className="mt-0.5 flex flex-wrap items-center gap-1">
                  {t.tags.map((g) => <span key={g} className="rounded bg-slate-100 px-1.5 text-xs text-slate-500">{g}</span>)}
                </div>
                <div className="mt-0.5 text-xs text-slate-400">
                  {t.sectionCount} sections · {t.itemCount} items{t.createdByName ? ` · by ${t.createdByName}` : ""}{t.description ? ` · ${t.description}` : ""}
                </div>
              </div>
              <div className="flex items-center gap-1">
                <Button variant="outline" className="h-7 text-xs text-slate-600 hover:bg-slate-50"
                  onClick={() => exportOne(t.id, t.name)} title="Export as JSON to share with another workspace">
                  <Download className="h-3.5 w-3.5" /> Export
                </Button>
                {isAdmin && (
                  <Button variant="outline" className={cn("h-7 text-xs", t.isFeatured ? "text-amber-600" : "text-slate-500")}
                    disabled={feature.isPending}
                    onClick={() => feature.mutate({ id: t.id, featured: !t.isFeatured })}
                    title={t.isFeatured ? "Unfeature" : "Feature for your org"}>
                    <Star className={cn("h-3.5 w-3.5", t.isFeatured && "fill-amber-400 text-amber-400")} />
                    {t.isFeatured ? "Featured" : "Feature"}
                  </Button>
                )}
                {canDelete && (
                  <Button variant="outline" className="h-7 text-xs text-rose-600 hover:bg-rose-50" disabled={del.isPending}
                    onClick={() => { if (confirm(`Delete template "${t.name}"?`)) del.mutate(t.id) }}>
                    <Trash2 className="h-3.5 w-3.5" /> Delete
                  </Button>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}
