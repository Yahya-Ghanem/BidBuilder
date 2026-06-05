"use client"

import { useQuery } from "@tanstack/react-query"
import { BarChart3, FolderKanban } from "lucide-react"
import { fetchApi } from "@/lib/api"
import { money } from "@/lib/utils"
import type { BenchmarkResult, BenchmarkUnitGroup } from "@/lib/types"
import { AppShell } from "@/components/app-shell"
import { usePermissions } from "@/lib/permissions"
import { Card, Badge, statusColor } from "@/components/ui"

export default function BenchmarksPage() {
  return (
    <AppShell title="Benchmarks">
      <Body />
    </AppShell>
  )
}

function Body() {
  const { can, isLoading: permLoading } = usePermissions()
  const { data, isLoading, error } = useQuery({ queryKey: ["benchmarks"], queryFn: () => fetchApi<BenchmarkResult>("/api/benchmarks") })

  if (permLoading) return <p className="text-slate-400">Loading…</p>
  if (!can("reports", "view")) return <p className="text-rose-600">You need the Reports permission to view benchmarks.</p>
  if (isLoading) return <p className="text-slate-400">Loading…</p>
  if (error) return <p className="text-rose-600">{(error as Error).message}</p>
  if (!data) return null

  return (
    <div className="max-w-4xl space-y-6">
      <p className="text-sm text-slate-500">
        Cost per unit/m² across your projects, from each project&apos;s representative estimate (latest published, else
        latest revision). Only areas that carry a measure (quantity + unit) appear. Mixed currencies are normalized to
        your base currency ({data.baseCurrency}) using the tenant rate table. Analytical only — bids are unchanged.
      </p>

      <ProjectsCard data={data} />

      {data.units.length === 0
        ? <Card className="p-5 text-sm text-slate-400">No area measures yet. Add a quantity + unit to project areas (on the project page) to benchmark cost per unit/m².</Card>
        : data.units.map((g) => <UnitGroupCard key={g.unit} g={g} />)}
    </div>
  )
}

function ProjectsCard({ data }: { data: BenchmarkResult }) {
  return (
    <Card className="space-y-3 p-5">
      <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600"><FolderKanban className="h-4 w-4" /> Projects</h3>
      <table className="w-full text-sm">
        <thead className="text-left text-xs text-slate-500">
          <tr><th className="py-1">Code</th><th className="py-1">Project</th><th className="py-1">Estimate</th><th className="py-1 text-right">Bid</th></tr>
        </thead>
        <tbody>
          {data.projects.map((p) => (
            <tr key={p.projectId} className="border-t border-[var(--border)]">
              <td className="py-2 font-mono text-xs">{p.code}</td>
              <td className="py-2 text-slate-700">{p.name}</td>
              <td className="py-2 text-xs text-slate-500">
                {p.estimateTitle
                  ? <>Rev {p.revision} {p.status && <Badge className={statusColor(p.status)}>{p.status}</Badge>}</>
                  : <span className="text-slate-400">no estimate</span>}
              </td>
              <td className="py-2 text-right font-medium">{money(p.bidPrice, p.currency)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  )
}

function UnitGroupCard({ g }: { g: BenchmarkUnitGroup }) {
  const base = g.baseCurrency
  const mixed = g.currencies.length > 1
  const excluded = g.count - g.convertibleCount      // points with no rate → not in the aggregate
  const hasAgg = g.min != null && g.avg != null && g.max != null
  return (
    <Card className="space-y-3 p-5">
      <div className="flex items-center justify-between">
        <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-600">
          <BarChart3 className="h-4 w-4" /> Cost per {g.unit}
          <span className="text-xs font-normal text-slate-400">· {g.count} area{g.count > 1 ? "s" : ""}</span>
        </h3>
        {hasAgg && (
          <div className="flex gap-3 text-xs text-slate-500">
            <span>min <b className="text-slate-700">{money(g.min!, base)}</b></span>
            <span>avg <b className="text-slate-700">{money(g.avg!, base)}</b></span>
            <span>max <b className="text-slate-700">{money(g.max!, base)}</b></span>
          </div>
        )}
      </div>
      {mixed && (
        <p className="text-xs text-slate-500">
          Multiple currencies ({g.currencies.join(", ")}) — aggregate normalized to <b>{base}</b> using your tenant currency rates.
        </p>
      )}
      {excluded > 0 && (
        <p className="text-xs text-amber-600">
          {excluded} area{excluded > 1 ? "s" : ""} excluded from the aggregate — no rate to {base} set.{" "}
          <a className="underline" href="/settings">Add currency rates in Settings.</a>
        </p>
      )}
      {!hasAgg && excluded === g.count && (
        <p className="text-xs text-amber-600">No aggregate — none of these currencies have a rate to {base}.</p>
      )}
      <table className="w-full text-sm">
        <thead className="text-left text-xs text-slate-500">
          <tr>
            <th className="py-1">Project</th><th className="py-1">Area</th>
            <th className="py-1 text-right">Quantity</th><th className="py-1 text-right">Total</th>
            <th className="py-1 text-right">Cost / {g.unit}</th>
            {mixed && <th className="py-1 text-right">≈ {base}</th>}
          </tr>
        </thead>
        <tbody>
          {g.points.map((pt, i) => (
            <tr key={`${pt.projectCode}-${pt.areaName}-${i}`} className="border-t border-[var(--border)]">
              <td className="py-2"><span className="font-mono text-xs text-slate-400">{pt.projectCode}</span></td>
              <td className="py-2 text-slate-700">{pt.areaName} <span className="text-xs text-slate-400">{pt.kind}</span></td>
              <td className="py-2 text-right tabular-nums">{pt.quantity} {pt.unit}</td>
              <td className="py-2 text-right tabular-nums text-slate-500">{money(pt.total, pt.currency)}</td>
              <td className="py-2 text-right font-medium tabular-nums">{money(pt.costPerUnit, pt.currency)}</td>
              {mixed && (
                <td className="py-2 text-right tabular-nums text-slate-500">
                  {pt.costPerUnitBase != null
                    ? money(pt.costPerUnitBase, base)
                    : <span className="text-amber-600" title={`No ${pt.currency}→${base} rate`}>—</span>}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  )
}
