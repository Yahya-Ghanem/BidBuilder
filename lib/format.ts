// 22.4 — Locale-aware formatters for numbers, currency and dates. Pure (no React) so
// they're unit-testable in the node vitest env, and callable from anywhere.
//
// Design decision — ALWAYS Western (Latin) digits, even under Arabic. Business figures
// in bids/estimates must be unambiguous and copy-pasteable across locales, so we force
// `numberingSystem: 'latn'` rather than letting ar-* render Arabic-Indic digits (٠١٢…).
// What DOES localize is grouping/decimal separators, currency-symbol placement, and the
// date field order — i.e. presentation, not the glyphs of the numerals themselves.

import type { Locale } from "./i18n-core"

/** Map our app locale to a concrete BCP-47 tag for Intl, pinned to Latin digits. */
function intlLocale(locale: Locale): string {
  // ar-AE keeps Gulf conventions (the app's default currency is AED) but with -nu-latn.
  return locale === "ar" ? "ar-AE-u-nu-latn" : "en-US-u-nu-latn"
}

/** Format a plain number with locale grouping (Western digits). */
export function formatNumber(value: number, locale: Locale = "en", opts: Intl.NumberFormatOptions = {}): string {
  return new Intl.NumberFormat(intlLocale(locale), { numberingSystem: "latn", ...opts }).format(value)
}

/** Format a money amount with the given ISO-4217 currency (2dp, Western digits). */
export function formatCurrency(value: number, currency = "AED", locale: Locale = "en"): string {
  return new Intl.NumberFormat(intlLocale(locale), {
    style: "currency",
    currency,
    numberingSystem: "latn",
    minimumFractionDigits: 2,
  }).format(value)
}

/** Format a date (Date or ISO string) in the locale's medium style (Western digits).
 *  Returns the input string unchanged if it isn't a parseable date. */
export function formatDate(value: string | Date, locale: Locale = "en", opts: Intl.DateTimeFormatOptions = { dateStyle: "medium" }): string {
  const d = value instanceof Date ? value : new Date(value)
  if (Number.isNaN(d.getTime())) return typeof value === "string" ? value : ""
  return new Intl.DateTimeFormat(intlLocale(locale), { numberingSystem: "latn", ...opts }).format(d)
}
