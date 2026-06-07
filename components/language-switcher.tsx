"use client"

import { Globe } from "lucide-react"
import { useI18n } from "@/lib/i18n"
import { cn } from "@/lib/utils"

/** 21.4 — Toggle the UI language between English and Arabic (which flips the whole
 *  layout to RTL). The choice persists in localStorage. */
export function LanguageSwitcher({ className }: { className?: string }) {
  const { locale, setLocale, t } = useI18n()
  const next = locale === "en" ? "ar" : "en"
  return (
    <button
      type="button"
      onClick={() => setLocale(next)}
      title={t("lang.label")}
      aria-label={t("lang.label")}
      className={cn(
        "flex items-center gap-1.5 rounded-md px-2 py-1.5 text-sm text-slate-600 hover:bg-slate-100",
        className,
      )}
    >
      <Globe className="h-4 w-4" />
      <span className="font-medium">{locale === "en" ? "ع" : "EN"}</span>
    </button>
  )
}
