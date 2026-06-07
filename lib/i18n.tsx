"use client"

import { createContext, useContext, useEffect, useState, type ReactNode } from "react"
import { type Locale, LOCALES, isRtl, translate } from "./i18n-core"

// 21.4 — Lightweight client-side i18n (en + ar with full RTL). Deliberately NOT
// locale-routed (no /en, /ar paths): the app is a client-rendered workspace, so a
// context-driven dictionary + a <html dir/lang> switch delivers Arabic + RTL with no
// routing/middleware refactor. The dictionaries + pure translator live in i18n-core.

export { type Locale, LOCALES, isRtl, translate, messages } from "./i18n-core"

const STORAGE_KEY = "bb_locale"

type I18nValue = {
  locale: Locale
  dir: "ltr" | "rtl"
  setLocale: (l: Locale) => void
  t: (key: string, vars?: Record<string, string | number>) => string
}

const I18nContext = createContext<I18nValue>({
  locale: "en",
  dir: "ltr",
  setLocale: () => {},
  t: (key) => translate("en", key),
})

export function I18nProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<Locale>("en")

  // Rehydrate the saved choice on mount (both SSR + first client render are "en", so no mismatch).
  useEffect(() => {
    try {
      const saved = localStorage.getItem(STORAGE_KEY) as Locale | null
      if (saved && LOCALES.includes(saved)) setLocaleState(saved)
    } catch { /* ignore */ }
  }, [])

  // Reflect the locale onto the document so the whole app flips direction + language.
  useEffect(() => {
    document.documentElement.lang = locale
    document.documentElement.dir = isRtl(locale) ? "rtl" : "ltr"
  }, [locale])

  const setLocale = (l: Locale) => {
    setLocaleState(l)
    try { localStorage.setItem(STORAGE_KEY, l) } catch { /* ignore */ }
  }

  const value: I18nValue = {
    locale,
    dir: isRtl(locale) ? "rtl" : "ltr",
    setLocale,
    t: (key, vars) => translate(locale, key, vars),
  }
  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>
}

export function useI18n(): I18nValue {
  return useContext(I18nContext)
}

/** Convenience hook for components that only need the translator. */
export function useT() {
  return useI18n().t
}
