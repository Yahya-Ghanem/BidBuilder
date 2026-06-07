"use client"
/**
 * 25.5 — Sticky stat-cards row with trend deltas + per-unit toggle.
 *
 * The bid-page stat strip used to be a plain grid of four-ish money values:
 * Direct cost · Indirect cost · Markups · Bid price. Two missing affordances
 * showed up in user research:
 *
 *   1. No indication of direction vs. the prior revision. Estimators iterate
 *      on a bid across revisions and constantly want to know "did this go up
 *      or down, and by how much, vs. the last revision". The server now ships
 *      a `previousDelta` on the breakdown; this component pipes each card's
 *      pct through a <DeltaBadge> with the right "lower-is-better" sign.
 *
 *   2. No way to see cost-per-m² (or cost-per-room, cost-per-key…). When a
 *      project has areas with a measurable quantity AND a defined unit (m²,
 *      room, etc.), dividing each card by that quantity is the single most-
 *      requested derived view. A toggle on the row applies it to all visible
 *      cards uniformly. We pick the divisor as the SUM of all top-level
 *      areas' quantities — that's the "total project footprint" and survives
 *      the common case of multiple buildings / plots tracked as siblings.
 *      The toggle is hidden when no top-level area has both a positive
 *      quantity AND a non-empty unit label: dividing by an unlabelled "unit"
 *      produces values nobody can interpret, so we just don't offer it.
 *
 * Persistence: the toggle state round-trips through `localStorage` keyed by
 * estimateId (`bb:estimate:{id}:perUnit` = "1"|"0"). That matches the
 * existing locale-preference pattern in `lib/i18n.tsx` and avoids a server
 * table for a display-only preference. It survives reload — which is what an
 * estimator reaching for the toggle expects — without paying for cross-device
 * sync that nobody asked for.
 */

import { useState, type ReactNode } from "react"
import type { Area, EstimateBreakdown, PreviousRevisionDelta } from "@/lib/types"
import { money } from "@/lib/utils"
import { useT } from "@/lib/i18n"
import { DeltaBadge, NewPill, Stat } from "./shared"

// localStorage key for the per-unit toggle, scoped per estimate. SSR-safe:
// every read/write is wrapped in a try/catch and a typeof-window guard.
const perUnitKey = (estimateId: number) => `bb:estimate:${estimateId}:perUnit`
function readPerUnit(estimateId: number): boolean {
  if (typeof window === "undefined") return false
  try { return window.localStorage.getItem(perUnitKey(estimateId)) === "1" } catch { return false }
}
function writePerUnit(estimateId: number, on: boolean) {
  if (typeof window === "undefined") return
  try { window.localStorage.setItem(perUnitKey(estimateId), on ? "1" : "0") } catch { /* private mode */ }
}

/**
 * The divisor and unit-label derived from a project's areas.
 *
 *   • `quantity` is the sum of all top-level areas' Quantity. Zero (i.e. no
 *     top-level area with a positive quantity) means "toggle hidden".
 *   • `unit` is the first top-level area's Unit, or empty string when none
 *     of them have a defined unit. We don't try to detect mixed units — if
 *     a tenant tags Tower A in m² and Tower B in keys, the result is in m²
 *     + keys, which is wrong, but it's the user's data choice not ours.
 *     The label still reads the first unit. An empty unit means we hide the
 *     toggle entirely rather than render the meaningless "per unit unit".
 */
export function divisorFromAreas(areas: Area[] | undefined) {
  if (!areas || areas.length === 0) return { quantity: 0, unit: "" }
  const topLevel = areas.filter((a) => a.parentAreaId == null)
  const quantity = topLevel.reduce((sum, a) => sum + (a.quantity || 0), 0)
  const unit = topLevel.find((a) => (a.unit ?? "").trim() !== "")?.unit?.trim() ?? ""
  return { quantity, unit }
}

/**
 * NOTE: the caller MUST render `<StatCards key={e.id} … />` so this component
 * remounts when the user switches revisions. The initial useState reads the
 * per-estimate preference once on mount — without the key, switching revisions
 * would leave the toggle state from the previous estimate "stuck".
 */
export function StatCards({ e, areas }: { e: EstimateBreakdown; areas: Area[] | undefined }) {
  const t = useT()
  const c = e.currency
  const { quantity: divisor, unit } = divisorFromAreas(areas)
  // 25.5 — Toggle visibility requires BOTH a positive divisor AND a non-empty
  // unit label. Showing "Per unit" with an unlabelled divisor produces values
  // an estimator can't interpret (m²? rooms? keys? plant?). When in doubt,
  // hide the affordance rather than offer an ambiguous one.
  const canPerUnit = divisor > 0 && unit !== ""
  const [perUnitState, setPerUnitState] = useState<boolean>(() => readPerUnit(e.id))
  // If the toggle was on but the user removes the divisor (deletes the only
  // top-level area), gracefully fall back to absolute view rather than silently
  // showing "—".
  const perUnit = canPerUnit && perUnitState

  function togglePerUnit() {
    const next = !perUnitState
    setPerUnitState(next)
    writePerUnit(e.id, next)
  }

  // Apply the divisor (or not) to a money value. We don't round here — Intl
  // formatting in <money> handles the display precision for the user's locale.
  const show = (v: number) => perUnit && divisor > 0 ? money(v / divisor, c) : money(v, c)
  const dpct: PreviousRevisionDelta | null = e.previousDelta
  const vsLabel = dpct ? `${t("ed.rev")} ${dpct.fromRevision}` : ""

  // 25.5 — Per-card delta slot. Three states:
  //   • dpct is null (this is revision 1, no comparison possible) → no slot.
  //   • dpct present but this card's pct is null (the prior revision had
  //     |value| < 0.005 — i.e. the card "first appeared" on this revision)
  //     → render a <NewPill>. Distinguishes "infinite change" from "no prior
  //     comparison" so the estimator isn't silently misled.
  //   • dpct + non-null pct → render a coloured <DeltaBadge>.
  function delta(pct: number | null | undefined, lowerIsBetter: boolean): ReactNode {
    if (!dpct) return undefined
    if (pct == null) return <NewPill vsLabel={vsLabel} />
    return <DeltaBadge pct={pct} lowerIsBetter={lowerIsBetter} vsLabel={vsLabel} />
  }

  return (
    <div className="sticky top-[100px] z-10 -mx-4 bg-slate-50/95 px-4 py-2 backdrop-blur sm:-mx-6 sm:px-6">
      {/* Toggle strip — only renders when there's a meaningful divisor AND
          a defined unit label. The label reads "Per m²" / "Per room" / etc.
          using the first top-level area's Unit. */}
      {canPerUnit && (
        <div className="mb-2 flex items-center justify-end gap-2 text-xs text-slate-600">
          <label className="inline-flex cursor-pointer items-center gap-2">
            <input
              type="checkbox"
              checked={perUnit}
              onChange={togglePerUnit}
              aria-label={t("stat.perUnit.aria", { unit })}
            />
            <span>{t("stat.perUnit.toggle", { unit })}</span>
            <span className="text-muted">({divisor.toLocaleString(undefined, { maximumFractionDigits: 2 })} {unit})</span>
          </label>
        </div>
      )}

      <div className="grid gap-4 sm:grid-cols-4">
        <Stat
          label={t("ed.stat.directCost")}
          value={show(e.directCost)}
          delta={delta(dpct?.directCostPct, /* lowerIsBetter */ true)}
        />
        <Stat
          label={t("ed.stat.indirect")}
          value={show(e.indirectCost)}
          delta={delta(dpct?.indirectCostPct, true)}
        />
        {/* 25.5 — Markups are the contractor's MARGIN envelope (overhead +
            profit + contingency that goes INTO the bid), not a cost the user
            wants to minimise. Higher-is-better, same as the bid card. */}
        <Stat
          label={t("ed.stat.markups")}
          value={show(e.markupCost)}
          delta={delta(dpct?.markupCostPct, /* lowerIsBetter */ false)}
        />
        <Stat
          label={e.taxAmount > 0 ? t("ed.stat.bidExclTax") : t("ed.stat.bidPrice")}
          value={show(e.bidPrice)}
          highlight={e.taxAmount <= 0}
          delta={delta(dpct?.bidPricePct, false)}
        />
        {e.taxAmount > 0 && (
          <Stat
            label={t("ed.stat.vat", { pct: e.taxRatePct ?? 0 })}
            value={show(e.taxAmount)}
          />
        )}
        {e.taxAmount > 0 && (
          <Stat
            label={t("ed.stat.totalInclTax")}
            value={show(e.bidPriceInclTax)}
            highlight
            delta={delta(dpct?.bidPriceInclTaxPct, false)}
          />
        )}
        {e.alternatesTotal > 0 && (
          <Stat label={t("ed.stat.alternates")} value={show(e.alternatesTotal)} />
        )}
        {e.commercialAdjustment !== 0 && (
          <Stat label={t("ed.stat.commercialAdj")} value={show(e.commercialAdjustment)} />
        )}
      </div>
    </div>
  )
}
