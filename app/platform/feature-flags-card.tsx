"use client"

import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Flag } from "lucide-react"
import { toast } from "sonner"
import { platformApi } from "@/lib/platform"
import { Card, Button, Input, TableScroll } from "@/components/ui"

/**
 * 29.B.1 — Platform admin UI over the feature-flag catalogue. SuperAdmin only
 * (the page already gates entry); each flag row exposes the three controls the
 * resolver respects: Enabled (master kill switch), Rollout % (gradual
 * rollout), Overrides (per-tenant pinning). Edits PUT through
 * <c>/api/feature-flags/{key}</c> which invalidates the API's catalogue cache
 * — toggle propagation is "&lt; 60s without a redeploy" per the 29.B.1 spec
 * (the 30s server cache + the SPA's 60s staleTime + focus-refetch is the
 * combined ceiling).
 */

interface FeatureFlagDto {
  id: number
  key: string
  description: string
  enabled: boolean
  rolloutPercentage: number
  overrides: Record<string, boolean>
  createdAt: string
  updatedAt: string
}

interface FlagInput {
  description?: string
  enabled?: boolean
  rolloutPercentage?: number
  overrides?: Record<string, boolean>
}

export function FeatureFlagsCard() {
  const qc = useQueryClient()
  const { data, isLoading, error } = useQuery({
    queryKey: ["platform-feature-flags"],
    queryFn: () => platformApi<FeatureFlagDto[]>("/api/platform/feature-flags"),
  })

  const save = useMutation({
    mutationFn: ({ key, body }: { key: string; body: FlagInput }) =>
      platformApi<FeatureFlagDto>(`/api/platform/feature-flags/${key}`, {
        method: "PUT",
        body: JSON.stringify(body),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["platform-feature-flags"] })
      toast.success("Flag updated")
    },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Card className="mt-6 p-5">
      <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-slate-600">
        <Flag className="h-4 w-4" /> Feature flags
        <span className="ml-auto text-xs font-normal text-slate-500">
          Toggle propagates in &lt; 60 s without a redeploy
        </span>
      </div>
      {isLoading && <p className="text-muted">Loading…</p>}
      {error && <p className="text-rose-600">{(error as Error).message}</p>}
      {data && data.length === 0 && (
        <p className="text-muted">No flags defined.</p>
      )}
      {data && data.length > 0 && (
        <TableScroll>
          <table className="w-full min-w-[40rem] text-sm">
            <thead>
              <tr className="text-left text-xs uppercase tracking-wide text-muted">
                <th className="py-2">Key</th>
                <th className="py-2">Description</th>
                <th className="py-2 text-center">Enabled</th>
                <th className="py-2 text-right">Rollout %</th>
                <th className="py-2 text-right">Overrides</th>
              </tr>
            </thead>
            <tbody>
              {data.map((f) => (
                <FlagRow
                  key={f.key}
                  flag={f}
                  busy={save.isPending}
                  onSave={(body) => save.mutate({ key: f.key, body })}
                />
              ))}
            </tbody>
          </table>
        </TableScroll>
      )}
    </Card>
  )
}

function FlagRow({
  flag,
  busy,
  onSave,
}: {
  flag: FeatureFlagDto
  busy: boolean
  onSave: (body: FlagInput) => void
}) {
  const [rollout, setRollout] = useState(String(flag.rolloutPercentage))
  const overrideCount = Object.keys(flag.overrides).length
  return (
    <tr className="border-t border-[var(--border)]">
      <td className="py-2 font-mono text-xs text-slate-700">{flag.key}</td>
      <td className="py-2 text-slate-600">{flag.description}</td>
      <td className="py-2 text-center">
        <input
          type="checkbox"
          aria-label={`Enable ${flag.key}`}
          checked={flag.enabled}
          disabled={busy}
          onChange={(e) => onSave({ enabled: e.target.checked })}
        />
      </td>
      <td className="py-2 text-right">
        <div className="flex items-center justify-end gap-2">
          <Input
            type="number"
            inputMode="numeric"
            min={0}
            max={100}
            className="w-20 text-right"
            aria-label={`Rollout percentage for ${flag.key}`}
            value={rollout}
            onChange={(e) => setRollout(e.target.value)}
            disabled={busy}
          />
          <Button
            variant="outline"
            className="px-2.5 py-1 text-xs"
            disabled={busy || rollout === String(flag.rolloutPercentage)}
            onClick={() => {
              const n = Number(rollout)
              if (!Number.isFinite(n) || n < 0 || n > 100) {
                toast.error("Rollout must be 0–100")
                return
              }
              onSave({ rolloutPercentage: n })
            }}
          >
            Save
          </Button>
        </div>
      </td>
      <td className="py-2 text-right text-slate-500">
        {overrideCount > 0 ? `${overrideCount} tenant${overrideCount === 1 ? "" : "s"}` : "—"}
      </td>
    </tr>
  )
}
