import { describe, it, expect } from "vitest"
import { LOCALES, messages, isRtl, translate } from "./i18n-core"

describe("i18n dictionaries", () => {
  it("exposes en and ar", () => {
    expect(LOCALES).toEqual(["en", "ar"])
  })

  it("every locale defines exactly the same keys (no missing/extra translations)", () => {
    const enKeys = Object.keys(messages.en).sort()
    const arKeys = Object.keys(messages.ar).sort()
    expect(arKeys).toEqual(enKeys)
  })

  it("no translation value is blank", () => {
    for (const loc of LOCALES)
      for (const [k, v] of Object.entries(messages[loc]))
        expect(v.trim(), `${loc}.${k}`).not.toBe("")
  })
})

describe("translate", () => {
  it("returns the locale's string", () => {
    expect(translate("en", "login.signIn")).toBe("Sign in")
    expect(translate("ar", "login.signIn")).toBe("تسجيل الدخول")
  })

  it("falls back to the raw key when unknown", () => {
    expect(translate("ar", "does.not.exist")).toBe("does.not.exist")
  })

  it("interpolates {vars}", () => {
    // (No keyed string uses a var today, so exercise the mechanism directly via a key
    // that echoes back unchanged plus a var-bearing literal fallback.)
    expect(translate("en", "Hello {name}", { name: "Sam" })).toBe("Hello Sam")
  })
})

describe("isRtl", () => {
  it("flags arabic as RTL and english as LTR", () => {
    expect(isRtl("ar")).toBe(true)
    expect(isRtl("en")).toBe(false)
  })
})
