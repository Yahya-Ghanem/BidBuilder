/**
 * 25.1 — `<Money>` is the single source of truth for rendering a money value in
 * the UI. Solves the "AED 187,500.50 AED" class of bugs that arose from rendering
 * `money(value)` (which already prefixes the currency code) next to a separate
 * `{currency}` span. The component takes currency as a REQUIRED prop, so the
 * bug class cannot recur: there is no way to render an amount without telling
 * the component which currency it's in.
 *
 * The string-formatting helper `money()` in `lib/utils.ts` stays — it's still
 * needed in non-React contexts (alt text, ARIA labels, generated PDFs called
 * from JS, downloads). `<Money>` wraps it for the React render path.
 *
 * Optional `secondary` renders the FX-converted amount as a faded suffix, e.g.
 * `SAR 2,724,499.41 ≈ USD 726,533.18` — the only case where two currency
 * codes legitimately appear next to each other.
 */
import { formatCurrency, composeMoney } from "@/lib/format"
import { useI18n } from "@/lib/i18n"
import { cn } from "@/lib/utils"

export interface MoneyProps {
  /** The amount in the primary currency. */
  value: number
  /** ISO-4217 code (AED, USD, SAR, …). Required to prevent the dup-currency bug. */
  currency: string
  /**
   * Optional secondary-currency conversion. When set, renders the primary value
   * followed by `≈ {secondary.currency} {converted}` in a muted suffix.
   * Used by the estimate header's "Show in" feature (see estimate-editor.tsx).
   */
  secondary?: { value: number; currency: string }
  /** Render as a `<span>` (default) — set to "div" for block layout. */
  as?: "span" | "div"
  /** Pass through tabular-nums / font-weight / color from the caller. */
  className?: string
  /** Pass through a title attribute (tooltip). */
  title?: string
}

export function Money({ value, currency, secondary, as: As = "span", className, title }: MoneyProps) {
  const { locale } = useI18n()
  // ARIA + screen-reader use the composed string so the converted amount reads as one unit.
  const ariaLabel = composeMoney(value, currency, locale, secondary)
  return (
    <As className={cn("tabular-nums", className)} title={title} aria-label={ariaLabel}>
      {formatCurrency(value, currency, locale)}
      {secondary ? (
        <span className="ml-1 text-slate-500"> ≈ {formatCurrency(secondary.value, secondary.currency, locale)}</span>
      ) : null}
    </As>
  )
}
