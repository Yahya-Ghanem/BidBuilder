import { clsx, type ClassValue } from "clsx"
import { twMerge } from "tailwind-merge"
import { formatCurrency } from "./format"
import type { Locale } from "./i18n-core"

/** Merge Tailwind class names, resolving conflicts. */
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

/** Format a number as currency for the bid UI. Defaults to English presentation
 *  (callers inside a localized view can pass the active locale). 22.4 routes this
 *  through the shared locale-aware formatter (Western digits, locale separators). */
export function money(value: number, currency = "AED", locale: Locale = "en") {
  return formatCurrency(value, currency, locale)
}
